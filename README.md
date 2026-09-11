---
title: 現代 C# .NET ASP.NET Core 學習筆記
tags: [csharp, dotnet, aspnet-core, learning]
---

# 現代 C# / .NET / ASP.NET Core 學習筆記

這是一份從基礎物件導向概念出發，學習現代 C#、.NET、ASP.NET Core、SQL Server 與 EF Core 的系統化教材。

學習路線不從變數、`if`、`for` 開始，而是：

> C# 型別與集合 → .NET 慣用 API → ASP.NET Core 實際情境 → SQL Server 與 EF Core

## 版本基準

- 教材基線：`.NET 8+`、C# 12+、ASP.NET Core 8+。
- 現行文件基線：截至 2026-09，`.NET 10` 是 LTS，C# 14 隨 .NET 10 發布；教材會標註少數只與 C# 14 有關的延伸功能，但核心範例刻意保持 .NET 8+ 可讀、可移植。
- 若公司仍使用 .NET 8，不需要為了本教材改用 .NET 10；`record`、nullable、LINQ、`async/await`、內建 DI、`IHttpClientFactory` 等主題都可直接套用。

## 建議閱讀順序

1. [[01-型別與值語意]]：先建立 C# 的型別與值語意模型。
2. [[02-型別設計]]：看懂 properties、records、`init`、`readonly`。
3. [[03-Generics與Collections]]：理解 interface collection 的 API 設計意圖。
4. [[04-LINQ]] → [[05-IEnumerable與IQueryable]]：這是讀 EF Core code 的關鍵。
5. [[06-非同步程式設計]] → [[07-CancellationToken]] → [[08-資源管理]]。
6. [[09-DependencyInjection]] → [[10-HttpClient]] → [[11-Stream]] → [[12-JSON與序列化]]。
7. [[13-ASP.NET-Core架構]]：先建立 host、middleware 和 endpoint foundation。
8. [[13-ASP.NET-Core-MVC與Razor-Views]]：集中學 MVC、Razor、routing、model binding、validation、PRG 和 Anti-Forgery。
9. [[15-ASP.NET-Core-MVC-CRUD]]：實際跑 Product MVC + EF Core CRUD，再讀 [[15-綜合實作]] 的 Users Web API。
10. [[14-WebApplicationFactory]]：用 HTTP 測試 MVC / API integration behavior。
11. 最後用 [[16-面試快速複習]]、[[16-ASP.NET-Core-MVC面試實戰]] 和 [[17-學習分級與路線圖]] 收斂。
12. MSSQL 基礎：[[18-SQLServer資料型別]] → [[19-SQLServer-NULL與三值邏輯]] → [[20-SQLServer-JOIN]] → [[21-SQLServer-Subquery-CTE-View]] → [[22-SQLServer-Window-Functions]]。
13. SQL 效能與併發：[[23-SQLServer-Index]] → [[24-Execution-Plan與Query-Performance]] → [[25-SQLServer-Transaction與Isolation]] → [[26-SQLServer-Lock-Blocking-Deadlock]]。
14. SQL Server + EF Core：[[27-SQLServer-Programmability-Temp-Pagination]] → [[28-EF-Core-Query與Methods]] → [[29-EF-Core-Related-Data-Tracking-N+1]] → [[30-EF-Core-Concurrency與Migration]] → [[31-User-Order整合實作]]。
15. 最後用 [[32-MSSQL面試快速複習]] 和 [[33-MSSQL學習優先級]] 收斂。

## 目錄

| 章節 | 主題 | 讀完後能做什麼 |
| --- | --- | --- |
| 01 | [[01-型別與值語意]] | 分辨 value/reference type、nullable、時間型別與 `var` |
| 02 | [[02-型別設計]] | 看懂 class、record、interface、property 與初始化語法 |
| 03 | [[03-Generics與Collections]] | 選擇適合的 collection abstraction 與 method signature |
| 04 | [[04-LINQ]] | 用 LINQ 篩選、排序、分組與轉換資料 |
| 05 | [[05-IEnumerable與IQueryable]] | 判斷查詢在記憶體還是 SQL Server 執行 |
| 06 | [[06-非同步程式設計]] | 正確閱讀與撰寫 `Task` / `async` / `await` |
| 07 | [[07-CancellationToken]] | 讓 request cancellation 從 HTTP 傳到 DB / HTTP 呼叫 |
| 08 | [[08-資源管理]] | 正確使用 `IDisposable`、`using`、`await using` |
| 09 | [[09-DependencyInjection]] | 理解 ASP.NET Core DI 與 service lifetime |
| 10 | [[10-HttpClient]] | 使用 `IHttpClientFactory` 呼叫外部 API |
| 11 | [[11-Stream]] | 看懂檔案、request body、response body 與 upload |
| 12 | [[12-JSON與序列化]] | 理解 JSON、DTO、model binding 與 serializer |
| 13 | [[13-ASP.NET-Core架構]] | 把 host、middleware、routing、service、DB 串成 request pipeline |
| 13A | [[13-ASP.NET-Core-MVC與Razor-Views]] | 寫出 MVC controller、Razor View、表單 validation、PRG 與 Tag Helpers |
| 14 | [[14-WebApplicationFactory]] | 用 integration test 透過 HTTP 測試 API |
| 15 | [[15-綜合實作]] | 閱讀並實作一個小型 Users Web API |
| 15A | [[15-ASP.NET-Core-MVC-CRUD]] | 完成 Product MVC + EF Core 的 List / Details / Create / Edit / Delete |
| 16 | [[16-面試快速複習]] | 在 30 秒內回答常見 C# / .NET / ASP.NET Core 問題 |
| 16A | [[16-ASP.NET-Core-MVC面試實戰]] | 回答 MVC、Razor、Model Binding、Validation、PRG 和 CSRF 面試題 |
| 17 | [[17-學習分級與路線圖]] | 判斷現在一定要會、知道即可、之後再學的內容 |
| 18 | [[18-SQLServer資料型別]] | 選擇 SQL Server type 並對應 C# / EF Core |
| 19 | [[19-SQLServer-NULL與三值邏輯]] | 正確理解 NULL、UNKNOWN、ISNULL、COALESCE |
| 20 | [[20-SQLServer-JOIN]] | 看懂 JOIN row shape 與 ON / WHERE 差異 |
| 21 | [[21-SQLServer-Subquery-CTE-View]] | 選擇 subquery、CTE、view、derived table |
| 22 | [[22-SQLServer-Window-Functions]] | 使用排名、最新一筆、top-N、LAG / LEAD |
| 23 | [[23-SQLServer-Index]] | 設計 clustered、composite、covering index |
| 24 | [[24-Execution-Plan與Query-Performance]] | 從 execution plan 與 SARGability 診斷效能 |
| 25 | [[25-SQLServer-Transaction與Isolation]] | 理解 ACID、isolation 與 transaction scope |
| 26 | [[26-SQLServer-Lock-Blocking-Deadlock]] | 分辨 blocking、deadlock 與 concurrency strategy |
| 27 | [[27-SQLServer-Programmability-Temp-Pagination]] | 比較 DB logic、中間結果工具與 pagination |
| 28 | [[28-EF-Core-Query與Methods]] | 讀懂 DbContext、DbSet、常見 async methods |
| 29 | [[29-EF-Core-Related-Data-Tracking-N+1]] | 避免 N+1，選擇 Include、projection、tracking |
| 30 | [[30-EF-Core-Concurrency與Migration]] | 處理 rowversion、migration 與 schema deployment |
| 31 | [[31-User-Order整合實作]] | 完成 Users / Orders / OrderItems 整合設計 |
| 32 | [[32-MSSQL面試快速複習]] | 快速回答 SQL、Index、Transaction、EF Core 題 |
| 33 | [[33-MSSQL學習優先級]] | 排定 A / B / C 學習優先級 |
| 99 | [[99-參考資料]] | 依章節快速找到官方文件與補充書籍 |

## C# → ASP.NET Core 知識地圖

```text
C# type system / record / nullable / interface
                │
                ├── Task / async / CancellationToken ──┐
                ├── LINQ / IEnumerable / IQueryable ───┤
                ├── IDisposable / Stream / JSON ───────┤
                └── DI registration / lifetime ─────────┘
                                │
                        ASP.NET Core host
                Program.cs / middleware / routing
                                │
             MVC controller / Razor View       API controller / Minimal API
                       │                                  │
              ViewModel → HTML                 DTO → JSON → HTTP response
                       └──────── service → repository / HttpClient ────────┘
```

## C# / ASP.NET Core → SQL Server 知識地圖

```text
HTTP endpoint
    ↓
Service / use case
    ↓  DI + CancellationToken
EF Core DbContext / DbSet
    ↓  IQueryable + LINQ expression tree
Generated SQL
    ↓
SQL Server optimizer
    ├── index seek / scan / lookup
    ├── join / sort / aggregate / window
    └── transaction / isolation / lock
    ↓
Rows → EF materialization / tracking
    ↓
Projection DTO → JSON response
```

看到一條 data access code 時，依序問：

1. `ToListAsync` / `FirstAsync` 在哪裡觸發 SQL？
2. query 是 read-only 還是要追蹤並更新 entity？
3. SQL 的 filter / join / order 是否有合理 index？
4. 是否有 N+1、過早 materialization、deep OFFSET 或不必要的欄位？
5. 併發下 transaction、isolation、rowversion 或 deadlock 怎麼處理？

## 參考資料

官方文件優先；其他書籍用來補充語言與 API 的細節。

- [Microsoft Learn — C# guide](https://learn.microsoft.com/en-us/dotnet/csharp/)
- [Microsoft Learn — ASP.NET Core fundamentals](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/?view=aspnetcore-10.0)
- [Microsoft Learn — .NET releases and support](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)
- [Microsoft Learn — What's new in .NET 10](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview)
- [C# in a Nutshell](https://www.albahari.com/nutshell/)
- [C# Yellow Book](https://www.robmiles.com/c-yellow-book/)

## 使用方式

每章中的 code block 優先追求「短、可以放進真實 ASP.NET Core 專案、能直接解釋設計意圖」。複製 Web API 範例時可用 `dotnet new webapi`；MVC 範例可用 `dotnet new mvc`，完整 Product CRUD 的檔案拆分見 [[15-ASP.NET-Core-MVC-CRUD]]。
