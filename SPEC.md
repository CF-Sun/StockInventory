# 股票即時庫存管理平台 — 開發規格(AI 實作版)

- 版本:v1.4,日期 2026-10-07(v1.1:§7.4 `market.stale` 門檻修正;v1.2:依實測更新 §7.1、§7.2、§7.7、§16.1、§16.2;v1.3:§7.3 非開盤時段補抓缺價標的;v1.4:新增 FR-25 截圖辨識新增持股、API-13、API-14、SEC-18 至 SEC-24、階段 P2b,細節見 `docs/sa/FR-25-截圖辨識新增持股.md`(§17))
- 對應的人類閱讀版:線上文件「股票即時庫存管理平台 開發規格書」(內容一致,本檔為可直接實作的精簡、結構化版本)
- 放置方式:放在專案根目錄;若使用 Claude Code,在 `CLAUDE.md` 加一行「規格見 SPEC.md,依 §14 階段逐步實作」;若使用其他工具(例如 `AGENTS.md`)同理。

---

## 0. 給實作者(AI)的規則

1. 本檔是唯一規格來源。其他說明與本檔衝突時,以本檔為準。
2. 依 §14 的階段 P0 → P5 逐階段實作。每階段完成後執行該階段列出的測試(閘門),全部通過才進下一階段。
3. 標示 `[VERIFY]` 的項目依賴外部系統的實際行為,必須先實測,不得憑記憶或猜測實作。實測結果寫進 `docs/verify-notes.md`,並把樣本回應存到 `tests/StockInventory.Quotes.Tests/Samples/`。
4. 不做規格外的功能(見 §2)。遇到規格沒寫的細節,選最簡單、且與本檔一致的做法,並在提交說明列出你的假設。
5. 所有金額與價格一律用 `decimal`(T-SQL 用 `decimal`/`bigint`),禁止 `double`、`float`。**捨入在伺服器端完成**,API 與 CSV 回傳的就是顯示值,前端不做任何運算與捨入。
6. 機敏資訊(連線字串、SMTP 密碼、初始管理員密碼)不得出現在原始碼、`appsettings.json`、日誌、前端;只從環境變數讀取。
7. 日誌不得記錄密碼、TOTP、Cookie,也不得記錄持股的成本與股數。
8. 目前使用者的 `UserId` 只能取自登入身分(Cookie),禁止信任任何請求參數、標頭或 body 內的使用者 Id。
9. 介面文字與使用者可見訊息一律使用繁體中文;程式碼識別字、資料庫物件、API 欄位一律英文。
10. 發現本檔內部矛盾時,停止該項實作並回報,不要自行選邊。

---

## 1. 專案事實

| 項目 | 內容 |
| --- | --- |
| 名稱 | 股票即時庫存管理平台 |
| 網址 | `https://stockInventory.newsafety.hk`(Cloudflare → IIS) |
| 後端 | ASP.NET Core(.NET LTS),MVC + REST API + SignalR;IIS in-process 託管 |
| 前端 | Razor 頁面 + Vue 3 小元件;所有腳本與樣式由本站提供,不引用外部 CDN;手機優先 RWD;繁體中文 |
| 資料庫 | SQL Server 2022(與其他系統共用同一個執行個體,資料庫各自獨立),資料庫名 `StockInventory`,EF Core + Migrations,網站使用專用 SQL 登入 |
| 認證 | ASP.NET Core Identity(`Guid` 主鍵)、強制 TOTP、關閉公開註冊 |
| 報價 | 證交所基本市況報導網站 MIS(`https://mis.twse.com.tw/stock/api/getStockInfo.jsp`),背景服務統一抓取,記憶體快取加 `Quotes` 資料表 |
| 時間 | 資料庫與 API 一律 UTC(`datetime2`、ISO 8601 加 `Z`);交易時段判斷用 Windows 時區識別碼 `Taipei Standard Time` |
| 日誌 | Serilog 檔案日誌,每日分檔,保留 30 天,放站台資料夾以外 |
| 測試 | 建議 xUnit;不做自動化瀏覽器測試 |

---

## 2. 功能需求(FR)與非目標

### 2.1 功能需求

| 編號 | 需求 | 規則 |
| --- | --- | --- |
| FR-01 | 登入 | 帳號、密碼加 TOTP;失敗 5 次鎖定 15 分鐘;閒置 30 分鐘登出;可勾「信任此裝置 30 天」(只略過 TOTP) |
| FR-02 | 關閉公開註冊 | 帳號只能由管理員建立 |
| FR-03 | 管理員頁面 | 新增與停用使用者、重設密碼、重設雙重驗證 |
| FR-04 | 備用還原碼 | 啟用 TOTP 時一次性顯示;手機遺失時以還原碼登入 |
| FR-05 | 資料隔離 | 使用者只能讀寫自己的庫存與持股 |
| FR-10 | 庫存 CRUD | 每人最多 20 個;名稱同一使用者內不可重複;刪除庫存連同持股一併刪除,需二次確認 |
| FR-11 | 庫存排序 | 使用者自訂順序 |
| FR-20 | 新增持股 | 以代碼或名稱搜尋標的;總成本(整數,≥ 0)、股數(整數,≥ 1);畫面即時預覽均價 |
| FR-21 | 修改、刪除持股 | 修改只改總成本與股數;刪除為實際刪除,需二次確認 |
| FR-22 | 唯一性與上限 | 同庫存內同代號不可重複;每庫存最多 200 檔;跨庫存可重複 |
| FR-23 | 異動紀錄 | 新增、修改、刪除持股都寫入 `HoldingChanges`;v1 無畫面 |
| FR-24 | 標的主檔 | 每日同步上市、上櫃股票與 ETF;不在名單內的代號不可新增 |
| FR-30 | 庫存選擇列 | 固定有「全部」;其餘每個庫存為可勾選標籤;勾「全部」= 總庫存,勾一個 = 單一庫存,勾多個 = 多選合併 |
| FR-31 | 記住選擇 | 存在 `UserSettings.SelectedPortfolioIds`,換裝置也套用 |
| FR-32 | 網址參數 | 選擇寫入網址 `?p=1,3`,網址優先於已儲存的選擇 |
| FR-33 | 同代號合併 | 多庫存檢視時同代號合併為一列,可展開看各庫存明細 |
| FR-40 | 每檔欄位 | 現價、漲跌、漲跌幅、均價、股數、總成本、市值、未實現損益、報酬率、今日損益、報價狀態 |
| FR-41 | 合計列 | 市值、總成本、未實現損益、整體報酬率、今日損益;固定置頂 |
| FR-42 | 扣費預估 | 設定可切換為扣除預估手續費與證交稅;預設只算帳面損益 |
| FR-43 | 顏色慣例 | 預設紅漲綠跌,可切換為綠漲紅跌 |
| FR-44 | 報價異常提示 | 交易時段內超過 3 分鐘未更新,頂端顯示黃色提示;缺價檔數註明在合計列 |
| FR-45 | 即時更新 | SignalR 推播;頁面在背景或鎖屏時暫停,回到前景立即補一次 |
| FR-50 | CSV 匯出 | 匯出目前檢視;UTF-8 含 BOM;不提供匯入 |
| FR-25 | 截圖辨識新增持股 | 在 /Portfolios 上傳券商庫存截圖,只辨識股票代號、股數、總成本;辨識結果須經使用者核對後才寫入;圖片不保存;每次上傳前須勾選同意送第三方 AI 服務;同庫存已存在的標的預設略過。細節見 §17 |

### 2.2 非目標(不要做)

- 興櫃、海外標的、期貨選擇權、融資融券、權證。
- 交易流水、已實現損益、配息、歷史績效、K 線圖、五檔明細。
- 庫存封存功能、持股備註欄、CSV 匯入(截圖辨識為 FR-25,不是 CSV 匯入)、LINE 或 Telegram 通知、公開註冊、第三方登入。
- 自動化瀏覽器測試。
- 持股 `HoldingChanges` 的畫面。
- 截圖辨識:不保存圖片、不自動寫入、不做多張合併、不做名稱比對、不做由均價推估成本、不支援 HEIC 與 PDF。

---

## 3. 解決方案結構

```
src/
  StockInventory.Core/       純函式與型別:計算、捨入、取價、狀態判斷。禁止任何 I/O、EF、HTTP、時鐘存取(時間由參數傳入)
  StockInventory.Data/       DbContext、實體、Migrations、查詢
  StockInventory.Quotes/     MIS 用戶端、解析器、抓取背景服務、快取、主檔與休市日同步、通知
  StockInventory.Web/        MVC、REST API、SignalR Hub、Identity、wwwroot(Vue 元件與樣式)
tests/
  StockInventory.Core.Tests/     UT-xx
  StockInventory.Quotes.Tests/   解析測試;Samples/*.json 為真實回應樣本
  StockInventory.Web.Tests/      IT-xx,使用獨立的測試資料庫,每次重建
docs/
  verify-notes.md            [VERIFY] 項目的實測紀錄
```

依賴方向:`Web → Quotes → Data → Core`,`Web → Data`,`Core` 不依賴任何專案。

---

## 4. 設定鍵

放 `appsettings.json` 或環境變數(環境變數用 `__` 取代 `:`)。標示 **(env)** 的只能放環境變數。

| 設定鍵 | 預設值 | 說明 |
| --- | --- | --- |
| `ConnectionStrings:Default` **(env)** | — | 網站專用 SQL 登入 |
| `Seed:AdminUserName`、`Seed:AdminEmail`、`Seed:AdminInitialPassword` **(env)** | — | 資料庫沒有任何使用者時,建立第一位 Admin;`MustChangePassword = 1` |
| `Smtp:Host`、`Smtp:Port`、`Smtp:UserName`、`Smtp:From`、`Alert:To` | — | 通知寄信設定,`Alert:To` 為收件者清單 |
| `Smtp:Password` **(env)** | — | |
| `Limits:MaxPortfoliosPerUser` | 20 | |
| `Limits:MaxHoldingsPerPortfolio` | 200 | |
| `Quote:BatchSize` | 30 | 單次查詢代號數,`[VERIFY]` |
| `Quote:MaxRequestsPer5s` | 3 | 所有批次共用的滑動視窗上限,`[VERIFY]` |
| `Quote:ActiveIntervalSeconds` | 5 | 有連線時的抓取間隔 |
| `Quote:IdleIntervalSeconds` | 300 | 無連線時的抓取間隔 |
| `Quote:TimeoutSeconds` | 5 | 單次請求逾時 |
| `Quote:StaleSeconds` | 180 | 判斷延遲的門檻 |
| `Market:OpenTime`、`Market:CloseTime` | `08:30`、`14:35` | 抓取時段(含頭尾) |
| `Market:TimeZoneId` | `Taipei Standard Time` | |
| `Fees:MinFee` | 20 | 最低手續費(元) |
| `Fees:StockTaxRate`、`Fees:EtfTaxRate` | 0.003、0.001 | 證交稅率 |
| `Auth:IdleMinutes` | 30 | 登入閒置逾時(滑動過期) |
| `Auth:TrustDeviceDays` | 30 | 記住裝置天數 |
| `Auth:LockoutMaxFailures`、`Auth:LockoutMinutes` | 5、15 | |
| `Auth:MinPasswordLength` | 12 | |
| `Alert:CooldownMinutes` | 30 | 同一事件重複寄信的間隔 |
| `Alert:LoginFailThreshold`、`Alert:LoginFailWindowMinutes` | 10、10 | 同一帳號或來源 IP 在視窗內失敗達門檻時寄信 |
| `Logging:Directory` | — | 站台以外的日誌資料夾 |
| `DataProtection:KeyDirectory` | — | 站台以外的金鑰資料夾 |
| `Vision:ApiKey` **(env)** | — | 視覺辨識 API 金鑰(FR-25),只能放環境變數;未設定時功能停用 |
| `Vision:Endpoint` | `https://api.anthropic.com/v1/messages` | 視覺 API 端點 |
| `Vision:Model` | `claude-sonnet-5-5` | 辨識使用的模型,`[VERIFY: Q-07]` 實測準確度後定案 |
| `Vision:TimeoutSeconds` | 30 | 單次辨識呼叫逾時 |
| `Vision:MaxImageBytes` | 5242880 | 上傳圖片大小上限 |
| `Vision:MaxCallsPerUserPerHour` | 10 | 每位使用者每小時辨識次數 |
| `Vision:MaxCallsPerDay` | 200 | 全站每日辨識次數 |

---

## 5. 資料庫

資料表以 EF Core 實體加 Migrations 建立。下列 T-SQL 是權威定義,實體映射的名稱、型別、限制、索引必須與之完全一致。

### 5.1 列舉值

| 欄位 | 值 |
| --- | --- |
| `Instruments.Market` | 1 = 上市(TWSE)、2 = 上櫃(TPEx) |
| `Instruments.Kind` | 1 = 股票、2 = ETF |
| `Quotes.PriceSource` | 1 = 當盤成交價、2 = 最佳買賣價中間價、3 = 昨收價 |
| `UserSettings.ColorScheme` | 0 = 紅漲綠跌(預設)、1 = 綠漲紅跌 |
| `HoldingChanges.Action` | `A` 新增、`U` 修改、`D` 刪除 |

### 5.2 AppUser(繼承 `IdentityUser<Guid>`,資料表 `AspNetUsers`)

Identity 其餘資料表採 EF Core 預設結構。雙重驗證還原碼使用 Identity 內建的 UserTokens。角色只有 `Admin`、`User`。

| 額外屬性 | 型別 | 說明 |
| --- | --- | --- |
| `DisplayName` | `nvarchar(50)` NOT NULL | 預設空字串 |
| `IsActive` | `bit` NOT NULL | 預設 1;為 0 時一律拒絕登入 |
| `MustChangePassword` | `bit` NOT NULL | 預設 1;建立帳號與管理員重設密碼時設為 1 |
| `CreatedAtUtc` | `datetime2` NOT NULL | |

### 5.3 自訂資料表

```sql
CREATE TABLE dbo.Instruments (
  Symbol       varchar(10)  NOT NULL CONSTRAINT PK_Instruments PRIMARY KEY,
  Name         nvarchar(50) NOT NULL,
  Market       tinyint      NOT NULL CONSTRAINT CK_Instruments_Market CHECK (Market IN (1, 2)),
  Kind         tinyint      NOT NULL CONSTRAINT CK_Instruments_Kind CHECK (Kind IN (1, 2)),
  IsActive     bit          NOT NULL CONSTRAINT DF_Instruments_IsActive DEFAULT 1,
  UpdatedAtUtc datetime2    NOT NULL
);
CREATE INDEX IX_Instruments_Name ON dbo.Instruments (Name);

CREATE TABLE dbo.UserSettings (
  UserId               uniqueidentifier NOT NULL CONSTRAINT PK_UserSettings PRIMARY KEY,
  ColorScheme          tinyint        NOT NULL CONSTRAINT DF_UserSettings_Color DEFAULT 0
                                      CONSTRAINT CK_UserSettings_Color CHECK (ColorScheme IN (0, 1)),
  DeductFees           bit            NOT NULL CONSTRAINT DF_UserSettings_Deduct DEFAULT 0,
  FeeRate              decimal(7,6)   NOT NULL CONSTRAINT DF_UserSettings_FeeRate DEFAULT 0.001425
                                      CONSTRAINT CK_UserSettings_FeeRate CHECK (FeeRate >= 0 AND FeeRate <= 0.01),
  FeeDiscount          decimal(4,3)   NOT NULL CONSTRAINT DF_UserSettings_Discount DEFAULT 1.000
                                      CONSTRAINT CK_UserSettings_Discount CHECK (FeeDiscount >= 0.001 AND FeeDiscount <= 1),
  SelectedPortfolioIds nvarchar(400)  NULL,   -- NULL = 全部;否則為逗號分隔的 PortfolioId,例如 '1,3'
  UpdatedAtUtc         datetime2      NOT NULL,
  CONSTRAINT FK_UserSettings_Users FOREIGN KEY (UserId) REFERENCES dbo.AspNetUsers (Id) ON DELETE CASCADE
);

CREATE TABLE dbo.Portfolios (
  PortfolioId  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Portfolios PRIMARY KEY,
  UserId       uniqueidentifier  NOT NULL,
  Name         nvarchar(30)      NOT NULL,
  SortOrder    int               NOT NULL,
  CreatedAtUtc datetime2         NOT NULL,
  UpdatedAtUtc datetime2         NOT NULL,
  CONSTRAINT FK_Portfolios_Users FOREIGN KEY (UserId) REFERENCES dbo.AspNetUsers (Id)
);
CREATE UNIQUE INDEX UX_Portfolios_User_Name ON dbo.Portfolios (UserId, Name);
CREATE INDEX IX_Portfolios_User_Sort ON dbo.Portfolios (UserId, SortOrder);

CREATE TABLE dbo.Holdings (
  HoldingId    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Holdings PRIMARY KEY,
  PortfolioId  int               NOT NULL,
  Symbol       varchar(10)       NOT NULL,
  TotalCost    bigint            NOT NULL CONSTRAINT CK_Holdings_Cost CHECK (TotalCost >= 0),  -- 新台幣整數
  Shares       bigint            NOT NULL CONSTRAINT CK_Holdings_Shares CHECK (Shares >= 1),
  CreatedAtUtc datetime2         NOT NULL,
  UpdatedAtUtc datetime2         NOT NULL,
  CONSTRAINT FK_Holdings_Portfolios  FOREIGN KEY (PortfolioId) REFERENCES dbo.Portfolios (PortfolioId) ON DELETE CASCADE,
  CONSTRAINT FK_Holdings_Instruments FOREIGN KEY (Symbol)      REFERENCES dbo.Instruments (Symbol)
);
CREATE UNIQUE INDEX UX_Holdings_Portfolio_Symbol ON dbo.Holdings (PortfolioId, Symbol);
CREATE INDEX IX_Holdings_Symbol ON dbo.Holdings (Symbol);

CREATE TABLE dbo.HoldingChanges (   -- 不設外鍵:持股或庫存刪除後仍保留
  ChangeId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_HoldingChanges PRIMARY KEY,
  UserId        uniqueidentifier NOT NULL,
  PortfolioId   int              NOT NULL,
  PortfolioName nvarchar(30)     NOT NULL,   -- 當時的庫存名稱快照
  Symbol        varchar(10)      NOT NULL,
  Action        char(1)          NOT NULL CONSTRAINT CK_HoldingChanges_Action CHECK (Action IN ('A', 'U', 'D')),
  OldTotalCost  bigint NULL, OldShares bigint NULL,   -- Action = 'A' 時為 NULL
  NewTotalCost  bigint NULL, NewShares bigint NULL,   -- Action = 'D' 時為 NULL
  ChangedAtUtc  datetime2        NOT NULL
);
CREATE INDEX IX_HoldingChanges_User_Time ON dbo.HoldingChanges (UserId, ChangedAtUtc);

CREATE TABLE dbo.Quotes (
  Symbol       varchar(10)   NOT NULL CONSTRAINT PK_Quotes PRIMARY KEY,
  LastPrice    decimal(12,4) NULL,
  PrevClose    decimal(12,4) NULL,
  PriceSource  tinyint       NOT NULL CONSTRAINT CK_Quotes_Source CHECK (PriceSource IN (1, 2, 3)),
  TradeDate    date          NOT NULL,
  QuoteTimeUtc datetime2     NULL,
  FetchedAtUtc datetime2     NOT NULL,
  CONSTRAINT FK_Quotes_Instruments FOREIGN KEY (Symbol) REFERENCES dbo.Instruments (Symbol)
);

CREATE TABLE dbo.MarketHolidays (   -- 證交所公告的休市日;週六日不存入
  HolidayDate date         NOT NULL CONSTRAINT PK_MarketHolidays PRIMARY KEY,
  Description nvarchar(50) NULL
);
```

### 5.4 資料寫入規則

| 情境 | 規則 |
| --- | --- |
| 新增持股 | 同一交易內寫 `Holdings` 與一筆 `HoldingChanges`(`A`,Old 欄位 NULL) |
| 修改持股 | 同一交易內更新 `Holdings`(含 `UpdatedAtUtc`)並寫一筆 `U`(Old 與 New 都有值);值沒變時不寫、也不報錯 |
| 刪除持股 | 同一交易內寫一筆 `D`(New 欄位 NULL)再刪除 |
| 刪除庫存 | 同一交易內,庫存下每筆持股各寫一筆 `D`,再刪除庫存;並把該庫存 Id 從 `UserSettings.SelectedPortfolioIds` 移除,移除後若為空則設 NULL |
| 庫存新增 | `SortOrder` = 該使用者目前最大值加 1 |
| 標的主檔同步 | 以 `Symbol` upsert;不在最新名單者設 `IsActive = 0`,不刪除(已有持股的標的仍要能顯示) |

---

## 6. 領域規則與計算(`StockInventory.Core`)

### 6.1 型別

```csharp
public enum Market { Twse = 1, Tpex = 2 }
public enum InstrumentKind { Stock = 1, Etf = 2 }
public enum PriceSource { Trade = 1, MidQuote = 2, PrevClose = 3 }
public enum QuoteStatus { Live, Delayed, Reference, PrevClose, Missing }
public enum MarketState { Open, Closed, Holiday }

public sealed record HoldingInput(
    int HoldingId, int PortfolioId, string Symbol, string Name,
    Market Market, InstrumentKind Kind, long Shares, long TotalCost);

public sealed record QuoteInput(
    decimal? LastPrice, decimal? PrevClose, PriceSource PriceSource, DateTime FetchedAtUtc);

public sealed record CalcOptions(
    bool Merge, bool DeductFees, decimal FeeRate, decimal FeeDiscount, decimal MinFee,
    decimal StockTaxRate, decimal EtfTaxRate, int StaleSeconds,
    MarketState MarketState, DateTime NowUtc);

// 主要入口:純函式,輸出為已捨入的顯示值(見 §6.4)
ViewResult ViewCalculator.Build(
    IReadOnlyList<HoldingInput> holdings,
    IReadOnlyDictionary<string, QuoteInput> quotes,   // key = Symbol
    CalcOptions options);
```

### 6.2 演算法

符號:C = TotalCost、S = Shares、P = LastPrice、Y = PrevClose。以下全程 `decimal`,未捨入;捨入只發生在最後輸出(§6.4)。

**步驟 1:逐筆持股(Holdings 一列)計算 `line`**

```
hasPrice = quote != null && quote.LastPrice != null
if hasPrice:
  gross       = P * S
  marketValue = gross
  fee = tax = 0
  if DeductFees:
    fee = max(MinFee, floor(gross * FeeRate * FeeDiscount))      # floor = 無條件捨去到整數
    tax = floor(gross * (Kind == Etf ? EtfTaxRate : StockTaxRate))
  unrealized = marketValue - C - fee - tax
  todayPnl   = (Y != null) ? (P - Y) * S : null
```

**步驟 2:分組成列(row)**

- `Merge = false`:一筆持股一列,`sources` 為空陣列。
- `Merge = true`:依 `Symbol` 分組成一列;`sources` 在該組有 2 筆以上時填入各筆的 `portfolioId`、`holdingId`、`shares`、`totalCost`,只有 1 筆時為空陣列。
  - `shares = ΣS`、`totalCost = ΣC`、`marketValue = ΣmarketValue`、`unrealized = Σunrealized`、`estFee = Σfee`、`estTax = Σtax`。
  - `todayPnl`:加總非 null 的值;全部為 null 則為 null。
  - 費用已在步驟 1 逐筆計算,合併列只相加,**不得**對合併後的金額重算費用。

**步驟 3:每列的衍生欄位**

| 欄位 | 公式 | 缺值 |
| --- | --- | --- |
| `avgCost` | C ÷ S | S ≥ 1,不會除以 0 |
| `change` | P − Y | Y 為 null 時為 null |
| `changePct` | (P − Y) ÷ Y × 100 | Y 為 null 或 0 時為 null |
| `returnRatePct` | unrealized ÷ C × 100 | C = 0 時為 null |
| `quoteStatus` | 見 §7.4 | — |

沒有價格的列(`hasPrice = false`):`quoteStatus = missing`;`lastPrice`、`prevClose`、`change`、`changePct`、`marketValue`、`unrealizedPnl`、`returnRatePct`、`todayPnl`、`estFee`、`estTax` 全為 null。

**步驟 4:合計(summary)**——只加總有價格的列

```
marketValue       = Σ row.marketValue
totalCost         = Σ row.totalCost
unrealizedPnl     = Σ row.unrealized            # DeductFees 開啟時為扣費後的值
returnRatePct     = unrealizedPnl / totalCost * 100   # totalCost = 0 時為 null
todayPnl          = Σ 非 null 的 row.todayPnl    # 全部為 null 時為 0
missingQuoteCount = 沒有價格的列數(合併後的列數)
```

**步驟 5:排序**:列預設依 `marketValue` 由大到小(null 排最後),再依 `symbol` 升冪。

**扣費開啟時的欄位語意**:`unrealizedPnl` 與 `returnRatePct` 就是扣費後的值,並附 `estFee`、`estTax`;未開啟時 `estFee`、`estTax` 為 null。今日損益、漲跌不受扣費影響。

### 6.3 捨入(輸出時一次完成)

| 欄位 | 輸出 |
| --- | --- |
| `totalCost`、`marketValue`、`unrealizedPnl`、`todayPnl`、`estFee`、`estTax` | 整數(`long`) |
| `avgCost` | 小數 2 位 |
| `returnRatePct`、`changePct` | 已乘 100 的百分比數字,小數 2 位 |
| `lastPrice`、`prevClose`、`change` | 交易所原值,最多 2 位小數,不另外捨入 |

捨入一律用 `Math.Round(x, digits, MidpointRounding.AwayFromZero)`;負數同理(−0.5 → −1)。**合計用未捨入的列值加總後再捨入**,不是把已捨入的列值相加,所以合計可能和各列顯示值相加差 1 元。

### 6.4 測試向量(單元測試必須使用這些數字)

下列 JSON 的 `expect` 是捨入後的輸出。未列出的欄位不檢查。時間相關欄位視為「有價格且非延遲」。

```json
{
  "TV-01_two_holdings": {
    "options": { "merge": true, "deductFees": false },
    "holdings": [
      { "holdingId": 1, "portfolioId": 1, "symbol": "0050", "kind": "etf",   "shares": 2000, "totalCost": 280000 },
      { "holdingId": 2, "portfolioId": 1, "symbol": "2330", "kind": "stock", "shares": 1000, "totalCost": 520000 }
    ],
    "quotes": {
      "0050": { "lastPrice": 150.50, "prevClose": 149.00 },
      "2330": { "lastPrice": 600.00, "prevClose": 610.00 }
    },
    "expect": {
      "rows": {
        "0050": { "avgCost": 140.00, "marketValue": 301000, "unrealizedPnl": 21000, "returnRatePct": 7.50,
                  "change": 1.50, "changePct": 1.01, "todayPnl": 3000 },
        "2330": { "avgCost": 520.00, "marketValue": 600000, "unrealizedPnl": 80000, "returnRatePct": 15.38,
                  "change": -10.00, "changePct": -1.64, "todayPnl": -10000 }
      },
      "summary": { "marketValue": 901000, "totalCost": 800000, "unrealizedPnl": 101000,
                   "returnRatePct": 12.63, "todayPnl": -7000, "missingQuoteCount": 0 },
      "note": "合計報酬率 12.625 必須顯示 12.63(AwayFromZero);銀行家捨入會得到 12.62,測試必須擋住"
    }
  },

  "TV-02_merge_and_fees": {
    "holdings": [
      { "holdingId": 11, "portfolioId": 1, "symbol": "0050", "kind": "etf", "shares": 1000, "totalCost": 130000 },
      { "holdingId": 25, "portfolioId": 2, "symbol": "0050", "kind": "etf", "shares": 1000, "totalCost": 150000 }
    ],
    "quotes": { "0050": { "lastPrice": 150.50, "prevClose": 149.00 } },
    "cases": {
      "merge=true, deductFees=false": {
        "rows": [ { "symbol": "0050", "shares": 2000, "totalCost": 280000, "avgCost": 140.00,
                    "marketValue": 301000, "unrealizedPnl": 21000, "returnRatePct": 7.50, "estFee": null, "estTax": null } ]
      },
      "merge=true, deductFees=true (FeeRate 0.001425, FeeDiscount 1.000, MinFee 20, EtfTaxRate 0.001)": {
        "rows": [ { "symbol": "0050", "shares": 2000, "totalCost": 280000, "avgCost": 140.00,
                    "marketValue": 301000, "unrealizedPnl": 20272, "returnRatePct": 7.24, "estFee": 428, "estTax": 300 } ],
        "note": "各筆分別計算:fee = floor(214.4625) = 214、tax = floor(150.5) = 150,兩筆相加得 428 與 300;不是對 301000 重算(那會得到 tax = 301)"
      },
      "merge=false, deductFees=true": {
        "rows": [
          { "holdingId": 11, "unrealizedPnl": 20136, "returnRatePct": 15.49, "estFee": 214, "estTax": 150 },
          { "holdingId": 25, "unrealizedPnl": 136,   "returnRatePct": 0.09,  "estFee": 214, "estTax": 150 }
        ]
      }
    }
  },

  "TV-03_avg_rounding":   { "holding": { "shares": 3, "totalCost": 100000 }, "expect": { "avgCost": 33333.33 } },

  "TV-04_zero_cost": {
    "holding": { "shares": 100, "totalCost": 0 }, "quote": { "lastPrice": 10.00, "prevClose": 10.00 },
    "expect": { "marketValue": 1000, "unrealizedPnl": 1000, "returnRatePct": null, "todayPnl": 0 }
  },

  "TV-05_odd_lot": {
    "holding": { "shares": 1, "totalCost": 30 }, "quote": { "lastPrice": 33.33, "prevClose": 33.33 },
    "expect": { "marketValue": 33, "unrealizedPnl": 3 }
  },

  "TV-06_negative_rounding": {
    "holding": { "shares": 1, "totalCost": 100 }, "quote": { "lastPrice": 99.50, "prevClose": 99.50 },
    "expect": { "marketValue": 100, "unrealizedPnl": -1, "returnRatePct": -0.50 },
    "note": "內部 marketValue = 99.5、unrealized = -0.5;AwayFromZero 後分別是 100 與 -1"
  },

  "TV-07_missing_quote": {
    "holdings": [ { "symbol": "2330", "shares": 1000, "totalCost": 520000 } ], "quotes": {},
    "expect": { "rows": [ { "symbol": "2330", "quoteStatus": "missing", "marketValue": null, "unrealizedPnl": null } ],
                "summary": { "marketValue": 0, "totalCost": 0, "unrealizedPnl": 0, "returnRatePct": null, "todayPnl": 0, "missingQuoteCount": 1 } }
  },

  "TV-08_prevclose_null": {
    "holding": { "shares": 100, "totalCost": 9000 }, "quote": { "lastPrice": 100.00, "prevClose": null },
    "expect": { "change": null, "changePct": null, "todayPnl": null, "summaryTodayPnl": 0, "marketValue": 10000 }
  },

  "TV-09_min_fee": {
    "options": { "deductFees": true },
    "holding": { "kind": "stock", "shares": 100, "totalCost": 1000 }, "quote": { "lastPrice": 10.00, "prevClose": 10.00 },
    "expect": { "estFee": 20, "estTax": 3, "unrealizedPnl": -23, "returnRatePct": -2.30 },
    "note": "gross = 1000;floor(1000 * 0.001425) = 1,小於 MinFee,取 20;tax = floor(1000 * 0.003) = 3"
  }
}
```

---

## 7. 報價服務(`StockInventory.Quotes`)

### 7.1 MIS 呼叫與回應 `[VERIFY: Q-01]`

請求(GET):

```
https://mis.twse.com.tw/stock/api/getStockInfo.jsp?ex_ch={ex_ch}&json=1&delay=0&_={epochMillis}
ex_ch = 以 | 連接,每檔為  tse_{Symbol}.tw(上市,Market = 1)  或  otc_{Symbol}.tw(上櫃,Market = 2)
```

回應 JSON 的 `msgArray` 每個元素對應一檔,使用下列欄位:

| 欄位 | 意義 | 用途 |
| --- | --- | --- |
| `c` | 股票代號 | 對應 `Symbol` |
| `z` | 最近成交價;**實測盤中常為 `-`(只在該次更新有成交時才有值)** | 現價第一順位 |
| `trade.z` | 回應中 `trade` 物件內的最近成交價(實測存在,例如 `"trade":{"t":"09:26:19","v":35,"z":"116.0000"}`) | `z` 無效時的第二順位,仍視為成交價 |
| `a` | 最佳五檔賣價,以 `_` 分隔 | 取第 1 檔為最佳賣價 |
| `b` | 最佳五檔買價,以 `_` 分隔 | 取第 1 檔為最佳買價 |
| `y` | 昨收價 | `PrevClose` |
| `tlong` | 資料時間(毫秒時間戳) | `QuoteTimeUtc` |

實作前必須實測並寫進 `docs/verify-notes.md`:`y`、`tlong` 的欄位名稱與格式;`otc_` 前綴是否由同一端點查詢上櫃;是否需先取得 session cookie(例如先請求基本市況頁);單次 `ex_ch` 可帶幾檔;實際請求限制;除權息日的 `y` 是否為調整後的值(Q-04)。若實測與上表不同,以實測為準並更新本節。

數字解析:`decimal.Parse(..., CultureInfo.InvariantCulture)`;`-`、空字串、非數字、≤ 0 一律視為「沒有這個值」。

### 7.2 取價順序

| 順序 | 條件 | `LastPrice` | `PriceSource` |
| --- | --- | --- | --- |
| 1 | `z` 為數字且 > 0;否則 `trade.z` 為數字且 > 0 | `z` 或 `trade.z` | 1 |
| 2 | 否則,最佳買價與最佳賣價(各取第 1 檔)皆 > 0 | (買價 + 賣價) ÷ 2 | 2 |
| 3 | 否則 `y` 為數字且 > 0 | `y` | 3 |
| 4 | 都沒有 | 不更新 Quotes 與快取(保留原值) | — |

`PrevClose` 一律取 `y`(有效時),與 `PriceSource` 無關。`TradeDate` 取 `tlong` 換算成 Taipei 時間的日期,沒有 `tlong` 時取目前 Taipei 日期。`FetchedAtUtc` 為本系統收到回應的時間。

### 7.3 抓取排程(`BackgroundService`)

```
每 Quote:ActiveIntervalSeconds(5 秒)喚醒一次:
  state = MarketCalendar.GetState(nowTaipei)            # §7.5
  若 state 改變 → 推播 MarketStatus
  若 state != Open → 只補抓「快取裡還沒有價格」的標的(v1.3,見下方說明),然後結束本輪
  active = SignalR 連線數(不含已 Pause 的連線)
  若 active == 0 且 距上次抓取 < Quote:IdleIntervalSeconds → 結束本輪
  若處於失敗退避期 → 結束本輪
  symbols = SELECT DISTINCT Symbol FROM Holdings     # 每輪重新查詢,資料量小
  依 Instruments.Market 組成 ex_ch,每 Quote:BatchSize 檔一批
  每一批:先通過速率限制器(滑動視窗,每 5 秒最多 Quote:MaxRequestsPer5s 次;超過就等到下一個視窗)
         請求(逾時 Quote:TimeoutSeconds)→ 解析 → 套用(§7.2)
  只要有任何一批成功 → lastSuccessAtUtc = now;對所有未 Pause 的連線重算並推播 ViewUpdated
  全部批次失敗 → 失敗次數 + 1,記錄日誌
```

- 網站啟動時先從 `Quotes` 載入記憶體快取(`ConcurrentDictionary<string, QuoteInput>`),讓第一個畫面立即有價。
- 套用報價:同一個動作內更新記憶體快取並 upsert `Quotes`。
- 新增持股成功後呼叫 `RequestImmediateFetch(symbol)`,下一個週期優先抓該代號,不等無連線的 5 分鐘間隔。
- **非開盤時段的補抓(v1.3)**:`state != Open` 時,只抓「記憶體快取還沒有價格」的標的(例如收盤後才新增的持股;實測 MIS 收盤後仍回傳收盤價),已有價格者不抓。沒有優先請求時,補抓最多每 `Quote:IdleIntervalSeconds` 一次(避免抓不到價的標的整晚重試);新增持股的 `RequestImmediateFetch` 不受此節流限制。沿用同一個速率限制器與失敗退避。
- 失敗退避:連續失敗 3 次後,間隔依序拉長為 15、30、60 秒(之後維持 60 秒);成功一次即恢復正常間隔。
- 抓取失敗不得讓任何 API 失敗;一律回傳快取中的舊價。
- 不重複代號超過 `BatchSize × MaxRequestsPer5s` 時,一輪抓取會超過 5 秒,畫面由延遲偵測反映;v1 不做優先順序。

### 7.4 報價狀態

```
quoteStatus(quote, marketState, nowUtc):
  if quote == null or quote.LastPrice == null:  return Missing
  base = quote.PriceSource switch { Trade => Live, MidQuote => Reference, PrevClose => PrevClose }
  if marketState == Open
     and (nowUtc - quote.FetchedAtUtc).TotalSeconds > StaleSeconds
     and base in (Live, Reference):             return Delayed
  return base
```

JSON 字串:`quoteStatus` 為 `live`、`delayed`、`reference`、`prevclose`、`missing`;`priceSource` 為 `trade`、`midquote`、`prevclose`。

全域旗標:`market.stale = (state == Open) && (now - lastSuccessAtUtc) > 門檻`;`market.lastFetchedAtUtc = lastSuccessAtUtc`(從未成功抓取過時為 null,且此時 `stale` 為 false)。
其中門檻:有連線(未 Pause 的 SignalR 連線數 > 0)時為 `Quote:StaleSeconds`;無連線時抓取間隔拉長為 `Quote:IdleIntervalSeconds`,所以門檻為 `Quote:IdleIntervalSeconds + Quote:StaleSeconds`(預設 480 秒),避免正常的閒置間隔被誤判為報價中斷。
(v1.1 修正:原版固定用 `StaleSeconds`,與 §7.3 的閒置間隔矛盾。)

### 7.5 市場狀態

| `MarketState` | 條件(`Taipei Standard Time`) |
| --- | --- |
| `Open` | 週一至週五、日期不在 `MarketHolidays`,且 `Market:OpenTime` ≤ 時間 ≤ `Market:CloseTime`(含頭尾) |
| `Closed` | 上述日期條件成立,但時間在時段外 |
| `Holiday` | 週六、週日,或日期在 `MarketHolidays` |

邊界:08:29:59 為 Closed、08:30:00 為 Open、14:35:00 為 Open、14:35:01 為 Closed。

### 7.6 通知(Email)

- **報價中斷**:`state == Open` 且 `market.stale` 為真時寄一封;同一事件之後每 `Alert:CooldownMinutes` 分鐘最多再寄一封;恢復(`stale` 變回 false)時寄一封恢復通知。
- **登入異常**:同一帳號或同一來源 IP 在 `Alert:LoginFailWindowMinutes` 分鐘內失敗達 `Alert:LoginFailThreshold` 次時寄一封,同樣套用冷卻時間。來源 IP 取自 `CF-Connecting-IP`(見 §11)。
- 寄信失敗只記日誌,不影響主流程。

### 7.7 定期工作 `[VERIFY: Q-02、Q-03]`

| 工作 | 時機 | 內容 |
| --- | --- | --- |
| 標的主檔同步 | 每日 07:00(Taipei) | 取得上市、上櫃的股票與 ETF 名單,upsert `Instruments`;必須能分辨 `Kind`(ETF 或股票);不在名單者設 `IsActive = 0` |
| 休市日同步 | 每年 1 月 1 日,以及啟動時若缺當年資料 | 寫入 `MarketHolidays`(週六日不存) |

實測決定的來源(2026-10-06,詳見 `docs/verify-notes.md`):
- 標的主檔:證交所 ISIN 公告頁 `https://isin.twse.com.tw/isin/C_public.jsp?strMode=2`(上市)與 `strMode=4`(上櫃),編碼 Big5(ms950),上市頁約 9 MB。以 CFICode 分辨:`ESVUFR` = 股票(含創新板),`EP` 開頭 = 特別股(視為股票),`CE` 開頭 = ETF;ETN、DR、受益證券、權證不納入。
- 休市日:`https://www.twse.com.tw/rwd/zh/holidaySchedule/holidaySchedule?date=YYYY&response=json`。回應含週末與「開始交易日」「最後交易日」這類有交易的公告,同步時須排除。
同步失敗只記日誌並於下個週期重試,不影響網站運作。

---

## 8. API 契約

通則:

- 路徑前綴 `/api`,JSON 使用 camelCase,時間為 UTC ISO 8601(加 `Z`)。
- 需登入的 API 未登入時回 401(不要重新導向到登入頁)。
- `POST`、`PUT`、`DELETE` 必須帶 `X-CSRF-TOKEN`,否則 400。
- 屬於其他使用者或不存在的資源一律回 404。
- 所有路徑的 `UserId` 取自登入身分。

### 8.1 庫存

| 編號 | 方法與路徑 | 請求 | 成功回應 |
| --- | --- | --- | --- |
| API-01 | `GET /api/portfolios` | — | `200` `[{ "id": 1, "name": "長期", "sortOrder": 1, "holdingCount": 5 }]`,依 `sortOrder` 升冪 |
| API-02 | `POST /api/portfolios` | `{ "name": "長期" }` | `201` 同 API-01 的單筆物件 |
| API-03 | `PUT /api/portfolios/{id}` | `{ "name": "新名稱" }` | `200` 同上 |
| API-04 | `DELETE /api/portfolios/{id}` | — | `204`;寫入規則見 §5.4 |
| API-05 | `PUT /api/portfolios/order` | `{ "ids": [3, 1, 2] }` | `204`;`ids` 必須恰好等於該使用者所有庫存 Id(不多不少、不重複),否則 400;`sortOrder` = 索引 + 1 |

### 8.2 持股與檢視

| 編號 | 方法與路徑 | 請求 | 成功回應 |
| --- | --- | --- | --- |
| API-06 | `GET /api/holdings` | 查詢參數 `portfolioIds`(逗號分隔整數,省略代表全部)、`merge`(預設 `true`) | `200` View(見下) |
| API-07 | `POST /api/portfolios/{id}/holdings` | `{ "symbol": "0050", "totalCost": 280000, "shares": 2000 }` | `201` `{ "holdingId": 11, "portfolioId": 1, "symbol": "0050", "totalCost": 280000, "shares": 2000 }` |
| API-08 | `PUT /api/holdings/{id}` | `{ "totalCost": 290000, "shares": 2000 }` | `200` 同 API-07 的物件 |
| API-09 | `DELETE /api/holdings/{id}` | — | `204` |

- API-06 的 `portfolioIds` 含不存在或不屬於自己的 Id 時忽略該 Id;若全部無效,視為「全部」。只選一個庫存時 `merge` 無作用。
- API-07:`symbol` 先去除空白並轉大寫,必須存在於 `Instruments` 且 `IsActive = 1`;成功後呼叫 `RequestImmediateFetch(symbol)`。
- API-07、API-08、API-09 都依 §5.4 寫入 `HoldingChanges`。

View 結構(API-06 回應,SignalR `ViewUpdated` 的內容也完全相同):

```json
{
  "asOf": "2026-10-05T05:12:03Z",
  "market": { "state": "open", "stale": false, "lastFetchedAtUtc": "2026-10-05T05:12:01Z" },
  "summary": {
    "marketValue": 901000, "totalCost": 800000,
    "unrealizedPnl": 101000, "returnRatePct": 12.63,
    "todayPnl": -7000, "missingQuoteCount": 0
  },
  "rows": [
    {
      "symbol": "0050", "name": "元大台灣50", "market": "TWSE", "kind": "etf",
      "shares": 2000, "totalCost": 280000, "avgCost": 140.00,
      "lastPrice": 150.50, "prevClose": 149.00, "change": 1.50, "changePct": 1.01,
      "priceSource": "trade", "quoteStatus": "live",
      "marketValue": 301000, "unrealizedPnl": 21000, "returnRatePct": 7.50, "todayPnl": 3000,
      "estFee": null, "estTax": null,
      "sources": [
        { "portfolioId": 1, "holdingId": 11, "shares": 1000, "totalCost": 130000 },
        { "portfolioId": 2, "holdingId": 25, "shares": 1000, "totalCost": 150000 }
      ]
    }
  ]
}
```

- `market.state` 為 `open`、`closed`、`holiday`;`rows[].market` 為 `TWSE`、`TPEx`;`rows[].kind` 為 `stock`、`etf`。
- 所有可能為 null 的欄位見 §6.2 與 §6.3;null 一律輸出 JSON `null`,不要省略欄位。
- `rows[].sources`:合併列且該組有 2 筆以上持股時才有內容,否則為 `[]`。
- 合計(`summary`)與各列都已依 §6.3 捨入。

### 8.3 搜尋、設定、匯出

| 編號 | 方法與路徑 | 說明 |
| --- | --- | --- |
| API-10 | `GET /api/instruments/search?q=&limit=10` | 只回 `IsActive = 1`。`q` 去除空白後 1 至 20 字元;比對 `Symbol` 開頭相符或 `Name` 包含 `q`;排序:代號完全相符、代號開頭相符、名稱相符;`limit` 預設 10、最大 20。回應 `[{ "symbol": "0050", "name": "元大台灣50", "market": "TWSE", "kind": "etf" }]` |
| API-11 | `GET /api/settings`、`PUT /api/settings` | 本文 `{ "colorScheme": 0, "deductFees": false, "feeRate": 0.001425, "feeDiscount": 1.000, "selectedPortfolioIds": null }`。`PUT` 為整份覆寫;`selectedPortfolioIds` 為 null 或整數陣列(不屬於自己的 Id 會被丟棄);範圍依 §5.3 的 CHECK |
| API-12 | `GET /api/export/holdings.csv` | 參數同 API-06。`text/csv; charset=utf-8`,**含 BOM**;檔名 `holdings-yyyyMMdd-HHmm.csv`(Taipei 時間)。欄位(含標題列):`代號,名稱,市場,股數,總成本,均價,現價,漲跌,漲跌幅(%),市值,未實現損益,報酬率(%),今日損益,報價狀態`;`merge=false` 時最前面多一欄 `庫存`;`DeductFees` 開啟時最後多 `預估手續費,預估證交稅`。數值為顯示值,null 為空白;字串欄位以 `=`、`+`、`-`、`@` 開頭時前面補一個 `'` 防止試算表公式注入 |

### 8.4 管理員 API 與健康檢查(API-20 至 API-24 需 `Admin` 角色,否則 403;API-30 為匿名)

| 編號 | 方法與路徑 | 說明 |
| --- | --- | --- |
| API-20 | `GET /api/admin/users` | `[{ "id": "…", "userName": "…", "email": "…", "displayName": "…", "isActive": true, "twoFactorEnabled": true, "roles": ["User"] }]` |
| API-21 | `POST /api/admin/users` | `{ "userName", "email", "displayName", "initialPassword" }`;建立角色 `User`、`MustChangePassword = 1`、未啟用 TOTP;密碼不符 §11 政策回 400;`userName` 重複回 409。回 `201` |
| API-22 | `POST /api/admin/users/{id}/deactivate`、`.../activate` | 設定 `IsActive`;停用時同時更新 `SecurityStamp` 使既有登入失效;不可停用自己(400);回 `204` |
| API-23 | `POST /api/admin/users/{id}/reset-password` | 產生 16 字元隨機臨時密碼,設 `MustChangePassword = 1`,更新 `SecurityStamp`;回 `200` `{ "temporaryPassword": "…" }`(只這一次回傳,不記日誌) |
| API-24 | `POST /api/admin/users/{id}/reset-2fa` | 重設驗證器金鑰、停用 TOTP、清除還原碼;回 `204` |
| API-30 | `GET /health`(匿名) | `{ "status": "ok", "db": "ok", "quotes": { "state": "open", "stale": false, "lastFetchedAtUtc": "…" } }`;資料庫失敗,或 `state == open` 且 `stale == true`,回 503 並把 `status` 設為 `down`;其餘回 200。不得回傳任何使用者資料或例外細節 |

---

## 9. SignalR 契約

Hub:`/hubs/view`,需登入(Cookie),啟用 WebSocket。

| 方向 | 方法或事件 | 參數與行為 |
| --- | --- | --- |
| 用戶端 → 伺服器 | `SetView(int[]? portfolioIds, bool merge)` | 記錄這條連線正在看的組合(`null` = 全部;無效 Id 規則同 API-06);立即推一次 `ViewUpdated` |
| 用戶端 → 伺服器 | `Pause()` | 此連線暫停推播,並且不計入「有連線」 |
| 用戶端 → 伺服器 | `Resume()` | 取消暫停,立即推一次 `ViewUpdated` |
| 伺服器 → 用戶端 | `ViewUpdated(view)` | 內容與 API-06 相同(§8.2) |
| 伺服器 → 用戶端 | `MarketStatus(market)` | `{ "state", "stale", "lastFetchedAtUtc" }`,狀態或旗標改變時推送 |

- 伺服器維護 `ConcurrentDictionary<connectionId, { UserId, PortfolioIds?, Merge, Paused }>`;連線中斷時移除。
- 每次報價套用完成,對每條未暫停的連線以其 `UserId`、組合與 `merge` 重新呼叫 `ViewCalculator.Build` 並推播;不得把一位使用者的資料推給另一位。
- 前端行為(§12.4):`visibilitychange` 為 hidden 時 `Pause()`,visible 時 `Resume()`;使用自動重連(間隔 0、2、5、10、30 秒);連線中斷期間,頁面可見時改為每 5 秒輪詢 API-06,連線恢復後停止輪詢。

---

## 10. 驗證規則與錯誤格式

### 10.1 驗證規則

| 欄位 | 規則 |
| --- | --- |
| 庫存名稱 | 去除前後空白後 1 至 30 字元;同一使用者內不可重複 |
| 標的代碼 | 去除空白、轉大寫;必須存在於 `Instruments` 且 `IsActive = 1` |
| 總成本 | 整數,0 至 999,999,999,999 |
| 股數 | 整數,1 至 1,000,000,000 |
| 同庫存同代號 | 不可重複(409) |
| 數量上限 | 每人 `Limits:MaxPortfoliosPerUser` 個庫存、每庫存 `Limits:MaxHoldingsPerPortfolio` 檔持股,超過回 422 |
| `feeRate` | 0 至 0.01 |
| `feeDiscount` | 0.001 至 1 |
| `colorScheme` | 0 或 1 |

### 10.2 錯誤格式

一律使用 RFC 7807 `application/problem+json`,另加 `code` 欄位供前端判斷:

```json
{
  "type": "https://stockinventory.newsafety.hk/problems/validation",
  "title": "輸入資料有誤",
  "status": 400,
  "code": "VALIDATION_FAILED",
  "errors": { "totalCost": ["總成本必須是 0 到 999,999,999,999 的整數"] }
}
```

| HTTP | `code` | 情境 |
| --- | --- | --- |
| 400 | `VALIDATION_FAILED` | 欄位格式或範圍錯誤(`errors` 列出各欄位訊息);防偽權杖缺失 |
| 401 | `UNAUTHENTICATED` | 未登入或登入逾時 |
| 403 | `FORBIDDEN` | 非 Admin 呼叫 `/api/admin/*` |
| 404 | `NOT_FOUND` | 資源不存在,或屬於其他使用者 |
| 409 | `DUPLICATE` | 庫存名稱重複、同庫存同代號、帳號名稱重複 |
| 422 | `LIMIT_EXCEEDED` | 超過庫存或持股數量上限 |
| 400 | `CONSENT_REQUIRED` | 截圖辨識未勾選同意送第三方服務 |
| 413 | `PAYLOAD_TOO_LARGE` | 截圖超過大小上限 |
| 415 | `UNSUPPORTED_MEDIA` | 截圖不是允許的格式(JPG、PNG、WebP) |
| 422 | `NOTHING_RECOGNIZED` | 圖片中找不到持股資料 |
| 429 | `RATE_LIMITED` | 辨識超過速率或已有進行中的請求,附 `Retry-After` |
| 502 | `UPSTREAM_ERROR` | 辨識服務失敗,或辨識功能尚未啟用(未設定金鑰,503) |
| 504 | `UPSTREAM_TIMEOUT` | 辨識服務逾時 |
| 500 | `INTERNAL_ERROR` | 未預期錯誤;細節只寫日誌,不回傳 |

---

## 11. 認證與安全要求

| 編號 | 要求 |
| --- | --- |
| SEC-01 | 密碼:Identity 預設雜湊;長度 ≥ `Auth:MinPasswordLength`(12);不強制字元組合 |
| SEC-02 | 所有帳號強制 TOTP(RFC 6238)。登入後若 `MustChangePassword = 1`,只允許進入 `/Account/Manage/Password`(以及登出與靜態檔);改完密碼後若未啟用 TOTP,只允許進入 `/Account/Manage/2fa`;兩者都完成前,其他頁面與 API 一律導向或回 403 |
| SEC-03 | 還原碼使用 Identity 內建機制;啟用 TOTP 時一次性顯示,關閉頁面後不可再看 |
| SEC-04 | 連續失敗 `Auth:LockoutMaxFailures` 次鎖定 `Auth:LockoutMinutes` 分鐘 |
| SEC-05 | 登入 Cookie:`HttpOnly`、`Secure`、`SameSite=Lax`;閒置 `Auth:IdleMinutes` 分鐘登出(滑動過期) |
| SEC-06 | 「信任此裝置」只略過 TOTP、不略過密碼;使用 Identity 內建的記住裝置 Cookie,期限 `Auth:TrustDeviceDays` 天 |
| SEC-07 | 登入失敗訊息一律「帳號或密碼錯誤」,不透露帳號是否存在;`IsActive = 0` 的帳號同樣顯示此訊息 |
| SEC-08 | 資料隔離:`UserId` 只取自登入身分;所有庫存與持股查詢都以 `UserId` 篩選(EF Core 全域查詢篩選,或統一的資料存取層),他人資源回 404 |
| SEC-09 | CSRF:`POST`、`PUT`、`DELETE` 驗證 `X-CSRF-TOKEN` |
| SEC-10 | XSS:Razor 預設編碼;Vue 不使用 `v-html`;CSP `default-src 'self'; img-src 'self' blob:; frame-ancestors 'none'`(v1.4:為截圖預覽加入 `blob:`,不加 `data:` 與 `https:`),腳本與樣式都由本站提供 |
| SEC-11 | 安全標頭:`X-Content-Type-Options: nosniff`、`Referrer-Policy: strict-origin-when-cross-origin`;HSTS `max-age=15552000`,**不加** `includeSubDomains` |
| SEC-12 | Forwarded Headers:只信任 Cloudflare 的 IP 範圍(設定檔維護);來源 IP 取自 `CF-Connecting-IP`,用於登入防護與日誌 |
| SEC-13 | 資料存取一律 EF Core 參數化,不拼接 SQL |
| SEC-14 | 正式環境不顯示例外細節,只回通用錯誤頁與 §10.2 的 JSON |
| SEC-15 | Data Protection 金鑰存在 `DataProtection:KeyDirectory`(站台資料夾以外的固定路徑),並納入備份 |
| SEC-16 | 網站的 SQL 登入只有 `StockInventory` 的資料讀寫權限,沒有結構變更權限;Migrations 由人工以另一個有權限的帳號執行,網站啟動時**不得**自動執行 Migrations |
| SEC-17 | 日誌規則見 §0 第 7 點 |
| SEC-18 至 SEC-24 | 截圖辨識的上傳驗證、不落地、日誌、金鑰、第三方同意、防濫用、防提示注入,見 §17 與 `docs/sa/FR-25-截圖辨識新增持股.md` §8 |

---

## 12. 前端規格

### 12.1 頁面

| 路徑 | 頁面 | 權限 |
| --- | --- | --- |
| `/` | 庫存總覽(主畫面) | 登入 |
| `/Account/Login` | 登入 | 匿名 |
| `/Account/LoginWith2fa` | 雙重驗證,可勾「信任此裝置 30 天」 | 已通過密碼 |
| `/Account/LoginWithRecoveryCode` | 還原碼登入 | 已通過密碼 |
| `/Account/Manage/2fa` | 掃描 QR Code 設定 TOTP,一次性顯示還原碼 | 登入 |
| `/Account/Manage/Password` | 變更密碼 | 登入 |
| `/Portfolios` | 庫存管理:新增、改名、排序、刪除 | 登入 |
| `/Settings` | 顏色慣例、扣費預估、手續費率與折扣 | 登入 |
| `/Admin/Users` | 使用者管理(API-20 至 API-24) | Admin |

### 12.2 主畫面版面

- 由上到下:頂端提示列(有狀態時才出現)→ 庫存選擇列 → 合計區 → 持股清單 → 「新增持股」。
- 合計區使用 `position: sticky` 固定在庫存選擇列下方。
- 寬度 ≥ 768 px 顯示表格;< 768 px 顯示卡片。
- 初次載入顯示骨架畫面;API 失敗時保留舊資料並顯示「重試」。

### 12.3 元件規則

| 元件 | 規則 |
| --- | --- |
| 頂端提示列 | `market.stale` 為真:黃色「報價延遲,最後更新 HH:mm:ss」(Taipei 時間);`state = closed`:灰色「已收盤」;`state = holiday`:灰色「休市」;其他情況不顯示 |
| 庫存選擇列 | 「全部」加各庫存標籤(依 `sortOrder`)。點「全部」取消其他勾選;點個別庫存切換勾選;全部取消時自動回到「全部」。選擇變更時:呼叫 `SetView`、`PUT /api/settings`、更新網址 `?p=1,3`(「全部」時移除該參數)。載入時網址參數優先於已儲存的選擇 |
| 合計區 | 市值、總成本、未實現損益加報酬率、今日損益;`missingQuoteCount > 0` 時加註「另有 N 檔缺價未計入」 |
| 持股清單 | 預設依伺服器回傳順序(市值由大到小);桌機點欄位標題可在前端重新排序(標的、市值、未實現損益、報酬率、今日損益),排序只是重排已收到的列,不重新計算 |
| 合併列 | `sources` 非空時列尾有「▸」,點開顯示各庫存的股數與總成本;庫存名稱由 API-01 的結果對照 |
| 手機卡片 | 預設顯示:代號與名稱、現價、漲跌幅、股數、均價、未實現損益加報酬率、今日損益;點開卡片顯示總成本與市值;`estFee` 非 null 時一併顯示預估手續費與證交稅 |
| 桌機表格 | 顯示所有欄位;`DeductFees` 關閉時不顯示 `estFee`、`estTax` 欄 |
| 新增持股 | 桌機為對話框、手機為底部面板:搜尋標的(API-10)→ 輸入總成本與股數 → 即時顯示「均價 = {總成本 ÷ 股數,小數 2 位}」(這是唯一允許前端計算的地方,僅為預覽,最終以伺服器為準)→ 儲存 |
| 編輯持股 | 只能改總成本與股數 |
| 刪除 | 一律二次確認(文案見 §12.5) |
| 空狀態 | 沒有任何庫存:引導「建立第一個庫存」;庫存內沒有持股:引導「新增持股」 |
| 數字顏色 | 依 `colorScheme`:0 為紅漲綠跌、1 為綠漲紅跌;零與 null 用中性色;使用 CSS 變數與 class(`.pnl-up`、`.pnl-down`、`.pnl-flat`),並跟隨系統淺色與深色模式 |
| 報價狀態標籤 | `reference` 標「參考價」、`prevclose` 標「昨收」、`delayed` 標灰色「延遲」、`missing` 的數字欄顯示「—」 |

### 12.4 即時更新(前端)

1. 頁面載入:先 `GET /api/holdings` 畫出畫面,再建立 SignalR 連線並呼叫 `SetView`。
2. 收到 `ViewUpdated` 就整份取代畫面資料。
3. `visibilitychange`:hidden → `Pause()`;visible → `Resume()`。
4. 連線中斷:使用自動重連;期間頁面可見時每 5 秒輪詢 `GET /api/holdings`;重連成功後停止輪詢並重新 `SetView`。

### 12.5 顯示格式與文案

- 數字:千分位逗號;正數前加「+」、負數前加「−」(U+2212)、零不加符號;報酬率與漲跌幅後加「%」;均價固定 2 位小數;null 顯示「—」。前端只做格式化,不運算。
- 繁體中文文案:

| 用途 | 文案 |
| --- | --- |
| 報價延遲提示 | `報價延遲,最後更新 {HH:mm:ss}` |
| 市場狀態 | `已收盤`、`休市` |
| 報價狀態 | `參考價`、`昨收`、`延遲` |
| 合計缺價 | `另有 {n} 檔缺價未計入` |
| 空狀態 | `建立第一個庫存`、`新增持股` |
| 刪除庫存確認 | `刪除「{name}」將一併刪除其中所有持股,且無法復原。確定刪除?` |
| 刪除持股確認 | `刪除 {symbol} {name}?刪除後無法復原。` |
| 均價預覽 | `均價 = {avg}` |
| 載入失敗 | `載入失敗,請重試` |
| 登入失敗 | `帳號或密碼錯誤` |

---

## 13. 部署(IIS)

| 項目 | 設定 |
| --- | --- |
| 站台 | 名稱 `StockInventory`;繫結 `stockInventory.newsafety.hk` 的 443 埠,使用 Cloudflare Origin 憑證 |
| 應用程式集區 | 獨立集區;.NET CLR 版本「無受控程式碼」;in-process;獨立執行身分 |
| 保持常駐 | 集區啟動模式 `AlwaysRunning`、閒置逾時 0、站台 `preloadEnabled = true`(背景抓價服務必須常駐) |
| 回收 | 關閉預設的循環回收,改為每日固定 03:00 |
| 必要元件 | 與 .NET LTS 版本對應的 Hosting Bundle;IIS 功能「WebSocket 通訊協定」 |
| 環境變數 | `ASPNETCORE_ENVIRONMENT=Production`;機敏設定見 §4 的 **(env)** 項目 |
| 資料夾權限 | 集區身分對站台資料夾唯讀;對日誌資料夾與 Data Protection 金鑰資料夾可寫 |
| 防火牆 | Windows 防火牆 443 只放行 Cloudflare 公布的 IP 範圍 |
| Cloudflare | SSL/TLS Full (strict)、Always Use HTTPS、WebSockets 開啟;建議對 `/Account/*` 設速率限制 |

發佈流程(沿用現行做法,不暫停網站):

1. 有結構變更時先備份 `StockInventory`。
2. `dotnet publish -c Release`。
3. 有結構變更時產生並以具結構變更權限的帳號執行遷移腳本(`dotnet ef migrations script --idempotent`)。
4. 把輸出資料夾內容複製到站台目錄(若 DLL 被鎖定而失敗,先回收應用程式集區再複製)。
5. 驗證:`/health` 回 200、能登入、主畫面數字與 §6.4 的 TV-01 一致。

---

## 14. 開發階段與任務

每個任務完成後勾選;每階段最後一項是閘門,必須全部通過才進下一階段。P4 可在 P0 完成後與 P1 至 P3 並行;P3 在 P4 完成前使用假報價提供者(`FakeQuoteProvider`,價格取 §6.4 TV-01)。

### P0 環境與骨架

- [ ] T0.1 依 §3 建立方案、專案與測試專案;`.gitignore` 排除 `appsettings.Production.json`、`*.user`、日誌與金鑰資料夾
- [ ] T0.2 設定鍵綁定(§4)、環境變數載入、Serilog 檔案日誌
- [ ] T0.3 EF Core 實體與初始 Migration(§5 全部資料表加 Identity)
- [ ] T0.4 `/health`(API-30)
- [ ] T0.5 依 §13 發佈到 IIS,經 Cloudflare 以 HTTPS 開啟
- [ ] **閘門 G0**:瀏覽器經 Cloudflare 以 HTTPS 開啟網站,`/health` 回 200

### P1 帳號與安全

- [ ] T1.1 Identity(`Guid` 主鍵)、`AppUser` 額外屬性、密碼政策、鎖定、Cookie 設定(SEC-01、04、05)
- [ ] T1.2 啟動時依 `Seed:*` 建立第一位 Admin
- [ ] T1.3 登入、TOTP 設定、還原碼、記住裝置、強制改密碼與強制設定 TOTP 的流程(SEC-02、03、06、07)
- [ ] T1.4 管理員 API 與頁面(API-20 至 API-24)
- [ ] T1.5 目前使用者服務與資料隔離(SEC-08)
- [ ] T1.6 Forwarded Headers、CSRF、安全標頭、CSP、HSTS(SEC-09 至 SEC-12、SEC-14)
- [ ] **閘門 G1**:IT-01、IT-06 通過

### P2 庫存與持股

- [ ] T2.1 標的主檔同步與搜尋 API-10 `[VERIFY: Q-02]`
- [ ] T2.2 庫存 API-01 至 API-05(含上限、排序、§5.4 規則)
- [ ] T2.3 持股 API-07 至 API-09(含唯一性、上限、`HoldingChanges`)
- [ ] T2.4 `/Portfolios` 頁面與持股新增、編輯、刪除的基本畫面(此階段不顯示價格)
- [ ] **閘門 G2**:IT-02、IT-03、IT-04、IT-05、UT-10 通過

### P3 計算與檢視

- [ ] T3.1 `StockInventory.Core`:`ViewCalculator` 與捨入工具,通過 TV-01 至 TV-09
- [ ] T3.2 API-06(含 `portfolioIds` 無效 Id 規則與 `merge`)
- [ ] T3.3 設定 API-11 與 `/Settings` 頁面
- [ ] T3.4 CSV 匯出 API-12
- [ ] T3.5 主畫面:庫存選擇列、網址參數、記住選擇、合計區、清單(卡片與表格)、合併列展開、顏色慣例(§12)
- [ ] **閘門 G3**:UT-01 至 UT-07、IT-07、IT-08 通過

### P2b 截圖辨識新增持股(FR-25,排在 P3 之後,不阻擋主線)

- [ ] T2b.1 `IHoldingImageRecognizer` 與辨識結果正規化、狀態判定(Core 純函式)
- [ ] T2b.2 視覺 API 用戶端(`Vision:*`)、速率限制(`AddRateLimiter`)
- [ ] T2b.3 API-13 辨識(不寫 DB)
- [ ] T2b.4 共用持股新增/更新領域服務、API-14 批次確認寫入
- [ ] T2b.5 `/Portfolios` 截圖核對表前端
- [ ] T2b.6 CSP 調整(`img-src 'self' blob:`)
- [ ] **閘門 G2b**:UT-11、IT-09 至 IT-12 通過

### P4 報價服務

- [ ] T4.1 實測 MIS(`[VERIFY: Q-01、Q-04]`),把真實回應存成樣本,寫 `docs/verify-notes.md`,必要時更新 §7.1
- [ ] T4.2 解析器與取價順序,通過 §15.2 的解析測試
- [ ] T4.3 抓取背景服務、速率限制、退避、記憶體快取、`Quotes` upsert、啟動載入、`RequestImmediateFetch`
- [ ] T4.4 休市日同步與市場狀態 `[VERIFY: Q-03]`
- [ ] T4.5 SignalR Hub、連線註冊表、推播(§9)與前端即時更新(§12.4)
- [ ] T4.6 報價狀態與延遲偵測、Email 通知(§7.4、§7.6)、`/health` 的報價欄位
- [ ] **閘門 G4**:UT-08、UT-09、解析測試通過;盤中實測約每 5 秒更新

### P5 上線

- [ ] T5.1 手機驗收與 §16.3 的手動驗收清單
- [ ] T5.2 備份還原演練、Cloudflare 與防火牆設定、外部健康檢查監控
- [ ] **閘門 G5**:§16.3 手動驗收清單全部勾選

---

## 15. 測試規格

### 15.1 單元測試(`StockInventory.Core.Tests`)

| 編號 | 範圍 | 案例 |
| --- | --- | --- |
| UT-01 | 均價 | TV-03,另加 `Shares = 1` |
| UT-02 | 單檔損益 | TV-01 的兩檔,逐欄比對 |
| UT-03 | 合計 | TV-01 的合計欄;報酬率必須是 12.63(不得為 12.62) |
| UT-04 | 同代號合併 | TV-02 的 `merge=true, deductFees=false` |
| UT-05 | 扣費預估 | TV-02 的另外兩個情境與 TV-09 |
| UT-06 | 缺價與缺昨收 | TV-07、TV-08、TV-04 |
| UT-07 | 四捨五入 | TV-05、TV-06 |
| UT-08 | 取價順序 | `z` 有值;`z` 為 `-` 且買賣價有值;只剩 `y`;全部沒有時不覆蓋舊值 |
| UT-09 | 市場與報價狀態 | §7.5 的四個時間邊界;週六日;`MarketHolidays` 內的日期;`delayed` 在 `StaleSeconds` 的邊界(剛好 180 秒不算延遲、180.001 秒算延遲) |
| UT-10 | 驗證規則 | §10.1 各欄位的上下界與錯誤訊息 |

### 15.2 解析測試(`StockInventory.Quotes.Tests`,不連網路)

樣本由 T4.1 實際抓取後存成檔案。

| 樣本 | 情境 | 預期 |
| --- | --- | --- |
| 正常 | 上市股票與 ETF 各一檔,`z` 有值 | `PriceSource = 1` |
| 缺成交價 | `z` 為 `-`,有最佳買賣價 | `PriceSource = 2`,取中間價 |
| 只有昨收 | `z` 為 `-`,買賣價為空 | `PriceSource = 3` |
| 停牌或欄位缺漏 | 沒有可用價格 | 不覆蓋 Quotes 原值 |
| 查無代號 | `msgArray` 為空 | 該代號略過,不報錯 |
| 上櫃 | `otc_` 前綴的回應 | 正確解析 |
| 格式異常 | 非 JSON、HTML 錯誤頁 | 視為失敗,保留舊價,記錄失敗 |
| 逾時 | 超過 `Quote:TimeoutSeconds` | 視為失敗,進入退避 |

### 15.3 整合測試(`StockInventory.Web.Tests`,使用獨立的測試資料庫,每次重建)

| 編號 | 案例 |
| --- | --- |
| IT-01 | 資料隔離:使用者 A 讀取、修改、刪除使用者 B 的庫存與持股,一律 404 |
| IT-02 | 唯一性:同名庫存、同庫存內同代號,回 409 |
| IT-03 | 上限:第 21 個庫存、第 201 檔持股,回 422 |
| IT-04 | 連動刪除:刪除庫存後持股一併刪除,每筆持股在 `HoldingChanges` 留一筆 `D`;`SelectedPortfolioIds` 移除該 Id |
| IT-05 | 異動紀錄:新增、修改、刪除持股各寫一筆 `A`、`U`、`D`,前後值正確;修改後值未變時不寫 |
| IT-06 | 登入:失敗 5 次鎖定;未設定 TOTP 不能進主畫面與 API;停用的帳號不能登入 |
| IT-07 | API-06 的組合:全部、單一、多選、`merge` 開關;`portfolioIds` 含無效 Id 時忽略,全部無效視為全部 |
| IT-08 | CSV 匯出:含 BOM、欄位順序正確、`merge` 兩種模式、公式注入防護 |

---

## 16. 待確認、已知限制與驗收

### 16.1 開發時待確認 `[VERIFY]`

| 編號 | 項目 | 做法 |
| --- | --- | --- |
| Q-01 | MIS 的實際行為:`otc_` 前綴、`y` 與 `tlong` 欄位、session cookie、單次可查幾檔、請求限制 | T4.1 實測,更新 §7.1 與 `Quote:*` |
| Q-02 | 標的主檔來源,以及如何辨識 ETF 與股票 | T2.1 選定公開資料來源,確認有可分辨 `Kind` 的欄位 |
| Q-03 | 休市日資料來源端點 | T4.4 |
| Q-04 | 除權息當天 MIS 的昨收是否為調整後的值 | **已實測(2026-10-06,2614):是調整後的除權息參考價**,公式不改,見 §16.2 |
| Q-05 | 現價與漲跌的顯示位數 | 目前為交易所原值、最多 2 位小數(§6.3);若使用者要求整數,只改 §6.3 |
| Q-06 | 最低手續費與證交稅特例 | 由使用者確認後調整 `Fees:*` |
| Q-07 | 視覺 API 模型選型與辨識準確度(FR-25) | T2b 以使用者提供、已去識別化的 10 至 20 張券商截圖實測,結果寫入 `docs/verify-notes.md`;門檻:代號 ≥ 98%、股數與總成本逐欄 ≥ 95% |

### 16.2 已知限制(不是缺陷,不要「修正」)

- MIS 是證交所網站內部使用的 API,沒有正式文件與服務保證;更新約每 5 秒,不是逐筆。
- 交易所資訊自用免同意;提供他人使用屬加值轉供,需向證交所取得同意。v1 只供擁有者自用。
- SQL Server Developer 版授權限開發與測試;日後換 Express 或 Standard 只需還原備份,程式碼不用改。
- 零股市值以整股報價估算。
- 手續費最低值是假設,債券型 ETF 等特殊標的的證交稅可能不同;扣費預估僅供參考。
- 持股為快照模式,成本與股數須人工維護;沒有已實現損益、配息、歷史績效。
- 截圖辨識可能出錯,代號、股數、總成本與單位(股或張)須由使用者核對;辨識的圖片會傳送至第三方服務(Anthropic API),不在本站保存。
- 除權息當天,MIS 的昨收 `y` 是除權息調整後的參考價(實測),所以漲跌、今日損益都以參考價為基準,與交易所顯示一致;配息造成的價格下跌不會算成今日虧損,配息後的成本需使用者自行調整。

### 16.3 手動驗收清單

- [ ] 手機(iOS Safari、Android Chrome):登入加 TOTP、切換庫存、新增、編輯、刪除持股
- [ ] 手機鎖屏後回到前景,畫面立即補上最新價
- [ ] 盤中價格約每 5 秒更新;收盤後標示「已收盤」;假日標示「休市」
- [ ] 暫時讓報價抓取失敗,3 分鐘後出現黃色提示與 Email;恢復後提示消失並收到恢復通知
- [ ] 直接以固定 IP 連線被擋下,只能經 Cloudflare 進入;SignalR 的 WebSocket 連線正常
- [ ] 重新發佈後,已登入的 Cookie 與記住裝置仍有效
- [ ] 備份還原演練成功,還原後數字與還原前一致
- [ ] 挑一檔實際持股,和券商 App 的現價、損益互相比對

---

## 17. 截圖辨識新增持股(FR-25,v1.4)

完整的功能說明、API-13 與 API-14 的請求與回應、狀態值、驗證規則、前端文案、測試案例與給實作者的注意事項,見 **`docs/sa/FR-25-截圖辨識新增持股.md`**(SA 規格交付文件,2026-10-07 使用者確認)。該文件視為本規格的附錄;兩者衝突時依 §0 第 10 點停止該項實作並回報。

摘要:
- 辨識只輸出股票代號、股數(含單位)、總成本;只用代號對應 `Instruments`;不做名稱比對、不由均價推估成本。
- 辨識端點(API-13)不寫資料庫;使用者核對後,由批次端點(API-14,單一交易)寫入,每筆各寫 `HoldingChanges` 的 `A` 或 `U`。
- 同庫存已存在的標的預設略過,可逐列選「更新為辨識值」;不提供全部覆蓋或加總合併。
- 圖片不保存、不落地磁碟、重新編碼去除 EXIF 後才送出;每次上傳前須勾選同意;日誌不記錄圖片、辨識結果、代號、股數、成本。
- 資料庫:無異動。
