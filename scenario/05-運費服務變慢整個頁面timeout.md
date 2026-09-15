---
title: 情境 05 運費服務變慢，整個頁面 timeout
tags: [scenario, async, cancellation, httpclient, json, aspnet-core]
---

# S05：運費服務變慢，整個頁面 timeout

## 工單 #1881

OrderService 已經從 Controller 拆出來了。下一個需求是建立訂單前先取得運費。

運費服務的 endpoint 平常很快，偶爾會卡住。客服回報的畫面是：

```text
按下建立訂單
    ↓
頁面轉圈
    ↓
過一段時間才看到錯誤
```

你看目前的呼叫方式，發現 action 會同步等待外部 HTTP 結果。外部服務還沒回來時，這個 request 就一直占著處理工作。

先把這次事故拆開：

- 外部服務慢，不代表本機 CPU 忙。
- 等待 HTTP response 時，不需要讓 request thread 一直卡住。
- 使用者關掉頁面後，原本的工作不一定還值得繼續。
- 服務回傳 404、409、503，業務意義不一樣。

## 先處理等待方式

`Task<T>` 表示一個尚未完成或已完成的非同步操作，以及它完成後會產生的結果。它不是一條 thread。

`await` 等待 I/O 時，如果工作尚未完成，方法可以把控制權交還給呼叫端；I/O 完成後，再從原本的位置接著執行。這和用 `.Result` 或 `.Wait()` 把目前執行緒堵住是不同的事情。

所以這裡不是把所有工作都丟進 `Task.Run`。HTTP、資料庫和檔案 API 本來就提供非同步版本，直接一路 await 即可。

## 第二個問題：每次呼叫都建立 HttpClient

第一版為了快速完成，OrderService 每次執行都自己建立一個 HttpClient。

短時間測試通常看不出問題。高頻呼叫時，每個 client 可能各自管理 connection pool；連線反覆建立和釋放，會讓 socket、port、DNS 更新與 handler 生命週期變得難以控制。

這裡改用 typed client，把一個外部 API 的設定和操作集中在一個 class：

```text
OrderService
    ↓
FreightRateClient
    ↓
HttpClientFactory 管理的 HttpClient
    ↓
運費服務
```

Service 不需要知道 base address 怎麼設定，也不需要自己決定 handler 何時替換。

## 第三個問題：request 已經取消了，工作還在跑

客服按下送出後關閉瀏覽器，HTTP request 的 `RequestAborted` token 會收到取消通知。

取消不是強制終止 thread。每一層都要把 token 傳給支援 cancellation 的 API，讓工作在安全的檢查點停止。

這條路徑要保持完整：

```text
HTTP request cancellation
    ↓
Controller action 的 CancellationToken
    ↓
OrderService
    ↓
FreightRateClient
    ↓
HttpClient request
```

如果 Controller 收到了 token，卻在 Service 裡換成 `CancellationToken.None`，取消就被截斷了。資料庫和後續寫入也要考慮是否使用同一個 token。

## 把外部回應包起來

運費服務回傳的是外部契約，不要讓 OrderService 直接處理一個未命名的 JSON object。先替它建立明確的 client response DTO，再由 client 處理 HTTP 細節。

client 需要做的事包括：

- 建立 request URL。
- 傳入 cancellation token。
- 判斷 404 是否代表「沒有可用運費」還是錯誤。
- 對其他非成功狀態保留錯誤資訊。
- 將 response body 反序列化成 DTO。

`GetFromJsonAsync` 對非成功 status 通常會拋出 HttpRequestException。若業務需要把 404 當成「找不到費率」，就要先取得 response、判斷 status，再讀取 JSON，不能期待便利方法替你做業務判斷。

## 這次先寫一個 client 邊界

下面的形狀只處理外部 API，不把折扣規則塞回 client：

```csharp
using System.Net;
using System.Net.Http.Json;

public sealed record FreightQuote(
    decimal Amount,
    string ServiceLevel);

public sealed class FreightRateClient(HttpClient client)
{
    public async Task<FreightQuote?> GetAsync(
        string postalCode,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"rates?postalCode={Uri.EscapeDataString(postalCode)}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<FreightQuote>(
            cancellationToken);
    }
}
```

`FreightRateClient` 回傳 null 的條件是外部服務明確回 404，不是所有錯誤都吞成 null。timeout、連線失敗、503 和 JSON 格式錯誤，仍然要交給上層按照 application policy 處理。

DI registration 則放在組裝位置：

```text
FreightRateClient
    BaseAddress：由設定取得
    Timeout：依運費服務的 SLO 決定
    Handler：由 HttpClientFactory 管理
```

timeout 不是 retry policy。是否重試要看 HTTP method、外部操作是否冪等，以及重試會不會讓同一個業務動作執行兩次。這張工單目前是讀取運費，後續付款或建立外部訂單時不能直接照搬同一套 retry。

## JSON 也有自己的邊界

運費服務的 JSON 是傳輸格式，FreightQuote 是 application 內部使用的 DTO。序列化器只負責把資料轉換，不會替你判斷：

- 金額是不是合理。
- 服務等級是不是允許的值。
- 這個客戶有沒有權限使用這個地址。
- 外部服務回傳的欄位是否符合目前版本的契約。

ASP.NET Core 的 request body、action parameter 和 response DTO 也會經過自己的 model binding 與 JSON formatter。不要因為 API 可以自動反序列化，就把外部 response、資料庫 Entity 和公開 response DTO 混成同一個型別。

## 這次事故的處理結果

目前先完成四個決定：

```text
1. 外部 HTTP 呼叫使用真正的 async API，不用 Result 或 Wait 阻塞。
2. HttpClient 交給 IHttpClientFactory 管理，外部 API 用 typed client 封裝。
3. RequestAborted 一路傳到 HttpClient、資料庫和其他可取消的 I/O。
4. 404、非成功狀態、timeout、cancellation 和 JSON 錯誤分開處理。
```

頁面不再因為外部服務等待而把同步流程整段卡住。但新工單已經排進來：財務要上傳發票檔案，下載回來卻是空的；而且某些 request 中斷後，暫存檔沒有被釋放。

## 回原教材查什麼

| 原教材 | 這張工單碰到的內容 | 涵蓋方式 |
| --- | --- | --- |
| 06 非同步程式設計 | Task、async／await、I/O 等待、避免 sync-over-async | 透過 timeout 事故帶出 |
| 07 CancellationToken | RequestAborted、合作式取消、取消例外 | 透過 request 中斷帶過 |
| 10 HttpClient | HttpClientFactory、typed client、timeout、status code、DNS／handler lifetime | 透過外部服務整合帶過 |
| 12 JSON 與序列化 | DTO、JSON request／response、反序列化、formatter | 透過外部 response 邊界帶出 |
| 13A ASP.NET Core 架構 | Controller、Service、設定與 exception handling | 接回前一張工單 |

完整回讀仍放在原教材：ValueTask、HttpClient resilience handler、ResponseHeadersRead 的 timeout 邊界、Newtonsoft.Json 與 System.Text.Json 的完整差異，以及 JSON options 和 source generation。

---

[[scenario/04-Controller變成大型雜物間|← 上一章：S04 Controller 變成大型雜物間]] · [[scenario/index|回到情境教材目錄]] · 下一章：S06 尚未建立
