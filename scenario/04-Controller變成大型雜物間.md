---
title: 情境 04 Controller 變成大型雜物間
tags: [scenario, dependency-injection, ioc, service, repository, aspnet-core]
---

# S04：Controller 變成大型雜物間

## 工單 #1873

S03 的商品建立流程已經可以交付。接著 PM 把建立訂單的需求補上來：

```text
建立訂單前：

1. 查客戶等級。
2. 計算折扣。
3. 查商品和庫存。
4. 取得運費。
5. 寫入 Orders 和 OrderItems。
6. 寄出確認信。
```

你打開 OrderController。

商品查詢、客戶查詢、折扣計算、權限判斷、訂單寫入、運費呼叫和寄信已經都在裡面。這次再加幾十行，功能大概還是能跑。

但測試先卡住了。

測試只想確認「白金客戶買兩件商品時，折扣應該是 10%」。為了建立 Controller，卻得先準備資料庫、HTTP client、寄信服務、登入資訊和時鐘。

這裡有三種處理方式：

```text
A. 保留所有 code 在 Controller，測試把每個依賴都 mock 起來。
B. 把 HTTP 邊界留在 Controller，其他流程移到 Service；資料存取再隔離成 Repository。
C. 每一個 class 都建立 interface，全部交給測試替換。
```

A 會讓測試跟 Controller 的細節綁得很緊。C 會先增加很多名稱，卻還沒有說明哪些東西真的需要替換。這個需求先選 B：先按責任和副作用切開，再決定哪些邊界需要 interface。

## 第一次抽出去，問題還在

你先把折扣計算和資料庫查詢搬進 OrderService，但 Service 裡面直接建立具體的 Repository 和 HttpClient。

Controller 變短了，測試仍然無法只測折扣規則。只要建立 OrderService，就會固定使用真實資料庫和真實 HTTP client。

這裡需要的不是再搬一次 method，而是讓 Service 不負責決定協作者的具體實作。

## 讓物件說出自己的依賴

OrderService 需要什麼，應該直接出現在 constructor：

```csharp
public sealed class OrderService(
    IOrderRepository repository,
    IDiscountPolicy discountPolicy,
    IFreightRateClient freightRateClient,
    IClock clock)
{
    // 建立訂單的 use case 會使用這些依賴。
}
```

這種方式叫 constructor injection。Service 不在 method 裡自己 new Repository，也不在執行時到處向 ServiceProvider 要物件。

這時才需要 DI container。

原本由 OrderService 決定「我要建立哪個 Repository」，現在把物件建立和依賴組合交給 application 的組裝位置。ASP.NET Core 內建的 DI container 會依照 registration 建立物件，並把 constructor 需要的依賴傳進去。

這也是 IoC 的實際樣子：控制物件建立的責任從業務 class 移到 composition root。不是多了一個讓程式自動變好的工具。

## 生命週期先做決定，不要最後才補

這次資料庫使用 EF Core。你把目前會一起工作的物件整理成表格：

| 物件 | 生命週期 | 判斷理由 |
| --- | --- | --- |
| DbContext | Scoped | tracking state 和資料庫工作跟 request scope 綁定 |
| EfOrderRepository | Scoped | 使用 request 內的 DbContext |
| OrderService | Scoped | 使用同一個 request 內的資料存取依賴 |
| 不保存 request 狀態的時鐘 | Singleton 或 Transient | 依實作是否 thread-safe 與是否保存狀態決定 |

Scoped service 不能被長期存在的 Singleton 捕捉。Singleton 會比 request 活得久；如果它保存 DbContext 或其他 scoped state，下一個 request 可能讀到不該共用的狀態，也可能在多執行緒下互相影響。

這不是只有 EF Core 才有的規則。任何依賴 request、scope 或短生命週期資源的物件，都要看清楚被誰持有。

## 把組裝集中起來

Program.cs 是目前的 composition root。服務 registration 的責任先集中在那裡：

```csharp
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
builder.Services.AddScoped<IDiscountPolicy, CustomerDiscountPolicy>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddHttpClient<FreightRateClient>();
```

Service 只依賴 abstraction；哪一個 implementation 被使用，留在組裝位置決定。

測試要換 fake repository 時，可以在測試 host 移除原本 registration，再加入 fake。這個替換點就是 interface 對測試真正有用的地方。

## BackgroundService 又帶來一個 lifetime 問題

下一個版本需要背景工作，定期把未完成的訂單送到通知服務。BackgroundService 通常是長生命週期物件，但通知工作需要 DbContext。

直接把 DbContext 注入 BackgroundService，看起來很方便，生命週期卻不相容。背景工作應該注入 IServiceScopeFactory，每次處理一批工作時建立 scope，工作結束後釋放 scope。

這個細節現在先記下來。RabbitMQ consumer 進場後，會再遇到同一個問題。

## Controller 縮回 HTTP 邊界

重構後，OrderController 只保留：

```text
接收 request
檢查 ModelState
呼叫 IOrderService
把結果轉成 View、redirect 或 status code
```

折扣、查庫存、建立 Entity、保存資料、取得運費和發通知都離開 action。測試折扣規則時，可以只準備 Service 需要的 fake；測試 Controller 時，則驗證 HTTP 行為。

不是所有 class 都需要 interface。折扣計算如果是簡單、穩定、沒有外部副作用的規則，可以直接留在 Service 或抽成具體 policy。資料庫、外部 HTTP、時鐘和通知則是比較明確的替換邊界。

## 工單交接

```text
已完成：

1. Controller 不再直接負責資料存取、折扣計算與外部呼叫。
2. OrderService 透過 constructor 宣告自己的依賴。
3. 物件建立集中在 Program.cs 的 DI registration。
4. DbContext、Repository 和 Service 以 request scope 工作。
5. 已確認 Singleton 不能長期持有 Scoped service。
6. BackgroundService 後續需要自行建立 scope。
```

測試現在可以替換 OrderService 的協作者。外部運費服務卻有另一張工單：服務有時很慢，客服頁面會跟著卡住。

## 回原教材查什麼

| 原教材 | 這張工單碰到的內容 | 涵蓋方式 |
| --- | --- | --- |
| 02 型別設計 | interface、constructor、readonly、Entity 與 service contract | 透過拆分責任帶出 |
| 09 Dependency Injection | constructor injection、IoC、DI container、Singleton／Scoped／Transient | 透過測試與生命週期帶過 |
| 13A ASP.NET Core 架構 | composition root、Program.cs、host 與 service wiring | 透過系統組裝帶出 |
| 14 WebApplicationFactory | 替換測試服務、HTTP 邊界 | 留給測試工單 |

完整回讀仍放在原教材：ValidateScopes、ValidateOnBuild、IServiceScopeFactory、Options、keyed services，以及 DI 與 Service Locator 的差異。

---

[[scenario/03-客服重新整理後訂單多一筆|← 上一章：S03 客服重新整理後訂單多一筆]] · [[scenario/index|情境教材目錄]] · [[scenario/05-運費服務變慢整個頁面timeout|下一章：S05 運費服務變慢，整個頁面 timeout →]]
