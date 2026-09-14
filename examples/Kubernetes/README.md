# Kubernetes deployment baseline

這組 manifest 對應 `examples/ContainerApi` 的 .NET 10 container。`/health/live` 只表示 process 還能回應；`/health/ready` 是接流量前的 readiness endpoint。ContainerApi 目前沒有外部 dependency，所以兩個 endpoint 都會回 healthy；正式服務應把可影響接流量的 dependency health check 標上 `ready`，不要把 database outage 塞進 liveness。

## 套用前要替換的外部資源

`app.yaml` 故意不建立下列含秘密或環境專屬的資源：

- `registry-credentials`：由 private registry 的 secret provider 或部署系統建立。
- `products-api-secret`：至少提供 `orders-connection-string`，用 external secret 或部署系統注入，不把 value commit 到 repo。
- `products-api-tls`：由 cert-manager、Gateway 或平台的憑證流程建立。
- `registry.example.com/...@sha256:000...`：替換為實際 image tag + digest；全零 digest 只是結構 placeholder，不可直接拿來 rollout。

ConfigMap 和 Secret reference、workload、Service、PDB、Ingress 都使用 `dotnet-notebook` namespace。不要把 production Secret 改成 `stringData` 直接提交；Kubernetes Secret 的 base64 不是加密。

## 驗證 manifest

有 cluster credentials 時，從 namespace 開始做 server-side dry-run：

```bash
kubectl apply --server-side --dry-run=server \
  -f examples/Kubernetes/namespace.yaml
kubectl apply --server-side --dry-run=server \
  -f examples/Kubernetes/app.yaml
kubectl apply --server-side --dry-run=server \
  -f examples/Kubernetes/migration-job.example.yaml
```

server-side dry-run 只能驗證 API schema 和 admission；它不會確認 image、Secret、TLS certificate、Ingress controller 或 migration command 真的存在。

## Rollout 與 shutdown

```bash
kubectl apply -f examples/Kubernetes/namespace.yaml
kubectl apply -f examples/Kubernetes/app.yaml
kubectl rollout status deployment/products-api -n dotnet-notebook
kubectl get pods -n dotnet-notebook -l app.kubernetes.io/name=products-api
```

Deployment 的 `replicas: 2` 是 redundancy，不等於跨 zone 高可用；`topologySpreadConstraints` 只提供排程提示，failure domain 和 node capacity 仍要由平台驗證。`minReadySeconds`、`maxUnavailable`、`maxSurge`、`progressDeadlineSeconds` 的值是結構示例，應依 warm-up、SLO、load test 和監控調整。

ContainerApi 設定 application shutdown timeout 為 30 秒；manifest 的 `preStop` 先等 5 秒，`terminationGracePeriodSeconds` 設為 45 秒，留出 `5 + 30` 秒以上的時間給 in-flight request。若實際 worker 有更長的 drain，三個值要一起改，不能只加大 Kubernetes grace period。

`kubectl rollout undo deployment/products-api` 只還原 Deployment 的 Pod template revision，不會還原 ConfigMap、Secret、Job 或 database migration。schema migration 要使用 reviewed SQL／migration bundle，由單一 migration Job 和獨立 identity 執行；不要讓每個 API replica 啟動時競爭跑 migration。

## 診斷所有 replicas

```bash
kubectl get pods -n dotnet-notebook -l app.kubernetes.io/name=products-api -o wide
kubectl describe deployment/products-api -n dotnet-notebook
kubectl get events -n dotnet-notebook --sort-by=.lastTimestamp
kubectl logs -n dotnet-notebook \
  -l app.kubernetes.io/name=products-api \
  --all-containers=true --prefix=true --timestamps=true
kubectl logs -n dotnet-notebook \
  -l app.kubernetes.io/name=products-api \
  --all-containers=true --prefix=true --previous
```

`--previous` 只在 container 曾重啟時有內容；selector、`--all-containers` 和 `--prefix` 讓多 replicas、多 containers 的輸出仍能辨認來源。

## Forwarded headers

Ingress／Gateway 必須只由可信 proxy 寫入 `X-Forwarded-For`、`X-Forwarded-Proto` 等 header。ASP.NET Core 應在 `UseForwardedHeaders` 中明確列出 trusted proxy IP 或 network，不能用「接受所有 proxy」的設定掩蓋邊界；如果 application 不依賴 scheme、client IP 或 redirect URL，就不要為了看起來完整而無條件信任外部 header。

## Autoscaling overlays

`hpa-resource.yaml` 和 `hpa-rabbitmq-external.yaml` 是互斥的 HPA 選項，不能同時套用到同一個 Deployment。HPA 接管 replicas 後，Deployment base manifest 不應再由 GitOps 每次寫回固定的 `spec.replicas`。`vpa-recommendation.yaml` 可以和 CPU HPA 一起使用，因為它是 `updateMode: Off` 且只觀察 memory recommendation；VPA CRD 和 components 要先按相容版本安裝。

RabbitMQ HPA 需要 Prometheus exporter、Prometheus、Prometheus Adapter 的 `externalRules` 和 `external.metrics.k8s.io` APIService；只套用 HPA object 不會產生 queue metric。Adapter 的 Helm values 範例在 `prometheus-adapter-values.example.yaml`。
