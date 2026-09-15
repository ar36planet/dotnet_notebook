---
title: 情境 05 運費服務變慢，整個頁面 timeout
tags: [scenario, async, cancellation, httpclient, json, aspnet-core]
---

# S05：運費服務變慢，整個頁面 timeout

## 工單 #1881

S04 的 OrderService 已經從 Controller 拆出來了。建立訂單前還要先取得運費。

運費服務平常會回應，偶爾卻會等很久。客服描述的不是錯誤訊息，而是：

```text
按下建立訂單
    ↓
頁面一直轉
    ↓
最後整個 request timeout
```

你先看 request timeline，發現本機沒有忙著算東西，大部分時間都在等外部 HTTP response。

目前有三個方向：

```text
A. 把同步呼叫包進 Task.Run。
B. 把 HTTP timeout 調得更長，讓它多等一下。
C. 讓 HTTP 呼叫從 Controller 到 client 都是非同步，並且傳遞 request cancellation。
```

A 會把同步等待搬到 thread pool，沒有讓外部服務變快；B 只能延後使用者看到錯誤的時間。這張工單選 C，但選完還要處理 client 的生命週期、狀態碼和 response body。

## 先把等待方式改對

`Task<T>` 表示一個尚未完成或已完成的非同步操作，以及它完成後會產生的結果。它不是一條 thread。

`await` 等待 I/O 時，如果工作尚未完成，方法可以把控制權交還給呼叫端；I/O 完成後，再從原本的位置接著執行。

`.Result` 和 `.Wait()` 則是同步等待。它們會讓目前執行緒停在那裡，不能因為最後仍然拿到相同結果，就把兩種等待方式視為一樣。

這裡也不需要把所有工作丟進 `Task.Run`。HTTP、資料庫和檔案 API 本來就有非同步版本，直接一路 await 才能讓每一層的取消和例外保持清楚。

## Request 已經消失了，工作還在跑

客服按下送出後關閉瀏覽器，這個 HTTP request 已經沒有使用者在等結果。

ASP.NET Core 會透過 `RequestAborted` 發出 cancellation signal。它不是強制殺掉 thread；每一層要把 token 傳給支援 cancellation 的 API，讓工作在安全檢查點停止。

這條路徑要完整：

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

如果 Service 把 token 換成 `CancellationToken.None`，下面的 HTTP 呼叫就不會知道 request 已經取消。資料庫、stream 或其他 I/O 也可能因此繼續做一個沒人要的工作。

## 每次 new HttpClient 可以嗎？

第一版的 OrderService 每次執行都自己建立 HttpClient。

這種寫法在低頻測試通常看不出問題。高頻呼叫時，連線池、socket、port 和 DNS 更新都變成每次 request 自己處理的問題。另一個極端是建立一個長壽命 client，卻沒有設定連線更新策略，外部服務的 DNS 變更可能很久才被看見。

這次是單一、明確的運費 API，選 typed client：

```text
OrderService
    ↓
FreightRateClient
    ↓
HttpClientFactory 管理的 HttpClient
    ↓
運費服務
```

外部 API 的 base address、handler、timeout 和 resilience 設定集中在 client registration；OrderService 只處理建立訂單需要的運費結果。

另一條可行路線是 static 或 singleton HttpClient 搭配 SocketsHttpHandler 的 PooledConnectionLifetime。這張工單選 typed client，因為它同時把外部 API 的契約和操作集中起來。

## 404 是錯誤嗎？

運費服務可能回傳幾種狀態：

```text
200：找到可用費率
404：這個郵遞區號沒有可用費率，或資源不存在
503：運費服務暫時不可用
```

先決定業務語意，再決定例外處理。若 404 代表「沒有可用費率」，它可以轉成 null 或明確的 domain result；503 則不能和 404 混在一起。timeout、連線失敗、JSON 格式錯誤也各自有不同的處理方式。

你原本想直接使用 `GetFromJsonAsync`。它很方便，但非成功 status 通常會拋出 `HttpRequestException`，不會替你判斷 404 對這個業務是不是正常結果。

所以 client 改成先拿 response，再判斷 status：

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

`FreightRateClient` 只把明確的 404 轉成 null。其他非成功狀態仍然往上拋，讓 application 層決定要回應錯誤、暫時重試，還是停止建立訂單。

## Timeout 不是 Retry

這次只是讀取運費，外部呼叫沒有寫入付款或建立訂單。若把 timeout 後的呼叫重試，通常比重試付款安全，但仍然要看服務是否真的冪等、重試會不會造成額外成本，以及 request 是否已經取消。

`HttpClient.Timeout` 可以提供整體時間上限。若使用 `ResponseHeadersRead`，它通常只涵蓋取得 headers 的階段，response body 的讀取還要使用 cancellation token 或另外的 timeout。這個邊界先記在原教材，等檔案下載工單再一起處理。

timeout 和 caller cancellation 都可能表現成 `TaskCanceledException`。不能看到這個 exception 就一律當成伺服器故障；要先判斷 caller token 是否已經取消，並決定 log 等級。

## JSON 只是傳輸格式

運費服務回傳 JSON，`FreightQuote` 是 application 內部使用的 DTO。序列化器只負責格式轉換，不會替你判斷：

- 金額是否符合訂單規則。
- 服務等級是否允許使用。
- 使用者是否有權限使用這個地址。
- 外部 response 是否符合目前版本的契約。

同樣的分界也適用於 ASP.NET Core 的 request body 和 response。JSON formatter 可以把資料轉成物件，但 validation、authorization、transaction 和敏感欄位處理仍由 application 自己負責。

## 工單交接

```text
已完成：

1. 外部 HTTP 呼叫使用 async API，不用 Result 或 Wait 阻塞。
2. RequestAborted 一路傳到 FreightRateClient。
3. 運費 API 使用 typed client，交由 HttpClientFactory 管理。
4. 404、其他非成功 status、timeout、cancellation 和 JSON 錯誤分開處理。
5. 已確認 timeout 和 retry 是兩個不同決策，不能直接綁在一起。
```

運費 client 現在可以被 OrderService 使用。下一張工單是財務上傳發票：下載回來的檔案是空的，某些 request 中斷後暫存檔也沒有釋放。

## 回原教材查什麼

| 原教材 | 這張工單碰到的內容 | 涵蓋方式 |
| --- | --- | --- |
| 06 非同步程式設計 | Task、async／await、I/O 等待、避免 sync-over-async | 透過 timeout 事故帶出 |
| 07 CancellationToken | RequestAborted、合作式取消、取消例外 | 透過 request 中斷帶出 |
| 09 Dependency Injection | typed client 的組裝位置 | 接續 S04 的 DI 邊界 |
| 10 HttpClient | HttpClientFactory、typed client、timeout、status code、DNS／handler lifetime | 透過外部服務整合帶過 |
| 12 JSON 與序列化 | DTO、JSON response、反序列化、formatter | 透過外部契約帶出 |
| 13A ASP.NET Core 架構 | Controller、Service、設定與例外處理 | 接回前兩張工單 |

完整回讀仍放在原教材：ValueTask、標準 resilience handler、ResponseHeadersRead 的 timeout 邊界、static client 搭配 PooledConnectionLifetime、Newtonsoft.Json 與 System.Text.Json 的差異，以及 JSON options 和 source generation。

---

[[scenario/04-Controller變成大型雜物間|← 上一章：S04 Controller 變成大型雜物間]] · [[scenario/index|回到情境教材目錄]] · 下一章：S06 尚未建立
