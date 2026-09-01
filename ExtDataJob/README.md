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

**不需要安裝 Docker**：用 .NET SDK 內建的容器發佈（csproj 的 `EnableSdkContainerSupport`）。

```powershell
$env:DOCKERHUB_TOKEN = "<Docker Hub 存取權杖，Read & Write>"
.\deploy.ps1              # 造映像 → 推 Docker Hub → 建立/更新 Container Apps Job
.\deploy.ps1 -RunNow      # 順便立刻觸發一次
.\deploy.ps1 -SkipPush    # 只改 job 設定，不重造映像
```

只想看映像造不造得出來、不推送：

```powershell
dotnet publish -c Release -t:PublishContainer -p:ContainerArchiveOutputPath=out.tar.gz
```

`Dockerfile` 保留給有 Docker 的機器，與 SDK 產出等價。

### Azure 資源（2026-09-01 已建好）

| 項目 | 值 |
|---|---|
| 資源群組 / 環境 | `Yanyue` / `yanyue-aca-env`（East Asia，與 botrate-job 共用） |
| Job | `extdata-job`，Schedule 觸發，cron `0 1 * * *`（UTC）＝台北每日 09:00 |
| 受控識別 | **使用者指派** `extdata-job-identity`，clientId `a91612f0-df85-4335-8fee-2de1bc4b7d3c` |
| 中央庫權限 | `extdata` schema 的 CRUD＋EXECUTE，三張 staging 表的 ALTER（TRUNCATE 用）。腳本：ERPV2 `sql/extdata-central-grant-job-identity-20260901.sql` |
| Key Vault | `yanyuekeyvault`（RBAC 模式），已指派 **Key Vault Secrets User** 給上述識別，供 Telegram 通知取 bot token |

**為什麼排每日而不是每月**：金屬行情是每日報價，**而且來源沒有歷史可回補**
（`cnyes.com/futures/basicmetal.aspx` 只有各交易所最新一個交易日的快照，沒有日期參數），
少跑一天就永遠少一天。公會資料每月才更新，本期已抓過會回報 `Skipped` 走本機既有檔案，
不會重複下載，所以一起排每日沒有代價。

**為什麼不走 GitHub Actions**（BotRateJob 是走的）：本 job 用相對路徑引用 ERPV2
（Azure DevOps 私有 repo）的四個共用專案，CI 要建置就得長期保管那個 repo 的存取權杖。
現階段在開發機建置推送，少一份憑證。要接 CI 就在 workflow 加一個 Azure DevOps PAT
的 secret 並先 clone ERPV2 到相對位置。

## 已知的坑（踩過，別重犯）

- **`Skipped` 不是「不用處理」**：代表本期已抓過、檔案就在本機，一樣要往下解析寫入。
  只看 `Success` 的話，排程第二次執行會靜靜地什麼都沒做還回報成功。
- **不要用 `DefaultAzureCredential` / `ChainedTokenCredential` 取權杖**：
  它們只在憑證回報「不可用」時才往下試；裝了 Azure Arc 代理程式的機器上，
  受控識別是「可用但失敗」，會直接中斷整條鏈。見 `AzureToken.cs` 的註解。
- **`ManagedIdentityCredential()` 參數空的只認系統指派**：本 job 用的是**使用者指派**識別
  （沿用 botrate-job 的做法），必須把 `AZURE_CLIENT_ID` 傳進去。開發機退回 az CLI 所以測不出來，
  只有部署上去才會炸在「取不到 Azure SQL 存取權杖」。見 `AzureToken.cs`。
