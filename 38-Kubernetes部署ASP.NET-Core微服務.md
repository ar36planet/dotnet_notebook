---
title: 38 Kubernetes 部署 ASP.NET Core 微服務
tags: [kubernetes, aspnet-core, microservices, docker, deployment]
---

# 38 Kubernetes 部署 ASP.NET Core 微服務

## 學習目標

- 分辨 Pod、Deployment、Service、ConfigMap、Secret 和 Ingress。
- 用 apps/v1 Deployment 部署 .NET 10 container。
- 用 ClusterIP 和 DNS 做 service-to-service communication。
- 設定 readiness、liveness、resource requests 和 limits。
- 看懂 rolling update、rollback 和基本 kubectl 診斷流程。

## 1. 一句話理解

Kubernetes Deployment 維持 Pod replicas，Service 提供穩定的 DNS 和 routing，ConfigMap / Secret 注入設定，Ingress 或 Gateway 把外部 HTTP request 導進 cluster。

先看會出事的場景：直接把 Pod IP 寫進另一個 service 的 configuration。Pod 被重建後 IP 會變，呼叫方立刻失效。Kubernetes service name 才是穩定的 application endpoint，例如 commands-service.default.svc.cluster.local。

## 2. Kubernetes 語法

### Deployment

~~~yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: products-api
spec:
  replicas: 2
  selector:
    matchLabels:
      app: products-api
  template:
    metadata:
      labels:
        app: products-api
    spec:
      containers:
        - name: products-api
          image: registry.example.com/products-api:10.0.0
          ports:
            - name: http
              containerPort: 8080
          env:
            - name: ASPNETCORE_HTTP_PORTS
              value: "8080"
          readinessProbe:
            httpGet:
              path: /health/ready
              port: http
          livenessProbe:
            httpGet:
              path: /health/live
              port: http
          resources:
            requests:
              cpu: 100m
              memory: 128Mi
            limits:
              cpu: 500m
              memory: 512Mi
~~~

Deployment 負責 desired state 和 rollout；Pod 只是執行個體，不應直接拿來當穩定 endpoint。image tag 應該是 immutable version 或 git SHA，不用 latest。

### Service

~~~yaml
apiVersion: v1
kind: Service
metadata:
  name: products-api
spec:
  type: ClusterIP
  selector:
    app: products-api
  ports:
    - name: http
      port: 8080
      targetPort: http
~~~

cluster 內的另一個 Pod 可以用 products-api:8080 連線。ClusterIP 是 internal service；NodePort、LoadBalancer 或 Gateway / Ingress 才是外部入口。Service selector 必須和 Pod labels 對得上，否則 service 沒有 endpoints。

### ConfigMap 與 Secret

~~~yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: products-api-config
data:
  Redis__Configuration: redis:6379
---
apiVersion: v1
kind: Secret
metadata:
  name: products-api-secret
type: Opaque
stringData:
  ConnectionStrings__Orders: "由部署系統注入的 connection string"
~~~

ConfigMap 放非機密設定；Secret 不是自動加密的秘密保管庫，production 仍要考慮 encryption at rest、RBAC 和 external secret store。

### Readiness 和 liveness

~~~text
readiness false → 不接收新 traffic
liveness false  → kubelet 可能重啟 container
~~~

readiness 檢查「現在能不能接流量」，liveness 檢查「process 是否需要被重啟」。不要把很慢的外部 dependency 直接塞進 liveness，否則短暫 database outage 可能造成 restart storm。

## 3. 實務範例：部署與診斷

~~~bash
kubectl apply -f products-api.yaml
kubectl get deployments
kubectl get pods -l app=products-api
kubectl get service products-api
kubectl describe pod <pod-name>
kubectl logs deployment/products-api --all-containers
kubectl rollout status deployment/products-api
~~~

更新 image：

~~~bash
kubectl set image deployment/products-api \
  products-api=registry.example.com/products-api:10.0.1
kubectl rollout status deployment/products-api
kubectl rollout history deployment/products-api
kubectl rollout undo deployment/products-api
~~~

微服務同步呼叫：

~~~text
PlatformService Pod
        ↓ HTTP http://commands-service:8080
CommandsService Service
        ↓
CommandsService Pods
~~~

這裡不使用 localhost，也不使用 Pod IP。若需要跨 namespace，使用 commands-service.team-a.svc.cluster.local 這類 DNS 名稱。

## 4. 常見誤解

- Pod 不是 deployment；Pod 會被刪除和重建，Deployment 才維持 replicas。
- containerPort 不會自動對外公開；Service 才提供 cluster routing。
- readiness false 不一定代表 application crashed；它可能只是暫時不適合接流量。
- Secret 的 base64 不是 encryption；要限制 RBAC 並考慮 encryption at rest。
- kubectl apply 成功不代表 application ready；要看 rollout、Pod condition、events 和 logs。
- Kubernetes Service selector 對不到 label 時，request 會沒有可用 endpoints。
- Ingress 需要 controller；宣告 Ingress resource 本身不會自動提供 load balancer。

## 5. 面試怎麼回答

> Kubernetes 用 Deployment 管理 ASP.NET Core Pod replicas 和 rollout，Service 提供穩定的 cluster DNS 和 load balancing，application 用 service name 呼叫另一個微服務。ConfigMap 注入非機密設定，Secret 放敏感設定但仍要搭配 RBAC 和 encryption。readiness 決定 Pod 是否接收 traffic，liveness 決定是否需要重啟。更新 image 後用 rollout status、events 和 logs 驗證，失敗時可以 rollback Deployment。

## 6. 小練習

1. 為 ContainerApi 寫 Deployment 和 ClusterIP Service。
2. 為 API 加入 live / ready 兩個 health endpoints。
3. 說明為什麼 Service selector 和 Pod labels 必須一致。
4. 將 API connection string 從 ConfigMap 移到 Secret。
5. 讓 PlatformService 使用 commands-service DNS，而不是 Pod IP。
