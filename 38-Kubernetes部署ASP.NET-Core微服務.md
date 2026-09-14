---
title: 38 Kubernetes 部署 ASP.NET Core 微服務
tags: [kubernetes, aspnet-core, microservices, docker, deployment]
---

# 38 Kubernetes 部署 ASP.NET Core 微服務

## 學習目標

- 分辨 Pod、Deployment、Service、ConfigMap、Secret、Ingress 和 Job。
- 用 apps/v1 Deployment 部署 .NET 10 container，並固定 image tag 與 digest。
- 讓 ASP.NET Core 的 health endpoints 和 startup、readiness、liveness probes 對得上。
- 設定 rolling update、securityContext、resource requests、PDB 和 graceful shutdown。
- 用 `kubectl` 診斷全部 replicas，知道 rollback 不會還原哪些外部資源。

## 1. 一句話理解

Kubernetes Deployment 維持 Pod replicas，Service 提供穩定的 DNS 和 routing，ConfigMap／Secret 注入設定，Ingress 或 Gateway 把外部 HTTP request 導進 cluster；真正的部署還要把 probe、權限、資源、更新和停止行為一起定義。

先看會出事的場景：直接把 Pod IP 寫進另一個 service 的 configuration。Pod 被重建後 IP 會變，呼叫方立刻失效。Kubernetes Service name 才是穩定 endpoint，例如 `commands-service.team-a.svc.cluster.local`；API 本身還要先通過 readiness，Service 才應把流量送進去。

## 2. Kubernetes 與 Java 對照

ASP.NET Core 的 Deployment、Service、probe 和 Secret injection，與 Spring Boot／Java application 放在 Kubernetes 上的邊界相同。`.NET` 的 `IHostApplicationLifetime`、`HostOptions.ShutdownTimeout` 對應 Java application graceful shutdown；兩者都不能把 liveness 當成 database health。`ConfigMap`／`Secret`、Service DNS、rolling update 和 Job migration 都是 Kubernetes contract，不是某一種語言的 API。

Java Spring Boot 常見 `/actuator/health/liveness` 和 `/actuator/health/readiness`；ASP.NET Core 可以用 `/health/live` 和 `/health/ready`，重點是 probe path、port、status code 和 application 的實作必須一致。只改 manifest 的 path，沒有修改 application endpoint，Pod 會永遠不 ready。

## 3. ASP.NET Core 與 Kubernetes 語法

### Health endpoint 與 shutdown timeout

`examples/ContainerApi/Program.cs` 的 application contract 是：

~~~csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
~~~

`/health/live` 只檢查 process 還能回應；`/health/ready` 才放需要接流量的 dependency checks。這個 sample 沒有外部 dependency，因此 ready endpoint 目前會回 healthy；正式服務要為 dependency health check 加上 `ready` tag，並決定哪些短暫故障只影響 readiness。不要把慢的 database 或 Redis check 放進 liveness，否則短暫 outage 會造成 restart storm。

### Manifest 的必要欄位

本章的可套用結構在 [examples/Kubernetes/app.yaml](examples/Kubernetes/app.yaml)：

- `metadata.namespace`、ConfigMap、Secret references、Service、PDB 和 workload 都在同一個 `dotnet-notebook` namespace。
- Deployment 明確設定 `replicas: 2`、`maxUnavailable: 0`、`maxSurge: 1`、`minReadySeconds: 10` 和 `progressDeadlineSeconds: 600`；這些數字是結構示例，應依 warm-up、SLO、load test 和監控調整。
- image 使用 version tag 加 digest，明確寫 `imagePullPolicy: IfNotPresent` 和 private registry `imagePullSecrets`。manifest 的全零 digest 是 placeholder，替換前不能 rollout。
- `configMapRef` 注入非機密設定；connection string 透過 `secretKeyRef` 讀取，不把 Secret value 提交到 repo。
- 專用 ServiceAccount 關閉 `automountServiceAccountToken`；application 不呼叫 Kubernetes API 時不需要 Pod token。
- `securityContext` 使用 non-root、`allowPrivilegeEscalation: false`、drop all capabilities、RuntimeDefault seccomp 和 read-only root filesystem。`/tmp` 由 `emptyDir` 提供可寫空間。

Service selector 必須和 Pod label 完全相同；`containerPort` 只是 container metadata，Service 才建立 cluster routing。Cluster 內呼叫使用 `products-api:8080`，不能使用 Pod IP 或 `localhost`。

### Probe 與 rolling update

manifest 的三種 probe 責任不同：

~~~text
startupProbe  → warm-up 期間先保護 application，成功前不執行 liveness/readiness
readinessProbe → false 時從 Service endpoints 移除，不接新 traffic
livenessProbe  → process 長期無法回應時讓 kubelet 重啟 container
~~~

`startupProbe` 的 `periodSeconds`、`timeoutSeconds` 和 `failureThreshold` 是 warm-up budget，不是通用預設值；應用 warm-up 要從實測啟動時間和 SLO 推導。readiness／liveness 也要明確設定 timeout、period 和 failure threshold，不能只寫 path。

Deployment 更新和 Pod health 是兩件事：`progressDeadlineSeconds` 只控制 rollout 是否長時間沒有進展；它不會修復錯誤 image、缺少 Secret 或 application exception。`replicas: 2` 只表示多一份 replica；若要跨 node／zone，要依 failure domain 加 topology spread 或 anti-affinity，必要時用 PDB 保護 voluntary disruption。

### Graceful shutdown 與 proxy 邊界

這個 sample 的 application shutdown timeout 是 30 秒，Pod `preStop` 先等待 5 秒，`terminationGracePeriodSeconds` 是 45 秒，因此有 `5 + 30` 秒以上的窗口讓 application drain in-flight request。實際 worker 有更長 drain 時，三個值必須一起量測和調整；Kubernetes grace 小於 application timeout 會在工作完成前送 SIGKILL。

Ingress／Gateway 後方的 ASP.NET Core 若依賴原始 scheme、client IP 或 redirect URL，才配置 `UseForwardedHeaders`。只信任明確列出的 proxy IP／network；不能清空 trusted proxy 限制後接受任何 client 送來的 `X-Forwarded-*` header。純粹回應 JSON 的 ContainerApi 不需要為了部署範例而無條件信任 forwarded headers。

## 4. 實務範例：Deployment、migration Job 與診斷

### 套用前的秘密和 image

[examples/Kubernetes/README.md](examples/Kubernetes/README.md) 列出套用前要由平台建立的三個資源：`registry-credentials`、`products-api-secret` 和 `products-api-tls`。Secret 不在 repo 中放明文；TLS secret 由 cert-manager、Gateway 或平台憑證流程管理。image 的 digest 要替換成 registry 實際產生的 digest，不能把 tag 當成 immutable identity。

### Server-side dry-run 與 rollout

有 cluster credentials 時，用 Kubernetes API 做 schema／admission 驗證：

~~~bash
kubectl apply --server-side --dry-run=server \
  -f examples/Kubernetes/namespace.yaml
kubectl apply --server-side --dry-run=server \
  -f examples/Kubernetes/app.yaml
kubectl apply --server-side --dry-run=server \
  -f examples/Kubernetes/migration-job.example.yaml

kubectl apply -f examples/Kubernetes/namespace.yaml
kubectl apply -f examples/Kubernetes/app.yaml
kubectl rollout status deployment/products-api -n dotnet-notebook
~~~

server-side dry-run 只能驗證 API schema 和 admission，不會確認 image、Secret、TLS certificate、Ingress controller 或 migration command 已存在。沒有 cluster 時，最多做 YAML parse；不能把 client-side parse 當成 server-side dry-run 通過。

### Migration Job

資料庫 migration 不應由每個 API replica 在 startup 時競爭執行。`examples/Kubernetes/migration-job.example.yaml` 把 migration 分成單一 Job、獨立 ServiceAccount、獨立 migration image 和 `backoffLimit: 1`；migration image 的 command 必須是 reviewed SQL／migration bundle 的實際入口，不能直接照抄 placeholder。Job 完成後，`rollout undo` 不會把已套用的 schema 自動降回去。

### 更新、rollback 和 logs

~~~bash
kubectl set image deployment/products-api \
  products-api=registry.example.com/dotnet-notebook/container-api:10.0.1@sha256:<real-digest> \
  -n dotnet-notebook
kubectl rollout status deployment/products-api -n dotnet-notebook
kubectl rollout history deployment/products-api -n dotnet-notebook
kubectl rollout undo deployment/products-api -n dotnet-notebook

kubectl get pods -n dotnet-notebook \
  -l app.kubernetes.io/name=products-api -o wide
kubectl get events -n dotnet-notebook --sort-by=.lastTimestamp
kubectl logs -n dotnet-notebook \
  -l app.kubernetes.io/name=products-api \
  --all-containers=true --prefix=true --timestamps=true
kubectl logs -n dotnet-notebook \
  -l app.kubernetes.io/name=products-api \
  --all-containers=true --prefix=true --previous
~~~

selector、`--all-containers`、`--prefix` 讓多 replicas 的 logs 能辨認來源；`--previous` 只在 container 曾重啟時有內容。`rollout undo` 只還原 Deployment 的 Pod template revision，不會還原 ConfigMap、Secret、PDB、Ingress、Job 或 database migration。

## 5. 常見誤解

- Pod 不是 Deployment；Pod 會被刪除和重建，Deployment 才維持 replicas 和 rollout history。
- `replicas: 2` 是 redundancy，不等於跨 zone 高可用；還要看 node、zone、topology spread 和 PDB。
- `containerPort` 不會自動對外公開；Service、Ingress 或 Gateway 才提供 routing。
- readiness false 不一定代表 application crashed；它可能只是暫時不適合接流量。
- liveness 不應直接檢查 SQL Server、Redis 或 RabbitMQ；dependency outage 通常先影響 readiness。
- startupProbe 不會延長 Kubernetes termination grace；啟動 budget 和停止 drain 是兩個不同的時間軸。
- Secret 的 base64 不是 encryption；要限制 RBAC、使用 encryption at rest 和 external secret provider。
- `kubectl apply` 成功不代表 application ready；要看 rollout、Pod conditions、events、probe status 和 logs。
- `rollout undo` 不會回滾 database schema；schema migration 必須有獨立的 reviewed rollback／forward-fix 策略。
- Ingress resource 需要對應 controller；宣告 Ingress 本身不會自動提供 load balancer。
- `X-Forwarded-*` 不是 client 可以任意指定的可信資訊；只有明確列出的 proxy 才能被 application 信任。

## 6. 面試怎麼回答

> 我會用 Deployment 管理 ASP.NET Core Pod replicas 和 rolling update，用 Service 提供穩定的 cluster DNS。ConfigMap 注入非機密設定，Secret 透過 secret provider 和 RBAC 管理；image 使用 immutable tag 加 digest。application 提供和 manifest 一致的 startup、readiness、liveness endpoints，readiness 決定是否接流量，liveness 不直接依賴外部 database。SecurityContext 會用 non-root、drop capabilities、RuntimeDefault seccomp 和 read-only root filesystem；停止時間要大於 preStop 加 application shutdown timeout。Migration 由單一 Job 執行，`rollout undo` 只還原 Pod template，不會還原 ConfigMap、Secret 或 schema。

## 7. 小練習

1. 把 `examples/Kubernetes/app.yaml` 的 image tag 和 digest 改成同一個 registry build，並用 server-side dry-run 驗證。
2. 為一個需要 SQL Server 的 API 設計只影響 readiness、不影響 liveness 的 health check。
3. 依實測 warm-up 95 百分位數，重新計算 startupProbe 的 period、timeout 和 failure threshold。
4. 說明 `replicas: 2`、topology spread 和 PDB 各自能保護哪一種 failure。
5. 設計 migration Job、database schema 版本和 rollout／rollback 的順序，讓 deployment 不會由多個 replica 同時 migrate。
