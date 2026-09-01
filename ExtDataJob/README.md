# ExtDataJob —— 外部資料抓取 job

車輛公會汽機車銷售統計 ＋ 基本金屬行情：**中央抓一次，所有客戶再從中央取**。
客戶端 ERP 不再各自抓取、也不再各自解析。

## 相依（跨 repo）

引用 `ERPV2` 的四個共用專案（**兩個 repo 需並存於 `source\repos` 底下**）：

| 專案 | 職責 |
|---|---|
| `TsErp.ExternalData.Fetching` | 找檔連結、下載、存原始檔、擷取歷程 |
| `TsErp.ExternalData.Parsing` | 版面判讀（零相依；公會 xls 24 年間改過好幾次版） |
| `TsErp.ExternalData.Parsing.Npoi` | 讀檔（Apache-2.0，容器可自由部署，不需 Telerik） |
| `TsErp.ExternalData.Central` | 寫中央庫（SqlBulkCopy → staging → MERGE） |

## 環境變數

| 變數 | 預設 | 說明 |
|---|---|---|
| `CENTRAL_SQL_SERVER` | （必填） | 例 `yyerp.database.windows.net` |
| `CENTRAL_SQL_DATABASE` | `YanyueCentral` | |
| `EXTDATA_ROOT` | 暫存資料夾 | 原始檔落地根目錄 |
| `EXTDATA_SINCE_MONTH` | 近三個月 | 只解析此月之後的工作表。**首次灌檔設 `2002-08` 跑全量** |
| `EXTDATA_SOURCES` | 全部 | 逗號分隔，例 `TTVMA_QTA1,METALPRICE` |
| `DRY_RUN` | 否 | 只抓取解析不寫 DB |
| `FORCE_REFETCH` | 否 | 本期已抓過也重抓 |
| `KEY_VAULT_URL` | 無 | 沒設就不送 Telegram 通知，只寫 log |

**連線字串裡沒有帳密**：容器內用受控識別，開發機退回 `az login` 的身分。

## 為什麼預設只解析近三個月

2026-09-01 實測（中央庫 Azure SQL **Basic**，5 DTU）：

| | 列數 | 耗時 |
|---|---|---|
| 全量 | 285,438 | **441 秒** |
| 近三個月 | 4,332 | **16 秒** |

公會每月只新增約 1,000 列。整包重灌不只慢，還會把 28 萬列全部 UPDATE 一遍。
**全量只在首次灌檔做一次**（已於 2026-09-01 完成）。

## 本機執行

```powershell
# 先 az login（開發機沒有受控識別）
$env:CENTRAL_SQL_SERVER="yyerp.database.windows.net"
$env:EXTDATA_ROOT="C:\temps\extdatajob"
$env:EXTDATA_SINCE_MONTH="2026-05"
$env:DRY_RUN="1"          # 先不寫 DB 看看
dotnet run
```

## 容器建置與部署

```powershell
.\publish.ps1              # dotnet publish → publish\
docker build -t extdatajob .
```

映像**不在容器內建置原始碼**（跨 repo 的原始碼不在 build context 裡），
也不需要 Playwright（全部走 HttpClient）。

排程建議：公會每月 10~18 日才公布上月數據，所以**每月 18~20 日**跑一次；
金屬行情是每日報價，要即時的話另設一個每日排程只跑 `EXTDATA_SOURCES=METALPRICE`。

## 已知的坑（踩過，別重犯）

- **`Skipped` 不是「不用處理」**：代表本期已抓過、檔案就在本機，一樣要往下解析寫入。
  只看 `Success` 的話，排程第二次執行會靜靜地什麼都沒做還回報成功。
- **不要用 `DefaultAzureCredential` / `ChainedTokenCredential` 取權杖**：
  它們只在憑證回報「不可用」時才往下試；裝了 Azure Arc 代理程式的機器上，
  受控識別是「可用但失敗」，會直接中斷整條鏈。見 `AzureToken.cs` 的註解。
