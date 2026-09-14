---
title: 35 Docker Compose ASP.NET Core 與 SQL Server
tags: [docker, docker-compose, aspnet-core, sql-server, ef-core]
---

# 35 Docker Compose ASP.NET Core 與 SQL Server

## 學習目標

- 用 Compose 描述 API 與 SQL Server service；沒有 frontend 時不要在文件宣稱有第三個 service。
- 分辨 host port 和 service-to-service port。
- 用 Compose service name 連線，不依賴 localhost 或硬編 IP。
- 為 SQL Server volume、health check、migration 和秘密設定清楚的責任。
- 知道 depends_on 只處理啟動依賴，不保證資料庫已經 ready。

## 1. 一句話理解

Docker Compose 是一份可版本控制的多 container 執行描述；每個 service 有自己的 image、environment、network、volume 和 health check，Compose 會替同一個 project 建立預設 network。

先看會出事的場景：Backend container 用 localhost,1433 連 SQL Server container。localhost 指向 Backend 自己，不是 SQL Server；即使兩個 container 在同一台電腦，這個連線也會失敗。Compose 中應使用 service name，例如 Server=db,1433。

## 2. Docker Compose 語法

### Service name 和 port

~~~text
Browser / client → host localhost:8081 → API container:8080
API container → db:1433 → SQL Server container:1433
~~~

只有需要從 host 存取的 service 才需要 ports。API 連 SQL Server 時使用 db:1433，不需要把 SQL Server port 發布到 host；若要用 SSMS 從 host 連線，才額外加 15433:1433。

### Compose 基本範例

~~~yaml
services:
  api:
    build:
      context: ./Api
      dockerfile: Dockerfile
    environment:
      ASPNETCORE_HTTP_PORTS: 8080
      DB_HOST: db,1433
      DB_NAME: Orders
      DB_USER: orders_app
      DB_PASSWORD_FILE: /run/secrets/app_db_password
    secrets:
      - app_db_password
    ports:
      - "127.0.0.1:8081:8080"
    depends_on:
      db:
        condition: service_healthy
    restart: unless-stopped
    stop_grace_period: 30s

  db:
    build:
      context: ./db
      dockerfile: Dockerfile
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_PID: Developer
    secrets:
      - sa_password
      - app_db_password
    volumes:
      - sqlserver-data:/var/opt/mssql
    healthcheck:
      test:
        - CMD-SHELL
        - >-
          SQLCMDPASSWORD="$$(cat /run/secrets/sa_password)"
          /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -Q "SELECT 1"
          >/dev/null 2>&1 || exit 1
      interval: 10s
      timeout: 5s
      retries: 30
      start_period: 30s
    restart: unless-stopped
    stop_grace_period: 60s

secrets:
  sa_password:
    file: ./secrets/sa_password.txt
  app_db_password:
    file: ./secrets/app_db_password.txt

volumes:
  sqlserver-data:
~~~

本機 secret 放在未提交的檔案：

~~~bash
printf '%s' '由本機 secret provider 產生的長密碼' > examples/ComposeSqlServer/secrets/sa_password.txt
printf '%s' '由本機 secret provider 產生的 app 密碼' > examples/ComposeSqlServer/secrets/app_db_password.txt
~~~

不要把這些檔案提交到 repository。正式環境使用部署平台的 secret，application 不使用 `sa`；確認 SQL Server image digest、sqlcmd 路徑和 license policy 都符合實際版本。

### depends_on 和 readiness

~~~yaml
depends_on:
  db:
    condition: service_healthy
~~~

這只表示 Compose 等待 db 的 health check 通過後才啟動 api。它不代表 schema 已完成 migration、seed data 已存在，或 application 的 connection pool 已經可長時間使用。

EF Core migration 可以由獨立 deployment job 執行，也可以在明確控制的 development compose profile 執行。不要讓每個 API replica 啟動時同時競爭 migration lock。

### Secret 的界線

Compose、Swarm、Kubernetes 的 secret 行為和支援範圍要依目前版本驗證。至少要做到：

- secret 不進 Git、Dockerfile、image layer 或 shell history。
- connection string 由 runtime 注入。
- production secret 由部署平台管理，不用教材中的固定密碼。
- docker compose config 輸出時小心不要把解析後的秘密貼到 log。

## 3. 實務範例：ASP.NET Core + SQL Server

### ASP.NET Core 設定

~~~csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Orders")));
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrdersDbContext>("sql", tags: ["ready"]);

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
app.MapControllers();
app.Run();
~~~

同一段 code 在 localhost-only development override 可使用 `Server=localhost,15433`，在本 Compose stack 則使用 `DB_HOST=db,1433`。這就是設定外部化：程式碼不因環境變更而分叉。正式環境應使用受信任 certificate 與 hostname validation；本範例的 `TrustServerCertificate` 只限本機 development。

### 常用 Compose commands

~~~bash
docker compose -f examples/ComposeSqlServer/compose.yaml config --quiet
docker compose -f examples/ComposeSqlServer/compose.yaml up --build --wait
curl http://127.0.0.1:8081/health/live
curl http://127.0.0.1:8081/health/ready
curl 'http://127.0.0.1:8081/api/orders?pageSize=20'
docker compose -f examples/ComposeSqlServer/compose.yaml ps
docker compose -f examples/ComposeSqlServer/compose.yaml logs api
docker compose -f examples/ComposeSqlServer/compose.yaml down
docker compose -f examples/ComposeSqlServer/compose.yaml down --volumes  # 會刪資料庫 volume，先確認範圍
~~~

docker compose down 預設不刪 named volume；--volumes 會讓資料庫資料消失，只有在明確要重置本機資料時使用。

### Frontend、API 和 CORS

如果另一個 frontend 直接從不同 origin 呼叫 API，需要設定 CORS；如果由同 origin server-side proxy 轉發，瀏覽器可能只看到一個 origin。先畫出實際 request path，再決定是否需要 CORS：

~~~text
Browser → frontend origin → server-side API call
~~~

和：

~~~text
Browser → frontend origin
Browser → api origin
~~~

不要只因為兩個 container 不同，就在所有地方開 AllowAnyOrigin。CORS 是 browser policy，不是 container network policy。

## 4. 常見誤解

- Compose service 的 localhost 不會指向另一個 service；使用 service name。
- host 的 15433 和 container 的 1433 是兩個不同 port；API container 通常只需要後者。
- depends_on 不是資料庫 migration 工具，也不是完整 readiness orchestration。
- volume 是資料生命週期的一部分；刪除 volume 可能刪掉整個 SQL Server database。
- Compose 可以建立 network，但不會自動替 application 設計 retry、timeout、migration lock 或 transaction。
- CORS 解決 browser origin policy，不解決 container 之間的 DNS、authentication 或 network isolation。
- SQL Server admin password 與 app password 由 secret file／平台 secret 注入；不應放在公開 Compose 檔案、argv 或 log。application 使用獨立 `orders_app`，不使用 `sa`。
- mutable tag 會讓教材今天能跑、明天不一定能重現；資料庫 image 要用明確版本，production deployment 再 pin digest。

## 5. 面試怎麼回答

> Docker Compose 用 YAML 描述多個 container service。API 和 database 各自有 image、environment、port、network 和 volume；同一個 Compose network 裡，API 用 database 的 service name 和 container port 連線，例如 db:1433，不是 localhost。depends_on 可以描述啟動依賴，但資料庫 readiness、migration、retry 和 backup 仍要由 application／deployment workflow 負責。密碼應由 secret file、secret store 或部署平台注入，不寫進 image 或 Git。

## 6. 小練習

1. 把 API 的 host port 從 8081 改成 18081，保持 container port 8080。
2. 說明為什麼 SQL Server container 要有 named volume。
3. 為 API 加入 AddDbContextCheck，並設計 SQL Server 尚未 ready 時的重試策略。
4. 將 Compose 拆成 development profile 和 production-like profile，說明每個 profile 的差異。
5. 把固定 connection string 改成環境變數，確認 git diff 不會出現密碼。
