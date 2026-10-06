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
| `Cloudflare__ProxyCidrs__0`、`Cloudflare__ProxyCidrs__1`… | Cloudflare 的 IP 範圍(逐項設定,格式如 `203.0.113.0/24`)。**必設**,否則不信任轉送標頭,來源 IP 會是 Cloudflare 的 IP。範圍請從 https://www.cloudflare.com/ips-v4 與 https://www.cloudflare.com/ips-v6 取得並定期更新 |

**不要**在正式環境設定 `Dev__*`。`Quote__Enabled` 預設為 true(使用真實報價服務)。

## 發佈流程(不暫停網站)

1. 有結構變更時先備份 `StockInventory`。
2. `dotnet publish src/StockInventory.Web -c Release -o publish`
3. 有結構變更時產生遷移腳本,並以具結構變更權限的帳號執行:
   `dotnet ef migrations script --idempotent --project src/StockInventory.Data -o migrate.sql`
4. 把 `publish` 內容複製到站台目錄(DLL 被鎖定而失敗時,先回收應用程式集區再複製)。
5. 驗證:`/health` 回 200、能登入、主畫面數字與 SPEC §6.4 的 TV-01 一致。
