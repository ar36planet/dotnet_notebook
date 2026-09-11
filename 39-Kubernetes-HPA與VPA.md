---
title: 39 Kubernetes HPA 與 VPA
tags: [kubernetes, hpa, vpa, autoscaling, microservices]
---

# 39 Kubernetes HPA 與 VPA

## 學習目標

- 分辨 horizontal scaling 和 vertical scaling。
- 使用 autoscaling/v2 撰寫 HPA。
- 知道 metrics-server、resource requests 和 limits 的關係。
- 理解 VPA 的用途、風險和為什麼不應和 HPA 同時控制同一資源。
- 把 autoscaling 和 application latency、queue depth、database capacity 一起思考。

## 1. 一句話理解

HPA 調整 Pod replicas，VPA 調整單一 Pod 的 resource requests / limits；它們都需要可靠的 metrics 和正確的 workload resource model，不能只貼一份 YAML 就保證有效。

先看會出事的場景：HPA 設定 CPU 目標，但 Deployment 沒有 cpu request；metrics-server 沒有資料，HPA 無法正常計算。另一個常見錯誤是 HPA 和 VPA 同時修改同一個 workload 的 CPU requests，兩個 controller 互相拉扯。

## 2. Kubernetes 語法

### HPA autoscaling/v2

~~~yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: products-api
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: products-api
  minReplicas: 2
  maxReplicas: 10
  behavior:
    scaleUp:
      stabilizationWindowSeconds: 0
    scaleDown:
      stabilizationWindowSeconds: 300
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: 60
~~~

CPU utilization 的百分比是相對於 container resource request，不是相對於整台 node 的總 CPU。沒有 requests，這個目標就沒有清楚的分母。

### VPA 的界線

VPA 不是 Kubernetes core 裡和 HPA 一樣普遍的內建 controller，通常需要額外安裝 VPA components。它會根據歷史 usage 建議或調整 requests / limits，調整時可能需要重建 Pod。

~~~text
HPA → replicas
VPA → per-Pod resource requests / limits
~~~

如果 HPA 以 CPU utilization 做 scaling，而 VPA 同時修改 CPU request，HPA 的分母會改變，兩者可能互相影響。常見策略是：

- HPA 負責 CPU / memory，VPA 只做 recommendation。
- HPA 負責 replicas，VPA 調整另一組不被 HPA 使用的資源。
- 由平台 team 明確測試兩者的協作方式，不直接套用範例 YAML。

## 3. 實務範例：觀察 autoscaling

~~~bash
kubectl get hpa products-api
kubectl describe hpa products-api
kubectl top pods
kubectl get deployment products-api -w
~~~

診斷順序：

1. metrics-server 是否在 cluster 裡正常運作。
2. Pod 是否有 CPU / memory requests。
3. HPA 是否抓得到 metrics。
4. workload 是否真的有可擴展的 stateless 設計。
5. database、Redis、RabbitMQ 和外部 API 是否能承受新增 replicas。

擴 replicas 不會自動消除 database lock、connection pool、下游 rate limit 或 queue bottleneck。若 request latency 隨 replicas 增加仍不下降，瓶頸可能在共享 dependency。

## 4. 常見誤解

- HPA 不是 traffic generator，也不會替 application 產生 metrics。
- CPU 低不代表使用者體驗好；API 可能卡在外部 API、database 或 lock。
- VPA 調整 requests 可能重建 Pod，不能當成零中斷設定。
- HPA maxReplicas 沒有替 downstream dependency 設上限；盲目放大可能讓 database 先崩潰。
- autoscaling/v1 和 apps/v1beta2 是舊教材常見寫法；現代範例應使用 autoscaling/v2 和 apps/v1。

## 5. 面試怎麼回答

> HPA 依 metrics 調整 Deployment replicas，VPA 則依 usage 建議或調整單一 Pod 的 CPU / memory requests。HPA 需要 metrics-server 和正確的 resource requests；VPA 調整時可能重建 Pod。兩者一起使用要小心，尤其 HPA 用 utilization 計算而 VPA 會修改 request 分母。autoscaling 也不能取代對 database、queue、connection pool 和下游服務容量的設計。

## 6. 小練習

1. 為 products-api 加上 CPU requests，再寫 autoscaling/v2 HPA。
2. 用 kubectl describe hpa 找出 metrics 不足的原因。
3. 說明為什麼 HPA replicas 增加後，SQL Server 可能先成為瓶頸。
4. 把 VPA 設計成 recommendation-only，避免直接重建 production Pod。
5. 為 RabbitMQ consumer 設計以 queue depth 為基礎的 custom metric scaling。
