---
title: 情境 04 Controller 變成大型雜物間
tags: [scenario, dependency-injection, ioc, service, repository, aspnet-core]
---

# S04：Controller 變成大型雜物間

## 工單 #1873

商品建立頁已經能正常 redirect。接著 PM 加了一個需求：建立訂單時，依客戶等級計算折扣，呼叫運費服務，寫入訂單，最後寄一封確認信。

你打開 OrderController。

商品查詢、客戶查詢、折扣計算、權限判斷、訂單寫入和寄信已經全部在裡面。這次如果再加四十行，功能大概還是能跑。

但你先想到的是三個月後的測試：

```text
要測一個折扣規則，必須先準備資料庫、HTTP client、寄信服務和登入資訊。
```

這不是測試工具不夠多。是這個 class 把太多責任和太多具體物件綁在一起。

## 先不要急著抽一堆 interface

第一個想法是把每個 class 都包成 interface。這樣看起來很乾淨，但 interface 數量增加不會自動讓設計變好。

先從 use case 的動作切開：

```text
Controller：HTTP 邊界、輸入與回應
Service：建立訂單這個 use case、折扣規則、流程順序
Repository：訂單與商品的資料存取
外部 client：運費服務
通知服務：寄信或其他通知
```

每一層只需要依賴它真正使用的協作者。資料庫查詢不應該散在 Controller 和 Service 的每個角落；外部 HTTP 呼叫也不應該直接寫在 action 裡。

這時候才碰到一個問題：OrderService 需要這些協作者，誰來建立它們？

## 讓物件說出自己的依賴

一個 Service 如果需要訂單 Repository、折扣規則、運費 client 和時鐘，這些需求應該出現在 constructor。物件建立完成時，它需要的東西就應該已經存在。

這種方式叫 constructor injection。

Controller 不再自己建立 Service，也不需要知道 Repository 具體使用哪個資料來源。它只宣告自己需要 IOrderService。

這裡的 IoC 不是另一套神秘機制。原本由 class 自己決定「我要 new 哪個協作者」，現在把建立物件和組合相依關係的責任交給 application 的組裝位置。

那個組裝位置就是 Program.cs 裡的 DI container registration。

```text
Program.cs
    ↓ 建立 IOrderService
OrderService
    ↓ 需要
IOrderRepository、IFreightRateClient、IClock
```

Controller 不需要呼叫 Service Locator，也不需要到處拿 IServiceProvider 自己 resolve。依賴直接留在 constructor，物件的需求看得見。

## 這次要選哪些生命週期？

OrderController、OrderService 和 Repository 都跟著一次 HTTP request 工作。使用 EF Core 的 DbContext 時，通常讓它在一個 request scope 內被建立和釋放。

這裡先做三個決定：

| 物件 | 生命週期方向 | 原因 |
| --- | --- | --- |
| DbContext | Scoped | tracking state 和資料庫工作跟 request scope 綁定 |
| OrderService | Scoped | 使用同一個 request 內的資料存取依賴 |
| 不保存狀態的時鐘或格式化服務 | Singleton 或 Transient，依實際 contract | 不需要持有 request 資料 |

不能讓 Singleton 長期抓住 Scoped service。Singleton 會比 request 活得久，裡面若保存 DbContext 或其他 scoped state，生命週期和執行緒安全都會出問題。

BackgroundService 也是長生命週期物件。之後如果訊息 consumer 需要 DbContext，不能直接把 request-scoped 物件塞進去，而要在每筆工作建立明確的 scope。

## 把組裝集中起來

這次先把註冊放在 application composition root：

```text
IOrderService       → OrderService       → Scoped
IOrderRepository    → EfOrderRepository  → Scoped
IFreightRateClient  → typed client        → 由 HttpClientFactory 管理
IClock              → SystemClock         → 依是否保存狀態決定
```

如果未來要用 fake repository 測試 Service，只需要替換這個 registration 或在測試 host 中覆寫它。Service 本身不需要知道目前是 SQL Server、SQLite 還是測試資料。

這就是 DI 真正幫到的地方：不是少寫幾個 `new`，而是把物件建立、生命週期和替換邊界集中管理。

## 仍然有一個取捨

不是每個 class 都要有 interface。

折扣計算如果只是 OrderService 裡一段不需要替換的純邏輯，直接留在 Service 可能比較清楚。資料庫、外部 HTTP、時鐘和通知則通常是副作用邊界，替換它們對測試和架構比較有價值。

Repository 也不是每張 table 都必須包一層。小型 app 可以讓 Service 直接使用 DbContext；需要隔離資料來源、集中查詢或保護 persistence contract 時，再抽出 Repository。

抽象的數量應該跟變動點和測試邊界一起決定，不是看起來越多越好。

## Controller 縮回 HTTP 邊界

OrderController 現在只留下：

```text
接收 request
檢查 ModelState
呼叫 IOrderService
把結果轉成 View、redirect 或 status code
```

折扣、查庫存、建立 Entity、保存資料、取得運費和發通知都離開 action。測試折扣規則時，可以只準備 Service 需要的 fake；測試 Controller 時，則只驗證 HTTP 行為。

這個分層還沒有讓所有問題消失。Service 下一步要呼叫外部運費 API，而外部服務的速度、狀態碼和斷線方式都不受這個 application 控制。

下一張工單已經進來了：

```text
運費服務有時 3 秒才回應，客服頁面會一起卡住。
```

## 回原教材查什麼

| 原教材 | 這張工單碰到的內容 | 涵蓋方式 |
| --- | --- | --- |
| 02 型別設計 | interface、constructor、readonly、Entity 與 service contract | 透過拆分責任帶出 |
| 09 Dependency Injection | constructor injection、IoC、DI container、Singleton／Scoped／Transient | 透過生命週期決策帶過 |
| 13A ASP.NET Core 架構 | composition root、Program.cs、host 與 service wiring | 留在系統組裝問題裡 |
| 14 WebApplicationFactory | 替換測試服務、HTTP 邊界 | 留給下一階段測試工單 |

完整回讀仍放在原教材：ValidateScopes、ValidateOnBuild、IServiceScopeFactory、Options、keyed services，以及 DI 與 Service Locator 的差異。
