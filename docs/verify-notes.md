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
| 納入 | 股票(`ESVUFR`)、ETF(`CE…`)、**特別股(`EP…`,視為股票)**:特別股與一般股證交稅率相同,MIS 報價方式相同,代號如 `1101B`(2026-10-06 決定納入) |
| 不納入 | ETN(`CMXXXU`)、DR(`EDSDDR`)、受益證券(`CBCIXU` 不動產投資信託、`DAFUFR` 資產基礎證券)、權證(`RW…`,上市約 3.5 萬筆):稅率或交易方式與 SPEC 的股票/ETF 假設不同。若你持有 REIT 等,告訴我稅率後可加入 |
| 驗證 | 0050 = `CEOGEU`、0056 = `CEOGEU`、00878 = `CEOJEU`、2330 = `ESVUFR`、6669 = `ESVUFR`(皆在上市清單);6488 = `ESVUFR`(在上櫃清單) |
| 實作 | `IsinParser`、`ReferenceDataSync` |
| 防呆 | 任一來源失敗,或某市場取得筆數不到現有有效筆數的一半時,整次同步中止、不更動資料 |

### Q-03 休市日

| 項目 | 實測結果 |
| --- | --- |
| 來源 | `https://www.twse.com.tw/rwd/zh/holidaySchedule/holidaySchedule?date=2026&response=json`(回 `{"stat":"ok",...,"data":[["2026-01-01","名稱","說明"],...]}`)。另有 OpenAPI `https://openapi.twse.com.tw/v1/holidaySchedule/holidaySchedule`,日期是民國年(`1150101`) |
| 內容 | 2026 年 27 筆。**包含週六日**(如 2/15、2/28、4/4、4/5、10/10、10/25),也包含**有交易**的公告(「國曆新年開始交易日」1/2、「農曆春節前最後交易日」2/11、「農曆春節後開始交易日」2/23) |
| 實作 | 排除週末與名稱含「開始交易」「最後交易」者,2026 年存入 18 天(有測試,用真實資料)。例如 2026-10-09(國慶日補假)會是休市 |
| 同步時機 | 啟動時缺當年資料就同步;每日 07:00 一併重新同步當年(SPEC 只寫每年 1 月 1 日與啟動時,這是補充假設,可涵蓋臨時公告) |

### 盤後行為(2026-10-06 13:46,收盤後)

| 項目 | 實測結果 |
| --- | --- |
| `z` | 收盤後 `z` 與 `trade.z` 都是**收盤價**(0050 = 116.50、2330 = 2585、6488 = 1205),`t`/`tlong` 停在 13:30:00 |
| 影響 | 盤後 `PriceSource` 仍為成交價(`trade`),報價狀態為即時(不會顯示「參考價」)。「只有昨收」只會出現在開盤前或當天完全沒有成交時(**未實測**) |
| 批次大小、速率 | 盤後 20/30/40/60 檔耗時 125 到 374 毫秒,連續 10 次 43 到 111 毫秒。盤中同樣測試有 0.4 到 3.9 秒的變異,所以盤中的耗時才是參考 |

### Q-04 除權息日昨收(2026-10-06,股票 2614 東森當日除息)

| 項目 | 實測結果 |
| --- | --- |
| MIS `y` | **17.30** |
| 前一交易日(10/05)實際收盤 | **19.10**(STOCK_DAY) |
| 結論 | **`y` 是除權息調整後的參考價**,不是前一日收盤價。證交所自己的收盤資料當日標註 `X`(除息),漲跌價差顯示 0.00 |
| 影響 | 漲跌、漲跌幅、今日損益都以參考價為基準(2614:15.95 − 17.30 = −1.35),**與交易所顯示一致**,公式不改。除息當天不會把「配息造成的價格下跌」算成今日虧損。持股成本是人工維護,配息後的成本需使用者自行調整(SPEC §16.2 已有「成本須人工維護」) |

## 尚未實測

| 編號 | 項目 | 說明 |
| --- | --- | --- |
| Q-05 | 現價顯示位數 | 目前為交易所原值、最多 2 位小數 |
| Q-06 | 最低手續費、證交稅特例 | 目前 `Fees:*` 預設值;債券型 ETF 證交稅特例未處理 |
| Q-07 | 視覺 API 模型選型與準確度(FR-25) | **真實 API 呼叫從未實測**(開發沙盒沒有金鑰、連不到 `api.anthropic.com`)。需實測:(1) 請求格式被接受,特別是 `output_config.format`(`json_schema`)與 `output_config.effort`;(2) `claude-sonnet-5-5` 對 10 至 20 張去識別化券商截圖的代號、股數、總成本準確度(建議代號 ≥ 98%、股數與成本逐欄 ≥ 95%);(3) 回應 `content` 的區塊型態與 `stop_reason`;(4) 單次耗時是否在 `Vision:TimeoutSeconds`(30 秒)內;(5) 用量與費用 |
| — | FR-25 部署參數 | IIS `maxAllowedContentLength` 須 ≥ `Vision:MaxImageBytes` + 64 KB(預設 5,242,880 + 65,536 = 5,308,416 位元組以上;站台預設 30,000,000 已足夠,若曾調低請確認);出站 HTTPS 須可連 `Vision:Endpoint`;全站每日額度以台北時間日界計算(UTC+8),重啟歸零 |
| — | 開盤前(09:00 前)與當天完全沒有成交的標的 | 此時 `z`、`trade` 是什麼未實測;解析器依 `z` → `trade.z` → 買賣價中間價 → 昨收 的順序,應能處理。想補測可在 08:30 到 09:00 之間跑 `-Sections Q01` |

## 樣本說明

`tests/StockInventory.Quotes.Tests/Samples/` 內:
- **真實樣本**(2026-10-06 實測):`real_tse_etf_z_dash.json`(0050 盤中,`z` 為 `-` 且有 `trade`)、`real_tse_etf_after_close.json`(0050 收盤後)、`real_unknown_symbol.json`(查無代號)、`real_holiday_2026.json`(休市日,依實測回應的 27 筆資料重建)。
- **合成樣本**(依規格描述手寫,不是真實回應):`normal_tse.json`、`no_trade_with_quotes.json`、`only_prevclose.json`、`no_price.json`、`empty.json`、`otc.json`、`html_error.html`。
- `real_isin_listed_head.html`:真實上市 ISIN 頁面開頭(使用者貼出,15 檔股票;以該片段的結構重建),C# 解析器已對它測試。完整 9 MB 頁面以 PowerShell 版跑過(統計結果見上),C# 解析器沒有對完整頁面跑過。其餘 ISIN 測試用的 HTML 是依同樣結構手寫的。

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
