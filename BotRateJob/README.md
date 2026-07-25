# BotRateJob — 台銀牌告匯率抓取（Container Apps Job）

## 為什麼有這個專案

台灣銀行在 CDN 層加了需要執行 JavaScript 的防爬蟲挑戰。任何不跑 JS 的 HTTP 客戶端
（包含原本 Function 裡的 `HttpClient`、`pandas.read_html`）打 `rate.bot.com.tw` 都只會拿到
**HTTP 200 + 一頁 HTML 挑戰頁**，不是 CSV。加 User-Agent、Referer、cookie 容器都無效。

因此改用 Playwright 開真實 Chromium，等挑戰自行通過後再取資料，並以 Container Apps Job
每天排程執行一次。

## 運作方式

1. Playwright 開啟 `https://rate.bot.com.tw/xrt?Lang=zh-TW`
2. 等 `table[title='牌告匯率']` 出現，代表挑戰已通過（實測約 50 秒）
3. 讀「牌價最新掛牌時間」作為資料日期
4. **主要路徑**：沿用已通過挑戰的 session，在頁面內 `fetch('/xrt/flcsv/0/day')` 取官方 CSV
5. **備援路徑**：CSV 失敗時改解析畫面上的表格（`FORCE_TABLE_PARSE=1` 可強制走這條）
6. 寫入 Cosmos，發 Telegram 通知

寫入的文件欄位（`Date` / `Currency` / `SpotRateBuying` / `SpotRateSelling` / `id`）與原本
Function 寫的完全一致，下游查詢不需要改。

**與原本行為的差異**：`id` 改成 `yyyyMMdd-幣別`（原本是隨機 GUID），並改用 upsert。
job 重跑或重試會覆蓋當天同幣別的資料，不會像以前一樣長出重複文件。

## 環境變數

| 變數 | 必要 | 預設 | 說明 |
|---|---|---|---|
| `COSMOS_CONNECTION_STRING` | 是 | — | Cosmos 連線字串，建議設為 secret |
| `COSMOS_DATABASE` | 否 | `TSAPI` | 資料庫名稱 |
| `COSMOS_CONTAINER` | 否 | `BankTaiwanSpotRate` | 容器名稱 |
| `KEY_VAULT_URL` | 否 | — | 未設定則略過 Telegram 通知 |
| `TELEGRAM_CHAT_ID` | 否 | `-5030274644` | 與 Function 相同 |
| `TELEGRAM_THREAD_ID` | 否 | `2` | 與 Function 相同 |
| `DRY_RUN` | 否 | — | `1` = 只抓取並印出，不寫 Cosmos、不發通知 |
| `FORCE_TABLE_PARSE` | 否 | — | `1` = 跳過 CSV 直接用表格解析 |

## 本機測試

```powershell
dotnet build BotRateJob\BotRateJob.csproj
# 首次需安裝瀏覽器
.\BotRateJob\bin\Debug\net8.0\playwright.ps1 install chromium

$env:DRY_RUN = "1"
dotnet run --project BotRateJob\BotRateJob.csproj
```

## 首次部署

以下變數依實際情況調整。

```bash
RG=Yanyue
LOCATION=eastasia
ENV_NAME=yanyue-aca-env
JOB=botrate-job
IDENTITY=botrate-job-identity
IMAGE=docker.io/<你的 Docker Hub 帳號>/botratejob:latest

# 1. 映像放在 Docker Hub 公開儲存庫，由 GitHub Actions 建置推送
#    （見「持續部署」一節）。映像不含任何機密，公開無妨。
#    Container Apps 匿名拉取即可，不需要註冊表憑證。

# 2. Container Apps 環境
az containerapp env create -g $RG -n $ENV_NAME --location $LOCATION

# 3. 建立使用者指派身分並授權讀取 Key Vault
az identity create -g $RG -n $IDENTITY --location $LOCATION
IDENTITY_ID=$(az identity show -g $RG -n $IDENTITY --query id -o tsv)
PRINCIPAL=$(az identity show -g $RG -n $IDENTITY --query principalId -o tsv)
CLIENT_ID=$(az identity show -g $RG -n $IDENTITY --query clientId -o tsv)

# YanyueKeyVault 使用 RBAC 授權模式，不是存取原則
az role assignment create --assignee-object-id $PRINCIPAL \
  --assignee-principal-type ServicePrincipal --role "Key Vault Secrets User" \
  --scope $(az keyvault show -g $RG -n YanyueKeyVault --query id -o tsv)

# 4. 建立排程 Job
#    cron 是 UTC：08:00 UTC = 台北 16:00
#    台銀營業到 15:30，16:00 抓到的是當日營業時間的最後一筆牌價（即收盤價）。
#    若晚於此時間執行，網頁會換成「非營業時間牌告匯率」，是另一組數字。
#    AZURE_CLIENT_ID 必須設定，DefaultAzureCredential 才知道要用哪個使用者指派身分
az containerapp job create \
  -g $RG -n $JOB --environment $ENV_NAME \
  --trigger-type Schedule \
  --cron-expression "0 8 * * *" \
  --image $IMAGE \
  --cpu 1.0 --memory 2.0Gi \
  --replica-timeout 600 \
  --replica-retry-limit 1 \
  --mi-user-assigned $IDENTITY_ID \
  --env-vars COSMOS_DATABASE=TSAPI COSMOS_CONTAINER=BankTaiwanSpotRate \
             KEY_VAULT_URL=https://yanyuekeyvault.vault.azure.net/ \
             AZURE_CLIENT_ID=$CLIENT_ID

# 5. Cosmos 連線字串設為 secret
CONN=$(az cosmosdb keys list -g $RG -n yycosmos --type connection-strings \
  --query "connectionStrings[?description=='Primary SQL Connection String'].connectionString | [0]" -o tsv)
az containerapp job secret set -g $RG -n $JOB --secrets "cosmos-conn=$CONN"
az containerapp job update -g $RG -n $JOB \
  --set-env-vars COSMOS_CONNECTION_STRING=secretref:cosmos-conn

# 6. 手動跑一次驗證
az containerapp job start -g $RG -n $JOB
az containerapp job execution list -g $RG -n $JOB -o table
```

## 持續部署

`.github/workflows/BotRateJob.yml` 會在 `BotRateJob/**` 有變動時建置映像、推上 Docker Hub，
再更新 job 的映像標籤。需要三個 repo secret：

| Secret | 用途 |
|---|---|
| `DOCKERHUB_USERNAME` | Docker Hub 帳號，同時決定映像名稱 |
| `DOCKERHUB_TOKEN` | Docker Hub 個人存取權杖（Read & Write） |
| `AZURE_CREDENTIALS` | 服務主體 JSON，範圍限 Yanyue 資源群組 |

設定 secret 時注意：`gh secret set` 需要互動式終端機，
在沒有 TTY 的環境（例如工具內執行）會讀到 EOF 並把值設成空字串，
secret 看起來有建立但實際是空的。用 GitHub 網頁或一般終端機設定。

Docker Hub 上的 `botratejob` 儲存庫必須是 **Public**，否則 Container Apps 無法匿名拉取。

## 成本

**每月約 NT$0。**

一天執行一次、每次約 120 秒（含拉映像與通過挑戰的 50 秒），1 vCPU / 2GiB：
每月約 3,650 vCPU-秒、7,300 GiB-秒，約為 Container Apps 免費額度
（180,000 vCPU-秒、360,000 GiB-秒）的 2%。

映像放在 Docker Hub 公開儲存庫，沒有註冊表費用。
Container Apps 環境會附帶一個 Log Analytics 工作區，每次執行只產生約十行日誌，
遠低於每月 5 GB 的免費擷取量。

## 注意事項

- 映像基於 `mcr.microsoft.com/playwright/dotnet:v1.61.0-noble`，
  **升級 `Microsoft.Playwright` 套件時必須同步改 Dockerfile 的映像標籤**，版本不一致會啟動失敗。
- 應用程式以 self-contained 發佈，因此不受基底映像 .NET 版本影響。
- PHP / IDR / KRW / VND / MYR 台銀沒有即期匯率，CSV 給 `0.00000`，
  程式照原本 Function 的行為寫入 0（表格路徑的 `-` 也一併轉成 0）。
- 挑戰通過需要約 50 秒，`--replica-timeout` 不要設太短。
- 容器 `BankTaiwanSpotRate` 的分割索引鍵是 `/BankTaiwanSpotRate`，但文件本身並沒有這個屬性
  （原本 Function 的輸出繫結也是如此），所有文件因此落在 undefined 分割區。
  程式刻意不明確指定 PartitionKey，由 SDK 從文件推斷，維持與既有資料相同的行為。
