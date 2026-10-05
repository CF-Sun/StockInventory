# [VERIFY] 項目實測紀錄

> 狀態:**尚未實測**。開發環境(雲端沙盒)的網路政策擋掉了 `mis.twse.com.tw`、`openapi.twse.com.tw`、
> `www.twse.com.tw`、`www.tpex.org.tw`(CONNECT 回 403),因此下列項目都沒有真實資料可依據。
> 以下「目前做法」是依 SPEC §7.1 的描述實作,**不是**實測結果;實測後請更新本檔與 SPEC §7.1。

## 待實測

| 編號 | 項目 | 目前做法(未驗證) | 實測時要確認 |
| --- | --- | --- | --- |
| Q-01 | MIS 行為 | 以 `ex_ch=tse_{Symbol}.tw\|otc_{Symbol}.tw` 單一端點查詢;欄位 `c z a b y tlong`;不處理 session cookie | `otc_` 是否同端點;`y`、`tlong` 欄位名與格式;是否需先取得 session cookie(HttpClient 已啟用預設 cookie 處理,但沒有先請求基本市況頁);單次可帶幾檔(`Quote:BatchSize` 預設 30);實際請求限制(`Quote:MaxRequestsPer5s` 預設 3) |
| Q-02 | 標的主檔來源 | **尚未實作**(T2.1) | 證交所、櫃買中心的公開資料來源,以及能分辨 ETF 與股票的欄位 |
| Q-03 | 休市日來源 | **尚未實作同步**(T4.4)。`MarketHolidays` 表、讀取與市場狀態判斷已完成 | 休市日公告端點與格式 |
| Q-04 | 除權息日 `y` | 今日損益沿用 `(P - Y) × S` | 除權息當天 `y` 是否為調整後的值 |
| Q-05 | 現價顯示位數 | 交易所原值、最多 2 位小數(§6.3) | 使用者是否要整數 |
| Q-06 | 最低手續費與證交稅特例 | `Fees:*` 預設值 | 使用者確認 |

## 樣本說明

`tests/StockInventory.Quotes.Tests/Samples/*.json` 是**依 SPEC §7.1 欄位描述手寫的合成樣本**,不是真實回應。
解析器測試只證明解析邏輯符合 SPEC,不證明與真實 MIS 回應相容。取得真實回應後,請替換或補充這些樣本。

## 規格內部矛盾(依 §0.10 回報,尚未自行選邊)

**§7.3 閒置間隔 與 §7.4 `market.stale`:**
- §7.3:無連線時抓取間隔為 `Quote:IdleIntervalSeconds`(300 秒)。
- §7.4:`market.stale = Open && (now - lastSuccessAtUtc) > StaleSeconds`(180 秒)。
- §7.6:`stale` 為真時寄「報價中斷」Email。

盤中沒有人開啟頁面時,最後成功時間每 300 秒才更新一次,所以約有 120 秒(300 − 180)的時間 `stale` 會是真,
`/health` 會回 503、並寄出「報價中斷」信,即使抓取完全正常。目前依 SPEC 字面實作,**尚未修正**。
可能的修正(請你決定):(a) 無連線時 stale 門檻改為 `IdleIntervalSeconds + StaleSeconds`;
(b) `/health` 與 Email 只在「有連線或最近一次抓取失敗」時才判斷;(c) 把 `IdleIntervalSeconds` 調到 < 180。

## 其他假設(SPEC 未寫明)

- `/api/csrf`:前端取得 `X-CSRF-TOKEN` 用。
- `GET /api/portfolios/{id}/holdings`:`/Portfolios` 頁列出持股用,不含價格。
- CSV「報價狀態」欄輸出 `live`、`delayed`、`reference`、`prevclose`、`missing` 代碼。
- 前端用純 JavaScript,沒有使用 Vue 3。
- `Quote:Enabled=false` 使用假報價(TV-01 的價格);`Dev:SeedSampleInstruments`、`Dev:UseInMemoryDatabase` 僅供開發,正式環境不要開啟。
- SignalR 前端程式庫 `@microsoft/signalr` 8.x 由 npm 下載後放在 `wwwroot/lib/signalr/`,由本站提供(MIT)。
