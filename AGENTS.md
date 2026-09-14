# AGENTS.md

給在這個 repo 工作的 agent。這裡寫的是筆記的體裁與範例標準，不是 code style。

## 這份筆記是什麼

作者自己要讀的 .NET 學習教材，同時放在 GitHub Pages（https://ar36planet.github.io/dotnet_notebook）。是教材，不是部落格，也不是日記。

- 不要寫敘事開場。「我第一次讀這段卡住」「同事跟我說」這種句子，就算事情真的發生過也是雜訊——讀的人是來查東西的。
- 小節開頭直接給結論，或直接進 code block，跟同檔案其他小節保持一致。
- 第一人稱只出現在〈面試怎麼回答〉。那一段是講稿，「我會根據…」是對的。
- 不寫感想。「這種東西要找很久」刪掉；前一句把現象講完就夠了。

## 範例的標準

1. **先給會出事的場景，再講規則**，不要「規則 → 示範」。ch1 的 UTC+8 差八小時、ch5 的 LINQ 翻譯失敗在查詢執行時拋出例外、ch5 的 `ToList()` 放錯位置變成整表掃描，都是這個形狀。
2. **範例要編譯並跑過才寫下來。** 本機有 dotnet 10.0.400。輸出、SQL、例外訊息一律貼實際跑出來的內容，不要憑印象寫示意的 SQL。
3. **貼出來的東西要能對照。** 講 EF Core 就把 log 出來的 SQL 貼上；講例外就貼完整訊息，不要只寫「會 throw」。
4. **用具體的 domain。** 訂單、帳號、地址、金額。不要 `foo`／`bar`，不要 `IsActive`／`Name` 這種沒有情境的欄位，不要 `=> new(42)`。
5. **數字要帶條件。** 「三筆看不出差別，三十萬筆是另一回事」可以；沒跑過的效能數字不要寫。

## 改寫時

- 刪多於加。段落改完不該比原來長；同一個點在兩節各講一次的，砍掉一處。
- 保留章節模板與順序：frontmatter（`title`、`tags`）→ 學習目標 → 1. 一句話理解 → 2. Java 對照 → 3. C# 語法 → 4. 實務範例 → 5. 常見誤解 → 6. 面試怎麼回答 → 7. 小練習。
- 英文術語只留 API 名稱和沒有通用中譯的字（`IQueryable`、expression tree）。能講中文的講中文。
- 動散文時走 sepia skill：`/sepia-refactor` 做小幅修訂，`/sepia-review` 只診斷不改。

## 動手前後

```bash
# 跑範例：開在 scratchpad，不要留在 repo 裡
dotnet new console -o <name> && dotnet run

# 確認頁面沒壞（public/ 已 gitignore）
npm run quartz -- build --directory .
```

`examples/` 底下是完整的可執行專案，Quartz 不會把它當成頁面。

## 發布

push 到 `main` 會觸發 `.github/workflows` 部署到 GitHub Pages，約一分鐘。其他分支不會發布。頁面 slug 是檔名轉小寫（`05-IEnumerable與IQueryable.md` → `/05-ienumerable與iqueryable`）。
