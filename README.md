# 股票即時庫存管理平台

規格見 [SPEC.md](SPEC.md);開發狀態與待實測項目見 [docs/verify-notes.md](docs/verify-notes.md);IIS 部署見 [docs/deploy-iis.md](docs/deploy-iis.md)。

## 本機試跑(不需要 SQL Server)

需要 .NET 8 SDK。以下設定只供開發,資料只存在記憶體,重啟即消失,**正式環境不要使用**。

PowerShell:

```powershell
$env:Dev__UseInMemoryDatabase = "true"        # 不連 SQL Server
$env:Dev__SeedSampleInstruments = "true"      # 補上 0050、2330 兩檔標的
$env:Quote__Enabled = "false"                 # 使用假報價(0050=150.50/昨收149、2330=600/昨收610)
$env:Seed__AdminUserName = "admin"
$env:Seed__AdminEmail = "admin@example.com"
$env:Seed__AdminInitialPassword = "請自訂至少12字元的密碼"
$env:Logging__Directory = "$PWD\logs"
$env:ASPNETCORE_URLS = "https://localhost:5443"
dotnet dev-certs https --trust
dotnet run --project src/StockInventory.Web --no-launch-profile
```

開啟 https://localhost:5443,以種子管理員登入;首次登入須先改密碼,再用驗證器 App 設定雙重驗證。

## 測試

```powershell
dotnet test
```

## 結構

`src/StockInventory.Core`(計算與規則)、`Data`(EF Core)、`Quotes`(報價服務與通知)、`Web`(頁面、API、SignalR)。
