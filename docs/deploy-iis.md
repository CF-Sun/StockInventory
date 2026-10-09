# IIS 部署清單(對應 SPEC §13)

這份清單只整理步驟,**尚未在實際主機上演練過**。

## 一次性設定

1. 安裝與 .NET 8 對應的 **Hosting Bundle**,並啟用 IIS 功能「WebSocket 通訊協定」。
2. 建立資料庫 `StockInventory`(SQL Server 2022)。建立兩個登入:
   - 網站用:只有該資料庫的資料讀寫權限(`db_datareader`、`db_datawriter`),**沒有**結構變更權限(SEC-16)。
   - 遷移用:有結構變更權限,只在發佈時由人工使用。網站啟動時**不會**自動執行遷移。
3. 建立 IIS 站台 `StockInventory`,繫結 `stockInventory.newsafety.hk` 的 443 埠,使用 Cloudflare Origin 憑證。
4. 應用程式集區:獨立集區、.NET CLR 版本「無受控程式碼」、獨立執行身分、啟動模式 `AlwaysRunning`、閒置逾時 0;
   站台 `preloadEnabled = true`;關閉循環回收,改為每日固定 03:00 回收(背景抓價服務必須常駐)。
5. 建立站台資料夾以外的兩個資料夾,並讓集區身分可寫:日誌資料夾、Data Protection 金鑰資料夾(納入備份)。站台資料夾對集區身分唯讀。
6. Windows 防火牆 443 只放行 Cloudflare 公布的 IP 範圍。Cloudflare:SSL/TLS Full (strict)、Always Use HTTPS、WebSockets 開啟;建議對 `/Account/*` 設速率限制。

## 環境變數(集區或站台層級設定;`__` 取代 `:`)

| 變數 | 說明 |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT=Production` | |
| `ConnectionStrings__Default` | 網站專用 SQL 登入的連線字串 |
| `Seed__AdminUserName`、`Seed__AdminEmail`、`Seed__AdminInitialPassword` | 資料庫沒有任何使用者時建立第一位 Admin;建立後可移除 |
| `Smtp__Host`、`Smtp__Port`、`Smtp__UserName`、`Smtp__From`、`Smtp__Password`、`Alert__To__0`、`Alert__To__1`… | 通知信;未設定時只記日誌、不寄信 |
| `Logging__Directory` | 站台以外的日誌資料夾 |
| `DataProtection__KeyDirectory` | 站台以外的金鑰資料夾。**必設**,否則重新發佈後登入 Cookie 與「記住裝置」會失效 |
| `Vision__ApiKey` | FR-25 截圖辨識的 Anthropic API 金鑰(只放環境變數;請用有支出上限、可輪替的專用金鑰)。**未設定時此功能停用**(API-13 回 503),網站其餘功能不受影響。其他 `Vision__*`(`Endpoint`、`Model`、`TimeoutSeconds`、`MaxImageBytes`、`MaxCallsPerUserPerHour`、`MaxCallsPerDay`)非機敏,預設值見 `appsettings.json` |
| `Cloudflare__ProxyCidrs__0`、`Cloudflare__ProxyCidrs__1`… | Cloudflare 的 IP 範圍(逐項設定,格式如 `203.0.113.0/24`)。**必設**,否則不信任轉送標頭,來源 IP 會是 Cloudflare 的 IP。範圍請從 https://www.cloudflare.com/ips-v4 與 https://www.cloudflare.com/ips-v6 取得並定期更新 |

FR-26(當日即時線圖,SPEC v1.5)的設定鍵**都不是機敏資訊**,預設值已在 `appsettings.json`,通常不必設定;需要調整時才用環境變數覆寫:

| 變數 | 預設 | 說明 |
| --- | --- | --- |
| `Quote__UnwatchedIntervalSeconds` | 30 | 盤中**沒人看**時的抓價間隔(秒),允許 5 至 300,超出範圍啟動時記錄設定錯誤並改用 30。MIS 若出現限流或封鎖跡象就調大(上限 300) |
| `Intraday__Enabled` | true | 總開關。`false`:不記錄、`/api/intraday` 回 503 `FEATURE_DISABLED`、前端不顯示走勢欄(緊急關閉用,設定後回收應用程式集區) |
| `Intraday__RetentionDays` | 7 | 保留天數 |
| `Intraday__MaxPoints`、`Intraday__GapBreakSeconds`、`Intraday__FlushSeconds`、`Intraday__MaxRequestsPerMinute`、`Intraday__RecordStartTime`、`Intraday__RecordEndTime` | 90、180、60、30、09:00、13:30 | 見 SPEC §4 |

注意:v1.5 起盤中沒人開頁面也會每 30 秒抓一次價(原本 300 秒),MIS 請求量約為原本的 10 倍(60 檔約每日 1,460 次);`Quote:IdleIntervalSeconds` 只剩非開盤時段補抓缺價標的的節流。

**不要**在正式環境設定 `Dev__*` 與 `Vision__UseFake`(Production 環境會忽略 `Vision__UseFake`)。IIS 請求大小上限:截圖上傳端點最大約 5 MB,請確認站台 `requestFiltering/requestLimits/@maxAllowedContentLength` 不低於 5,308,416 位元組(預設 30,000,000 已足夠);出站 HTTPS 需可連 `api.anthropic.com`。`Quote__Enabled` 預設為 true(使用真實報價服務)。

## 發佈流程(不暫停網站)

1. 有結構變更時先備份 `StockInventory`。
2. `dotnet publish src/StockInventory.Web -c Release -o publish`
3. 有結構變更時產生遷移腳本,並以具結構變更權限的帳號執行:
   `dotnet ef migrations script --idempotent --project src/StockInventory.Data -o migrate.sql`
4. 把 `publish` 內容複製到站台目錄(DLL 被鎖定而失敗時,先回收應用程式集區再複製)。
5. 驗證:`/health` 回 200、能登入、主畫面數字與 SPEC §6.4 的 TV-01 一致。

## FR-26 當日即時線圖:資料庫結構變更(須人工執行)

新增資料表 `dbo.QuoteIntraday`(EF Migration `AddQuoteIntraday`)。**網站使用的 SQL 登入沒有 DDL 權限,網站啟動時不會自動遷移**,所以必須由具結構變更權限的帳號人工執行。

部署順序(程式不能比資料表先上線,雖然先上線也只會讓走勢功能失敗、不影響其他功能):

1. **備份** `StockInventory`(結構變更前一律先備份)。
2. 以具結構變更權限的帳號,在 `StockInventory` 資料庫執行下列任一腳本(兩者效果相同,都是冪等的,重複執行不會出錯):
   - 已附在 repo:`docs/sql/AddQuoteIntraday.sql`(只含 `AddQuoteIntraday`,前提是資料庫已套用過 `InitialCreate`)。
   - 或自行產生完整腳本:`dotnet ef migrations script --idempotent --project src/StockInventory.Data -o migrate.sql`。
3. **驗證**資料表存在:`SELECT OBJECT_ID(N'dbo.QuoteIntraday');`(不是 NULL),且 `__EFMigrationsHistory` 有 `AddQuoteIntraday`。
4. **權限**:網站 SQL 登入需對 `dbo.QuoteIntraday` 有 `SELECT, INSERT, UPDATE, DELETE`。若它是 `db_datareader` 加 `db_datawriter` 成員,自動涵蓋;否則由有權限帳號執行 `GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.QuoteIntraday TO <網站登入>;`(登入名稱不寫進文件或程式)。
5. 發佈新版程式(沿用上方「發佈流程」第 2、4 步)。
6. 驗證:`/health` 回 200;日誌出現「走勢紀錄器啟動」且沒有「走勢資料表無法讀取」;驗收清單見 `docs/verify/fr26-acceptance.md`。

回滾:
- **程式可直接回滾**到舊版,新表不影響舊程式,**不需刪表**(舊版程式不會讀寫它)。
- 緊急關閉走勢功能但保留新版程式:設定 `Intraday__Enabled=false` 並回收集區。若是 MIS 被限流,先調大 `Quote__UnwatchedIntervalSeconds`(上限 300)。
- 備份還原:新表在同一資料庫內,隨 `StockInventory` 備份;還原後最多缺備份時間點之後的走勢資料,可接受。

