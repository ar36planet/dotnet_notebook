---
title: 39 Kubernetes HPA 與 VPA
tags: [kubernetes, hpa, vpa, autoscaling, microservices]
---

# 39 Kubernetes HPA 與 VPA

## 學習目標

- 分辨 horizontal scaling 和 vertical scaling。
- 分辨 resource、custom、external metrics 需要的 API/provider。
- 使用 `autoscaling/v2` 寫出有行為限制的 HPA。
- 理解 resource request、missing metrics、not-ready Pod 和 startup CPU 對 HPA 的影響。
- 用 VPA recommendation-only 觀察建議，避免 HPA 和 VPA 同時控制同一個 request。
- 把 RabbitMQ queue depth 接到完整的 custom/external metrics pipeline。

## 1. 一句話理解

HPA 調整 Pod replicas，VPA 調整單一 Pod 的 resource requests；兩者都依賴 metrics API、正確的 resource model 和可承受的 dependency capacity，不能只貼一份 YAML 就保證有效。

先看會出事的場景：HPA 設定 CPU utilization 目標，但 Deployment 的某個 container 沒有 CPU request，utilization 沒有分母；或 HPA 使用 CPU request 當分母，同時 VPA 又修改 CPU request，HPA 的判斷基準會一直變。另一種常見誤會是把 RabbitMQ queue depth 寫進 HPA，卻沒有 exporter、Prometheus、adapter 和 `external.metrics.k8s.io`，HPA 根本拿不到這個數字。

## 2. Kubernetes 與 Java 對照

Java Spring Boot 和 ASP.NET Core 在 HPA／VPA 上的主要差異是 application metrics 的產生方式；HPA、Metrics API、requests、provider 和 controller 的規則不因語言改變。Spring Boot 常用 Micrometer 暴露 Prometheus metrics，ASP.NET Core 可用 OpenTelemetry／Prometheus exporter；兩者都要把 metric 從 application 送進監控系統，再由 adapter 暴露給 Kubernetes。

HPA 的 `scaleTargetRef` 可以指向 Deployment 或 StatefulSet，不是直接操作 Java／.NET process。VPA 也只看到 Pod/container 的 usage 和 request，不知道 ORM query、GC pause、SQL lock 或 RabbitMQ handler 的 business latency；這些 SLO 要另外做 metrics 和 capacity test。

## 3. HPA、metrics API 與公式

### 三條 metrics path

| HPA metric type | Kubernetes API | 常見 provider | 是否一定要 Metrics Server | target 的意義 |
| --- | --- | --- | --- | --- |
| `Resource` | `metrics.k8s.io` | Metrics Server | Resource metrics 通常需要 | `Utilization` 以 request 作分母；`AverageValue` 是 raw value |
| `ContainerResource` | `metrics.k8s.io` | Metrics Server | Resource metrics 通常需要 | 指定 container 的 usage；`Utilization` 仍以該 container request 作分母 |
| `Pods`／`Object` | `custom.metrics.k8s.io` | Prometheus Adapter 等 custom metrics adapter | 不一定 | per-Pod、object 的 raw custom metric |
| `External` | `external.metrics.k8s.io` | Prometheus Adapter 或其他 external metrics adapter | 不一定 | 不屬於某個 Pod 的 queue、SaaS、broker 等 raw metric |

因此「所有 HPA 都需要 Metrics Server」是錯的：使用 `Resource` 或 `ContainerResource` 時需要可用的 `metrics.k8s.io` provider，通常是 Metrics Server；只使用 custom／external metric 時，必要的是對應的 adapter 和 APIService。若同一個 HPA 有多種 metrics，所有被列出的 provider 都要可用。

### HPA 公式

最基本的 desired replicas 計算是：

~~~text
desiredReplicas =
ceil(currentReplicas × currentMetricValue / desiredMetricValue)
~~~

例如 `currentReplicas = 2`、平均 CPU raw usage `= 120m`、target `= 60m`，初步建議是 `ceil(2 × 120 / 60) = 4`；這只是 controller 的 metric recommendation，還要經過 min/max、tolerance、missing metrics、not-ready Pod 和 behavior policy。`averageUtilization: 60` 不是 60% 的 node CPU，而是 usage 相對於 request 的比例。

只有 `Resource`／`ContainerResource` 使用 `target.type: Utilization` 時，request 是分母。`AverageValue` 和 custom／external raw metric 不會因為 request 改變而自動換算；`AverageValue` 的單位要和 provider 回傳的 quantity 一致。

### HPA manifest 與 behavior

CPU resource 路徑的範例在 [examples/Kubernetes/hpa-resource.yaml](examples/Kubernetes/hpa-resource.yaml)：

~~~yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: products-api
  namespace: dotnet-notebook
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: products-api
  minReplicas: 2
  maxReplicas: 10
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: 60
~~~

這裡的 `2`、`10` 和 `60%` 都是 workload-specific example，不是建議的通用數值；要用壓測、latency SLO、node capacity、SQL connection pool 和下游 rate limit 決定。HPA 啟用後，Deployment 的 `spec.replicas` 應由 HPA 擁有；不要每次重新 apply 一份固定 replicas 的 base manifest，把 controller 的結果覆寫回去。

`behavior` 要同時寫清楚方向和速度：

- `stabilizationWindowSeconds` 用最近的 desired recommendations 抑制 flapping；範例的 `300s` 只代表五分鐘 downscale window，不代表所有 workload 都該等五分鐘。
- `policies` 限制每個 period 可增加或刪除多少 Pod；`type: Pods` 是數量，`type: Percent` 是比例。
- 多個 policy 的預設選擇是允許變化較大的那個；`selectPolicy: Min` 改成較保守的變化，`Disabled` 可停用該方向的 scaling。
- `tolerance` 的 API 行為依 Kubernetes 版本：目前官方文件標示 v1.33 起可用、v1.37 起 stable；沒有設定時使用 cluster-wide default（官方 default 為 10%，也可能被 controller-manager flag 改掉）。為了兼容較舊 cluster，本章 manifest 不依賴這個欄位。

`autoscaling/v2` 是目前 HPA stable API，`autoscaling/v1` 仍可用但欄位能力較少；不要把仍受支援的 HPA `v1` 和已移除的 workload `apps/v1beta1`／`apps/v1beta2` 混為一類。Deployment target 使用 `apps/v1`。

### Metrics 不完整時的行為

HPA 是 control loop，不是連續反應的 interrupt。每次同步會：

1. 找到 `scaleTargetRef` 的 Pods，從對應 metrics API 取值。
2. 忽略 terminating／failed Pod；resource metrics 中沒有 request 的 container 不能計算 utilization。
3. 將 missing metrics 的 Pod 留到後續保守修正；scale down 時假設它們使用 100% target，scale up 時假設 0%，降低錯誤放大的幅度。
4. CPU 指標遇到剛啟動且尚未穩定 Ready 的 Pod 時先排除；not-ready Pod 只會讓 scale up 更保守。
5. 多個 metric 各自計算 desired replicas，取最大的建議；若某個 metric 取不到而其他 metric 建議 scale down，通常跳過 scale down，但仍可依可用 metric scale up。

HPA controller 的 `--horizontal-pod-autoscaler-initial-readiness-delay` 和 `--horizontal-pod-autoscaler-cpu-initialization-period` 是 cluster-wide defaults；官方文件目前列出的 default 是 30 秒和 5 分鐘，不是 application manifest 可以自行設定的欄位。ASP.NET Core warm-up 的 startupProbe、readiness transition 和這兩個 controller window 要一起觀察，避免 startup CPU spike 觸發錯誤 scale out。

## 4. VPA recommendation-only 與 queue depth pipeline

### VPA 的安裝前提和 API version

VPA 不是 Kubernetes core 中和 HPA 一樣的內建 controller。官方 VPA project 由 recommender、updater、admission-controller 三個 component 組成，使用 `autoscaling.k8s.io/v1` 的 `VerticalPodAutoscaler` CRD；安裝前需要 cluster credentials、Metrics Server 和與 Kubernetes 版本相容的 VPA release。以目前官方 installation 文件為例，VPA 1.7.x 對應 Kubernetes 1.35–1.37；Kubernetes 1.34 應選相容的 1.6.x，而不是盲目使用 latest。

安裝要固定 upstream release，不把 master branch 當 production source：

~~~bash
git clone https://github.com/kubernetes/autoscaler.git
cd autoscaler/vertical-pod-autoscaler
git checkout vertical-pod-autoscaler-1.7.1
./hack/vpa-up.sh
~~~

這段命令會修改 cluster，只有在確認 Kubernetes／VPA 相容矩陣和 RBAC 後才執行。VPA component 與 CRD 都存在後，才 apply [examples/Kubernetes/vpa-recommendation.yaml](examples/Kubernetes/vpa-recommendation.yaml)：

~~~yaml
apiVersion: autoscaling.k8s.io/v1
kind: VerticalPodAutoscaler
metadata:
  name: products-api-recommendation
spec:
  targetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: products-api
  updatePolicy:
    updateMode: Off
  resourcePolicy:
    containerPolicies:
      - containerName: products-api
        controlledResources: [memory]
        controlledValues: RequestsOnly
~~~

`updateMode: Off` 仍會由 recommender 寫入 `status.recommendation`，但不會由 VPA evict 或 mutate Pod，是 recommendation-only dry run。查詢建議：

~~~bash
kubectl get vpa products-api-recommendation -n dotnet-notebook \
  -o jsonpath='{range .status.recommendation.containerRecommendations[*]}{.containerName}{" cpu="}{.target.cpu}{" memory="}{.target.memory}{"\n"}{end}'
kubectl describe vpa products-api-recommendation -n dotnet-notebook
~~~

本例 HPA 以 CPU utilization 做分母，因此 VPA 只控制 memory，並且 `RequestsOnly` 不碰 limits。若 VPA 同時控制 CPU request，HPA 的 utilization denominator 就會移動；若 VPA 改 memory，而 HPA 也用 memory utilization，同樣會互相干擾。正式環境要先選 ownership，再用壓測驗證。

### RabbitMQ queue depth 的完整 external metrics pipeline

queue depth 不能直接寫成 HPA metric name；需要每一段都存在：

~~~text
RabbitMQ management / exporter
  ↓ rabbitmq_queue_messages_ready{queue,vhost}
Prometheus scrape + retention
  ↓ PromQL
Prometheus Adapter externalRules
  ↓ external.metrics.k8s.io/v1beta1 APIService
HPA External metricSelector
  ↓
Deployment replicas
~~~

本 repo 提供兩個互斥的 HPA 選項：

- [hpa-resource.yaml](examples/Kubernetes/hpa-resource.yaml)：使用 `metrics.k8s.io` 的 CPU resource metric。
- [hpa-rabbitmq-external.yaml](examples/Kubernetes/hpa-rabbitmq-external.yaml)：使用 `external.metrics.k8s.io` 的 `rabbitmq_queue_messages_ready`。

RabbitMQ queue 選項需要 Prometheus 已經收到 exporter 的 `rabbitmq_queue_messages_ready`，而且至少保留 `queue` 和 `vhost` labels。Prometheus Adapter 的 Helm values 在 [prometheus-adapter-values.example.yaml](examples/Kubernetes/prometheus-adapter-values.example.yaml)：

~~~yaml
rules:
  external:
    - seriesQuery: '{__name__="rabbitmq_queue_messages_ready",queue!="",vhost!=""}'
      resources:
        namespaced: false
      name:
        matches: '^rabbitmq_queue_messages_ready$'
        as: rabbitmq_queue_messages_ready
      metricsQuery: 'sum(<<.Series>>{<<.LabelMatchers>>}) by (queue, vhost)'
~~~

`namespaced: false` 是因為 RabbitMQ queue 不是 Kubernetes object；HPA 用 selector 指定 `queue` 和 `vhost`。若你的 exporter 已把 Kubernetes namespace 映射成 metric label，可以改用 namespaced rule，但要在 adapter config、Prometheus labels 和 HPA namespace 三處一致。

Adapter 的安裝仍要固定 chart version，並指向正確的 Prometheus Service；Prometheus Adapter 的 chart 只提供 API translation，不會代替 RabbitMQ exporter 或 Prometheus：

~~~bash
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts
helm repo update
helm upgrade --install prometheus-adapter \
  prometheus-community/prometheus-adapter \
  --namespace monitoring --create-namespace \
  --values examples/Kubernetes/prometheus-adapter-values.example.yaml \
  --version <pinned-chart-version>

kubectl get --raw \
  '/apis/external.metrics.k8s.io/v1beta1/namespaces/dotnet-notebook/rabbitmq_queue_messages_ready'
kubectl describe hpa products-api -n dotnet-notebook
~~~

`<pinned-chart-version>` 必須替換成平台測試過的 chart version；沒有 APIService、adapter rule、Prometheus series 或 labels 時，HPA 會出現 `FailedGetExternalMetric`／`ScalingActive=False`，不是把 `kubectl top` 裝好就會恢復。

`AverageValue: "30"` 是示例：它代表每個 replica 目標處理約 30 個 ready messages，應由 handler throughput、message age SLO、prefetch、retry rate 和下游 capacity 實測。queue depth 上升但 handler latency、SQL connection pool 或 downstream rate limit 已飽和時，盲目擴 replicas 只會把瓶頸推給共享 dependency。

### 實務診斷

先分辨 HPA 用哪條 API，再查 provider：

~~~bash
kubectl get --raw /apis/metrics.k8s.io/v1beta1/nodes
kubectl top pods -n dotnet-notebook
kubectl get apiservice | grep -E 'metrics|custom|external'
kubectl describe hpa products-api -n dotnet-notebook
kubectl get hpa products-api -n dotnet-notebook -o yaml
kubectl get vpa products-api-recommendation -n dotnet-notebook -o yaml
~~~

看到 HPA `ScalingActive=False` 時，依序檢查：APIService 是否 Available、provider 是否有 RBAC／TLS 問題、metric discovery 是否找到 series、labels 是否能對上 selector、Pod requests 是否完整、Deployment 是否已 Ready，以及 SQL Server／Redis／RabbitMQ 是否能承受新增 replicas。autoscaling 是 capacity control loop，不是 traffic generator。

## 5. 常見誤解

- 不是所有 HPA 都需要 Metrics Server；只有 resource metrics 路徑必須有可用的 `metrics.k8s.io` provider。
- CPU utilization 的分母是 request，不是 node CPU；缺 request 或 container 名稱不一致，metric 可能無法計算。
- Custom metric 和 external metric 都需要 adapter；只在 HPA YAML 寫出名稱不會創造 metric。
- HPA 會在 missing／not-ready metrics 時採保守計算，不是拿不到資料仍照原比例盲目擴容。
- HPA 多個 metrics 會取最大的 desired replicas；某一個 metric 失敗時，scale down 可能被跳過。
- `stabilizationWindowSeconds` 不是 rate limit；要搭配 `policies` 和 `selectPolicy` 限制每個 period 的變化。
- `tolerance` 的欄位和 stable version 有 Kubernetes 版本條件；不能把目前 cluster 的 default 寫成所有 cluster 的 API 保證。
- VPA `Off` 會給 recommendation，但不會自行修改 Pod；`Recreate`／`Auto` 可能 evict，不能當零中斷設定。
- HPA 和 VPA 同時控制 CPU request 會改變 HPA 分母；要明確分配 resource ownership。
- `replicas: 2`、`maxReplicas: 10`、`60%`、`300s` 和 queue `30` 都是示例，沒有 workload、SLO 和 capacity 條件就沒有通用意義。
- autoscaling 不會替 application 解決 database lock、connection pool、cache stampede、RabbitMQ poison message 或 downstream rate limit。

## 6. 面試怎麼回答

> 我會先問 HPA 使用哪一種 metrics。Resource 和 ContainerResource 需要 `metrics.k8s.io`，通常由 Metrics Server 提供；custom metrics 需要 `custom.metrics.k8s.io` adapter，RabbitMQ queue 這類不屬於 Pod 的訊號則需要 `external.metrics.k8s.io`。HPA 依公式計算 desired replicas，Utilization 只有在 Resource／ContainerResource target 下才以 request 作分母；missing metrics、not-ready Pod 和 startup CPU 會讓 controller 保守處理。VPA 是額外安裝的 controller，我會先用 `autoscaling.k8s.io/v1`、`updateMode: Off` 觀察 recommendation，並讓 VPA 只控制不被 HPA 使用的 resource。最後用 load test、latency SLO、queue age 和 database capacity 決定 min/max、policy 和 target，而不是直接套用 2、10 或 60%。

## 7. 小練習

1. 為 `products-api` 寫一個 resource HPA，列出 requests 缺失時 HPA status 會出現的診斷方向。
2. 把 CPU HPA 改成 `ContainerResource`，說明 sidecar request 是否會影響目標 container 的分母。
3. 以 `currentReplicas=3`、current queue depth `= 95`、每 replica target `= 30` 計算初步 desired replicas，並列出 maxReplicas、tolerance 和 policy 可能再改變什麼。
4. 從 RabbitMQ exporter 到 `external.metrics.k8s.io` 寫出每一段的 resource、API 和驗證命令。
5. 為 HPA 使用 CPU utilization 的 Deployment 設計 VPA recommendation-only policy，說明為什麼 `controlledResources` 不能包含 CPU。
