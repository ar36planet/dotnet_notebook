---
title: 34 Docker 與 .NET Container
tags: [docker, container, dotnet, aspnet-core, deployment]
---

# 34 Docker 與 .NET Container

## 學習目標

- 分辨 image、container、volume、network 和 registry。
- 用 .NET 10 multi-stage Dockerfile 建立 ASP.NET Core image。
- 理解 build stage 和 runtime stage 為什麼要分開。
- 用 environment variable 傳入 ASP.NET Core 設定，不把密碼寫進 image。
- 看懂 container port、host port、`EXPOSE`、health check 和 non-root user。

## 1. 一句話理解

Docker image 是可重複分發的應用程式檔案系統，container 是該 image 的執行個體；.NET container 通常只帶 production publish output 和 ASP.NET Core runtime，不把 SDK 和原始碼帶進 production image。

先看一個會出事的場景：把整個 .NET SDK image 當成 production image，並把 connection string 寫在 Dockerfile。image 會變大，SDK、原始碼和秘密也會一起進入 registry。正確做法是把 restore、build、publish 放在 build stage，最後只把 publish output 複製到 runtime stage。

## 2. Docker 與 .NET 語法

### Image、container、volume、network

```text
Dockerfile → docker build → image
image + config → docker run → container
container data → volume
container-to-container → Docker network
image distribution → registry
```

- image 是 immutable layers 的集合。
- container 是 image 的 writable layer 加上 process 和 runtime configuration。
- container 刪掉後，寫在 writable layer 的資料通常也消失；資料庫要使用 volume。
- 同一個 Docker network 上的 service 應使用 service name 互相連線，不要把另一個 container 的 `localhost` 當成對方。

### .NET 10 multi-stage Dockerfile

以下 Dockerfile 對應 `examples/ContainerApi`：

```dockerfile
# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["ContainerApi.csproj", "./"]
RUN dotnet restore "ContainerApi.csproj"

COPY . .
RUN dotnet publish "ContainerApi.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "ContainerApi.dll"]
```

每一段的責任：

1. `sdk:10.0` 提供 restore、compile、publish 所需的 SDK。
2. 先只複製 `.csproj`，讓 dependency restore layer 可以被 Docker cache 重用。
3. `dotnet publish` 產生 Release output。
4. `aspnet:10.0` 只提供 ASP.NET Core runtime。
5. `USER $APP_UID` 讓 process 不以 root 執行。
6. `EXPOSE` 是 image metadata；host mapping 要在 `docker run -p` 或 Compose 設定。

### Build、run、log、stop

```bash
docker build --tag container-api:10.0 .
docker image ls container-api

docker run --rm \
  --name container-api \
  --publish 8080:8080 \
  --env ASPNETCORE_ENVIRONMENT=Production \
  container-api:10.0
```

`8080:8080` 是 `host port:container port`。另一個 terminal 可以檢查：

```bash
curl http://localhost:8080/health
docker ps
docker logs container-api
docker stop container-api
```

`--rm` 會在 container 停止後移除 container；它不會刪 image，也不會刪 named volume。

### Configuration 和秘密

ASP.NET Core configuration 可以從 environment variable 取得：

```bash
docker run --rm \
  --publish 8080:8080 \
  --env ConnectionStrings__Orders="由部署環境注入" \
  --env Logging__LogLevel__Default=Information \
  container-api:10.0
```

`__` 會對應到 configuration section 的 `:`。不要把 password、token 或 private certificate 寫進 Dockerfile、source control、build layer 或 application log。

### Volume 和 health check

```bash
docker volume create container-api-data
docker run --rm \
  --mount source=container-api-data,target=/data \
  container-api:10.0
```

application 可以提供 health endpoint：

```csharp
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
```

health endpoint 不要只測 process 還活著；若它宣稱 ready，應依需求檢查必要的 database / dependency，但不要讓短暫的外部服務中斷造成整個 deployment 無限重啟。

## 3. 實務範例：把 ASP.NET Core 放進 container

範例專案在 `examples/ContainerApi`：

```text
examples/ContainerApi/
├── ContainerApi.csproj
├── Program.cs
├── Dockerfile
└── .dockerignore
```

本機先確認 application：

```bash
dotnet run --project examples/ContainerApi/ContainerApi.csproj --urls http://localhost:5080
curl http://localhost:5080/health
```

再建立 image：

```bash
cd examples/ContainerApi
docker build -t container-api:10.0 .
docker run --rm -p 8080:8080 container-api:10.0
curl http://localhost:8080/health
```

Docker 內的 application listening port、host port 和 browser URL 要分開思考：container 內只需要聽 `8080`，host 可以選 `8080`、`18080` 或其他尚未使用的 port。

## 4. 常見誤解

- `EXPOSE 8080` 不等於 host 已經可以用 `localhost:8080` 連線；還需要 `-p 8080:8080`。
- container 內的 `localhost` 指向目前 container，不是 host，也不是 Compose 裡的另一個 service。
- image 不是 VM；container 通常共用 host kernel，不會替每個 container 開一台完整 guest OS。
- multi-stage build 讓 final image 根本不包含 SDK、原始碼和 restore cache。
- `docker stop` 不等於刪掉 volume；資料是否持久化取決於 volume mapping 和服務本身的寫入位置。
- `latest` 不是可重現版本。教材和 production deployment 應使用明確 tag。
- `USER $APP_UID` 可能讓原本依賴 root 寫入 `/app` 的程式失敗；要把可寫資料放到正確 volume。
- Docker image 裡的 secret 即使之後刪掉 Dockerfile 行，也可能留在舊 layer；secret 外洩時要撤銷並重建 image。

## 5. 面試怎麼回答

> Docker image 是包含應用程式和 runtime 檔案的 immutable layer 集合，container 是 image 的執行個體。對 .NET 我會用 multi-stage build：前段使用 SDK restore、build、publish，後段只使用 ASP.NET Core runtime 和 publish output，並以 non-root user 執行。設定和秘密在 runtime 透過 environment、secret store 或部署平台注入；資料庫和上傳檔案則使用 volume 或外部 managed service。`EXPOSE` 只是 metadata，真正的 host port mapping 由 `docker run -p` 或 Compose 完成。

## 6. 小練習

1. 把 `examples/ContainerApi` 的 host port 從 `8080` 改成 `18080`，container port 保持 `8080`。
2. 解釋為什麼 `COPY *.csproj` 和 `dotnet restore` 可以形成獨立 cache layer。
3. 找出一個不能以 root 執行的檔案寫入需求，設計 volume 和 directory permission。
4. 為 container 加入 `/health` check，區分 process alive 和 application ready。
5. 將 image tag 從 `latest` 改成 `10.0.0-<git-sha>`，說明這對 rollback 的幫助。
