<#
  [VERIFY] 實測腳本(Windows PowerShell 5.1 或 PowerShell 7 都可執行)。
  用途:實測 MIS(Q-01、Q-04)、標的主檔來源(Q-02)、休市日來源(Q-03),並把結果存成檔案與 summary.txt。

  用法(請在「可以上網的電腦」上執行,盤中與盤後各跑一次最理想):
    powershell -ExecutionPolicy Bypass -File .\collect-samples.ps1
    powershell -ExecutionPolicy Bypass -File .\collect-samples.ps1 -ExDivSymbol 2330   # 除權息當天,檢查昨收是否為調整後的值

  輸出:同一資料夾下的 verify-out\ 內有原始回應檔,以及 summary.txt(請把 summary.txt 的內容貼給我)。
  這個腳本只對外送出 GET 請求,不會修改任何東西。本腳本在撰寫時未能實際執行(沙盒連不到這些網站),若有語法錯誤請把錯誤訊息貼給我。
#>
param(
  [string]$ExDivSymbol = ""
)

$ErrorActionPreference = 'Continue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36'
$out = Join-Path $PSScriptRoot 'verify-out'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$summary = Join-Path $out 'summary.txt'
Set-Content -Path $summary -Value ("實測時間(本機):" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')) -Encoding UTF8

function Log([string]$text) {
  Write-Host $text
  Add-Content -Path $summary -Value $text -Encoding UTF8
}

function Get-Raw([string]$name, [string]$url, $session) {
  Log ""
  Log "=== $name ==="
  Log "URL: $url"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  try {
    if ($session) {
      $r = Invoke-WebRequest -Uri $url -UseBasicParsing -WebSession $session -UserAgent $ua -TimeoutSec 20
    } else {
      $r = Invoke-WebRequest -Uri $url -UseBasicParsing -UserAgent $ua -TimeoutSec 20
    }
    $sw.Stop()
    $bytes = $r.RawContentStream.ToArray()
    [IO.File]::WriteAllBytes((Join-Path $out ($name + '.bin')), $bytes)
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    [IO.File]::WriteAllText((Join-Path $out ($name + '.txt')), $text, (New-Object Text.UTF8Encoding($false)))
    Log ("狀態: {0}  大小: {1} bytes  耗時: {2} ms  Content-Type: {3}" -f $r.StatusCode, $bytes.Length, $sw.ElapsedMilliseconds, $r.Headers['Content-Type'])
    if ($r.Headers['Set-Cookie']) { Log ("Set-Cookie: " + (($r.Headers['Set-Cookie'] -split ';')[0])) }
    return $text
  } catch {
    $sw.Stop()
    $code = ''
    if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    Log ("失敗: HTTP {0}  {1}  耗時: {2} ms" -f $code, $_.Exception.Message, $sw.ElapsedMilliseconds)
    return $null
  }
}

function Preview([string]$text, [int]$n) {
  if ($null -eq $text) { return }
  if ($text.Length -gt $n) { Log ($text.Substring(0, $n) + ' ...(截斷)') } else { Log $text }
}

function Mis-Url([string]$exCh) {
  $ms = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
  return ('https://mis.twse.com.tw/stock/api/getStockInfo.jsp?ex_ch=' + [uri]::EscapeDataString($exCh) + '&json=1&delay=0&_=' + $ms)
}

function Show-Fields($text) {
  if ($null -eq $text) { return }
  try {
    $j = $text | ConvertFrom-Json
    Log ("msgArray 筆數: " + @($j.msgArray).Count)
    foreach ($e in @($j.msgArray)) {
      $line = "  "
      foreach ($k in @('c', 'n', 'ex', 'z', 'a', 'b', 'y', 'tlong', 't', 'd')) {
        $line += ($k + '=' + $e.$k + '  ')
      }
      Log $line
    }
  } catch {
    Log "  (無法解析成 JSON:$($_.Exception.Message))"
  }
}

Log ""
Log "############ Q-01:MIS ############"

# --- A. 先取得 session cookie(基本市況頁)---
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$null = Get-Raw 'Q01_A_index_page' 'https://mis.twse.com.tw/stock/index.jsp' $session
$cookieNames = ($session.Cookies.GetCookies('https://mis.twse.com.tw') | ForEach-Object { $_.Name }) -join ','
Log ("取得的 cookie 名稱: [" + $cookieNames + "]")

# --- B. 有 session:上市單檔 ---
$t = Get-Raw 'Q01_B_tse_0050_with_session' (Mis-Url 'tse_0050.tw') $session
Show-Fields $t
Preview $t 1500

# --- C. 沒有 session(全新連線,不帶 cookie):是否也能查 ---
$t = Get-Raw 'Q01_C_tse_0050_no_session' (Mis-Url 'tse_0050.tw') $null
Show-Fields $t

# --- D. 上市 + 上櫃 + ETF 混合(確認 otc_ 前綴可用同一端點)---
$t = Get-Raw 'Q01_D_mixed_tse_otc' (Mis-Url 'tse_2330.tw|otc_6488.tw|tse_0050.tw|tse_00878.tw') $session
Show-Fields $t
Preview $t 2500

# --- E. 單次可帶幾檔:依序測試 20、30、40、60、80 檔 ---
$symbols = '1101,1102,1216,1301,1303,1326,1402,1476,1590,1605,1795,2002,2105,2201,2301,2303,2308,2317,2324,2327,2330,2345,2352,2357,2379,2382,2395,2408,2409,2412,2454,2474,2603,2609,2615,2801,2880,2881,2882,2883,2884,2885,2886,2887,2890,2891,2892,2912,3008,3034,3037,3045,3231,3481,3711,4904,5871,5876,5880,6505,9904,9910,0050,0056,00878,00919,00929,00940,2105,2492,2618,2610,3017,3293,3443,3661,6669,6446'.Split(',') | Select-Object -Unique
Log ""
Log "=== Q01_E 批次大小測試(可用代號數: $($symbols.Count))==="
foreach ($n in @(20, 30, 40, 60, 80)) {
  if ($n -gt $symbols.Count) { continue }
  $exCh = ($symbols | Select-Object -First $n | ForEach-Object { 'tse_' + $_ + '.tw' }) -join '|'
  $sw = [Diagnostics.Stopwatch]::StartNew()
  try {
    $r = Invoke-WebRequest -Uri (Mis-Url $exCh) -UseBasicParsing -WebSession $session -UserAgent $ua -TimeoutSec 20
    $sw.Stop()
    $j = $r.Content | ConvertFrom-Json
    Log ("要求 {0} 檔 -> HTTP {1}, msgArray {2} 筆, {3} bytes, {4} ms" -f $n, $r.StatusCode, @($j.msgArray).Count, $r.RawContentStream.Length, $sw.ElapsedMilliseconds)
  } catch {
    $sw.Stop()
    $code = ''
    if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    Log ("要求 {0} 檔 -> 失敗 HTTP {1} {2}" -f $n, $code, $_.Exception.Message)
  }
  Start-Sleep -Seconds 2
}

# --- F. 請求限制:連續 10 次(每次間隔 300ms),觀察是否被擋 ---
Log ""
Log "=== Q01_F 速率測試(連續 10 次,間隔 300ms)==="
for ($i = 1; $i -le 10; $i++) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  try {
    $r = Invoke-WebRequest -Uri (Mis-Url 'tse_2330.tw') -UseBasicParsing -WebSession $session -UserAgent $ua -TimeoutSec 20
    $sw.Stop()
    $j = $r.Content | ConvertFrom-Json
    Log ("第 {0} 次 -> HTTP {1}, msgArray {2} 筆, {3} ms" -f $i, $r.StatusCode, @($j.msgArray).Count, $sw.ElapsedMilliseconds)
  } catch {
    $sw.Stop()
    $code = ''
    if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    Log ("第 {0} 次 -> 失敗 HTTP {1} {2}" -f $i, $code, $_.Exception.Message)
  }
  Start-Sleep -Milliseconds 300
}

# --- G. 不存在的代號 ---
$t = Get-Raw 'Q01_G_unknown_symbol' (Mis-Url 'tse_9999999.tw') $session
Show-Fields $t
Preview $t 600

Log ""
Log "############ Q-04:除權息日昨收 ############"
if ($ExDivSymbol -ne "") {
  $t = Get-Raw 'Q04_mis' (Mis-Url ('tse_' + $ExDivSymbol + '.tw')) $session
  Show-Fields $t
  $ym = (Get-Date).ToString('yyyyMMdd')
  $t = Get-Raw 'Q04_stock_day' ('https://www.twse.com.tw/rwd/zh/afterTrading/STOCK_DAY?date=' + $ym + '&stockNo=' + $ExDivSymbol + '&response=json') $null
  if ($t) {
    try {
      $j = $t | ConvertFrom-Json
      Log ('STOCK_DAY 欄位: ' + ($j.fields -join ' | '))
      $rows = @($j.data)
      $last = $rows | Select-Object -Last 3
      foreach ($row in $last) { Log ("  " + ($row -join ' | ')) }
      Log "請比較:MIS 的 y(昨收)是否等於上列『前一個交易日』的收盤價?若不同,代表 y 是除權息調整後的參考價。"
    } catch { Log '  (STOCK_DAY 無法解析)' }
  }
} else {
  Log "(未指定 -ExDivSymbol,略過。除權息日請以 -ExDivSymbol <代號> 重跑;今日除權息名單可看證交所『除權除息預告表』)"
  $t = Get-Raw 'Q04_exdiv_list' 'https://www.twse.com.tw/rwd/zh/exRight/TWT48U?response=json' $null
  Preview $t 1200
}

Log ""
Log "############ Q-03:休市日 ############"
$year = (Get-Date).Year
$t = Get-Raw 'Q03_holiday_rwd' ('https://www.twse.com.tw/rwd/zh/holidaySchedule/holidaySchedule?date=' + $year + '&response=json') $null
Preview $t 1500
$t = Get-Raw 'Q03_holiday_openapi' 'https://openapi.twse.com.tw/v1/holidaySchedule/holidaySchedule' $null
Preview $t 1500

Log ""
Log "############ Q-02:標的主檔(上市、上櫃的股票與 ETF)############"
foreach ($mode in @(@{ n = 'Q02_isin_listed'; m = 2 }, @{ n = 'Q02_isin_otc'; m = 4 })) {
  $url = 'https://isin.twse.com.tw/isin/C_public.jsp?strMode=' + $mode.m
  Log ""
  Log ("=== " + $mode.n + " ===")
  Log "URL: $url"
  try {
    $r = Invoke-WebRequest -Uri $url -UseBasicParsing -UserAgent $ua -TimeoutSec 30
    $bytes = $r.RawContentStream.ToArray()
    [IO.File]::WriteAllBytes((Join-Path $out ($mode.n + '.bin')), $bytes)
    $text = $null
    try { $text = [Text.Encoding]::GetEncoding(950).GetString($bytes) } catch { $text = [Text.Encoding]::UTF8.GetString($bytes) }
    Log ("狀態: {0}  大小: {1} bytes" -f $r.StatusCode, $bytes.Length)
    Log ("CFICode ESVUFR(疑似股票)出現次數: " + ([regex]::Matches($text, 'ESVUFR')).Count)
    Log ("CFICode CEOGEU(疑似 ETF)出現次數: " + ([regex]::Matches($text, 'CEOGEU')).Count)
    foreach ($sym in @('0050', '0056', '00878', '2330', '6488', '6669')) {
      $m = [regex]::Match($text, '<tr>[^\r\n]*?>' + $sym + '[^\r\n]*')
      if ($m.Success) {
        $row = $m.Value -replace '<[^>]+>', ' | ' -replace '\s+', ' '
        Log ("  " + $sym + ": " + $row)
      }
    }
  } catch {
    Log ("失敗: " + $_.Exception.Message)
  }
}
$t = Get-Raw 'Q02_openapi_listed_companies' 'https://openapi.twse.com.tw/v1/opendata/t187ap03_L' $null
Preview $t 600
$t = Get-Raw 'Q02_openapi_stock_day_all' 'https://openapi.twse.com.tw/v1/exchangeReport/STOCK_DAY_ALL' $null
Preview $t 600
$t = Get-Raw 'Q02_tpex_quotes' 'https://www.tpex.org.tw/openapi/v1/tpex_mainboard_quotes' $null
Preview $t 600

Log ""
Log ('完成。請把 ' + $summary + ' 的內容貼給我;若 MIS 回應(Q01_B、Q01_D)內容很長,另外貼 verify-out 內該檔案的前 1500 字即可。')
