# [VERIFY] 項目實測紀錄

實測日期:2026-10-06(週二,盤中 09:26 到 09:30 台北時間),由使用者在能上網的 Windows 電腦以 `docs/verify/collect-samples.ps1` 執行。
開發沙盒連不到這些網站,所以實測由使用者執行,結果貼回來。

## 已實測

### Q-01 MIS(`getStockInfo.jsp`)

| 項目 | 實測結果 | 對實作的影響 |
| --- | --- | --- |
| session cookie | **不需要**。`/stock/index.jsp` 回 404;有無 cookie 的請求結果相同(伺服器自己會回 `JSESSIONID`) | 維持現狀,不先請求基本市況頁 |
| `otc_` 前綴 | 同一端點可查上櫃(`otc_6488.tw` 正常回傳) | 無 |
| 欄位 | `c`(代號)、`z`、`a`、`b`(五檔,`_` 分隔,結尾有 `_`)、`y`(昨收,字串)、`tlong`(毫秒時間戳,字串)、另有 `t`(時間)、`d`(日期)、`n`(名稱)、`ex`(tse/otc) | 與 SPEC §7.1 一致 |
| **`z` 常為 `-`** | 盤中 0050、2330 的 `z` 都是 `-`(只有該次更新剛好有成交的標的如 00878 才有值);回應內有 `trade` 物件,其 `z` 是最近成交價 | **新增 `trade.z` 作為第二順位(仍視為成交價)**,否則多數標的會被誤判為「參考價」。已更新 SPEC §7.1、§7.2 與解析器 |
| 回應格式 | Content-Type 是 `text/html`,內容是 JSON,JSON 前有約 17 行空白 | 解析器可容忍前導空白(有測試) |
| 查無代號 | **不是空陣列**:回一個 `c` 為空的元素(`{"tv":"-","s":"-","c":"","z":"-"}`) | 解析器略過 `c` 為空的元素(有測試) |
| 批次大小 | 20、30、40、60 檔皆成功(`msgArray` 筆數相符)。80 檔未測(測試用清單只有 77 檔)。耗時:20 檔 0.4 秒、30 檔 2.2 秒、40 檔 2.6 秒、60 檔 3.2 秒 | 維持 `Quote:BatchSize = 30`。注意:單次請求耗時變異很大(29 毫秒到 3.9 秒),`Quote:TimeoutSeconds = 5` 偶爾可能逾時,但只有「全部批次都失敗」才會進入退避 |
| 請求限制 | 間隔 300 毫秒連續 10 次請求,全部成功,沒有被擋 | 維持 `Quote:MaxRequestsPer5s = 3`(保守值) |

### Q-02 標的主檔

| 項目 | 實測結果 |
| --- | --- |
| 來源 | `https://isin.twse.com.tw/isin/C_public.jsp?strMode=2`(上市,8.8 MB)與 `strMode=4`(上櫃,3.0 MB),Big5 編碼,整頁單行 HTML |
| 欄位 | 代號及名稱(全形空白分隔)\| ISIN \| 上市日 \| 市場別 \| 產業別 \| CFICode \| 備註;以單一儲存格的列標示區段 |
| 分辨股票與 ETF | **以 CFICode**:`ESVUFR` = 股票(上市 1053 筆、上櫃 893 筆,另有創新板 31 筆也是 `ESVUFR`);ETF 的 CFICode 有很多種(`CEOGEU`、`CEOIEU`、`CEOJEU`、`CEOGDU`、`CEOIBU`、`CEOJBU`、`CEOJLU`、`CEOGMU`、`CEOGBU`、`CEOGCU`…),**全部以 `CE` 開頭** |
| 不納入 | 特別股(`EPN…`)、ETN(`CMXXXU`)、DR(`EDSDDR`)、受益證券(`CBCIXU`、`DAFUFR`)、權證(`RW…`,上市約 3.5 萬筆) |
| 驗證 | 0050 = `CEOGEU`、0056 = `CEOGEU`、00878 = `CEOJEU`、2330 = `ESVUFR`、6669 = `ESVUFR`(皆在上市清單);6488 = `ESVUFR`(在上櫃清單) |
| 實作 | `IsinParser`、`ReferenceDataSync`。特別股與受益證券要不要納入,請你決定(目前不納入) |
| 防呆 | 任一來源失敗,或某市場取得筆數不到現有有效筆數的一半時,整次同步中止、不更動資料 |

### Q-03 休市日

| 項目 | 實測結果 |
| --- | --- |
| 來源 | `https://www.twse.com.tw/rwd/zh/holidaySchedule/holidaySchedule?date=2026&response=json`(回 `{"stat":"ok",...,"data":[["2026-01-01","名稱","說明"],...]}`)。另有 OpenAPI `https://openapi.twse.com.tw/v1/holidaySchedule/holidaySchedule`,日期是民國年(`1150101`) |
| 內容 | 2026 年 27 筆。**包含週六日**(如 2/15、2/28、4/4、4/5、10/10、10/25),也包含**有交易**的公告(「國曆新年開始交易日」1/2、「農曆春節前最後交易日」2/11、「農曆春節後開始交易日」2/23) |
| 實作 | 排除週末與名稱含「開始交易」「最後交易」者,2026 年存入 18 天(有測試,用真實資料)。例如 2026-10-09(國慶日補假)會是休市 |
| 同步時機 | 啟動時缺當年資料就同步;每日 07:00 一併重新同步當年(SPEC 只寫每年 1 月 1 日與啟動時,這是補充假設,可涵蓋臨時公告) |

## 尚未實測

| 編號 | 項目 | 說明 |
| --- | --- | --- |
| Q-04 | 除權息日 `y` 是否為調整後的值 | 只能在除權息當天測:`.\collect-samples.ps1 -Sections Q04 -ExDivSymbol <代號>`。證交所的除權除息預告表可用(`TWT48U`)。目前今日損益沿用 `(P - Y) × S` |
| Q-05 | 現價顯示位數 | 目前為交易所原值、最多 2 位小數 |
| Q-06 | 最低手續費、證交稅特例 | 目前 `Fees:*` 預設值;債券型 ETF 證交稅特例未處理 |
| — | 盤後(收盤後)的 MIS 行為 | 沒有成交價時 `z` 與 `trade` 會是什麼。建議盤後再跑一次 `-Sections Q01`;目前解析器依 `z` → `trade.z` → 買賣價中間價 → 昨收 的順序,應能處理 |

## 樣本說明

`tests/StockInventory.Quotes.Tests/Samples/` 內:
- **真實樣本**(2026-10-06 實測):`real_tse_etf_z_dash.json`(0050 盤中,`z` 為 `-` 且有 `trade`)、`real_unknown_symbol.json`(查無代號)、`real_holiday_2026.json`(休市日,依實測回應的 27 筆資料重建)。
- **合成樣本**(依規格描述手寫,不是真實回應):`normal_tse.json`、`no_trade_with_quotes.json`、`only_prevclose.json`、`no_price.json`、`empty.json`、`otc.json`、`html_error.html`。
- ISIN 清單的測試 HTML 是依實測欄位結構手寫的最小頁面;完整的頁面解析邏輯已用真實頁面以 PowerShell 版跑過(統計結果見上),但 C# 解析器沒有對真實頁面跑過。

## 規格內部矛盾(已解決)

**§7.3 閒置間隔 與 §7.4 `market.stale`**:無連線時抓取間隔為 300 秒,但 `stale` 門檻為 180 秒,
會在盤中沒人開頁面時誤報「報價中斷」(`/health` 回 503、寄 Email)。

**已採用方案 (a)**:無連線時 `stale` 門檻為 `IdleIntervalSeconds + StaleSeconds`(預設 480 秒),
有連線時維持 `StaleSeconds`(180 秒)。已實作於 `QuoteFetcher.Snapshot`,測試:
`Stale_WhenNoConnections_UsesIdlePlusStaleThreshold`、`IdleNormalOperation_NeverFlagsStale`。
**SPEC.md §7.4 需同步修改**(SPEC.md 不在本 repo,請你更新原檔):

> `market.stale = (state == Open) && (now - lastSuccessAtUtc) > 門檻`,
> 其中門檻 = 有連線時為 `Quote:StaleSeconds`;無連線時為 `Quote:IdleIntervalSeconds + Quote:StaleSeconds`。

注意:單檔報價的 `quoteStatus = delayed`(§7.4 的另一條規則,以 `FetchedAtUtc` 與 `StaleSeconds` 比較)沒有改動。
閒置一段時間後有人開頁面,最初幾秒畫面可能顯示「延遲」,抓取(5 秒內)完成後會恢復。

## 其他假設(SPEC 未寫明)

- `/api/csrf`:前端取得 `X-CSRF-TOKEN` 用。
- `GET /api/portfolios/{id}/holdings`:`/Portfolios` 頁列出持股用,不含價格。
- CSV「報價狀態」欄輸出 `live`、`delayed`、`reference`、`prevclose`、`missing` 代碼。
- 前端用純 JavaScript,沒有使用 Vue 3。
- `Quote:Enabled=false` 使用假報價(TV-01 的價格);`Dev:SeedSampleInstruments`、`Dev:UseInMemoryDatabase` 僅供開發,正式環境不要開啟。
- SignalR 前端程式庫 `@microsoft/signalr` 8.x 由 npm 下載後放在 `wwwroot/lib/signalr/`,由本站提供(MIT)。
