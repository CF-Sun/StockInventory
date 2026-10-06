# 專案說明(給 AI 與開發者)

- 規格見 `SPEC.md`,依其 §14 階段 P0 → P5 逐步實作;規格是唯一來源,衝突時以規格為準(§0)。
- 標示 `[VERIFY]` 的項目必須先實測,結果寫在 `docs/verify-notes.md`;目前尚未實測的項目都列在那裡。
- 測試:`dotnet test`(Core、Quotes、Web 三個測試專案)。Web 測試用 InMemory 資料庫,沒有連真正的 SQL Server。
- 機敏資訊只放環境變數(連線字串、`Smtp:Password`、`Seed:*`),不可寫進原始碼或 `appsettings.json`。
- 介面文字與使用者可見訊息用繁體中文;程式識別字、資料庫物件、API 欄位用英文。
