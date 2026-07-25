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
ACR=yanyueacr
ENV_NAME=yanyue-aca-env
JOB=botrate-job
IDENTITY=botrate-job-identity

# 1. Container Registry（Basic）
az acr create -g $RG -n $ACR --sku Basic --admin-enabled false

# 2. 建置並推送映像（在雲端建，本機不需要 Docker）
az acr build --registry $ACR --image botratejob:latest --file BotRateJob/Dockerfile BotRateJob

# 3. Container Apps 環境
az containerapp env create -g $RG -n $ENV_NAME --location $LOCATION

# 4. 建立使用者指派身分並授權
#    必須在建立 job 之前完成：系統指派身分要等 job 建好才存在，
#    但建立 job 當下就要能拉映像，會變成先有雞先有蛋。
az identity create -g $RG -n $IDENTITY --location $LOCATION
IDENTITY_ID=$(az identity show -g $RG -n $IDENTITY --query id -o tsv)
PRINCIPAL=$(az identity show -g $RG -n $IDENTITY --query principalId -o tsv)
CLIENT_ID=$(az identity show -g $RG -n $IDENTITY --query clientId -o tsv)

az role assignment create --assignee-object-id $PRINCIPAL \
  --assignee-principal-type ServicePrincipal --role AcrPull \
  --scope $(az acr show -g $RG -n $ACR --query id -o tsv)

# YanyueKeyVault 使用 RBAC 授權模式，不是存取原則
az role assignment create --assignee-object-id $PRINCIPAL \
  --assignee-principal-type ServicePrincipal --role "Key Vault Secrets User" \
  --scope $(az keyvault show -g $RG -n YanyueKeyVault --query id -o tsv)

# 5. 建立排程 Job
#    cron 是 UTC：02:00 UTC = 台北 10:00（台銀約 09:00 開始掛牌）
#    AZURE_CLIENT_ID 必須設定，DefaultAzureCredential 才知道要用哪個使用者指派身分
az containerapp job create \
  -g $RG -n $JOB --environment $ENV_NAME \
  --trigger-type Schedule \
  --cron-expression "0 2 * * *" \
  --image $ACR.azurecr.io/botratejob:latest \
  --cpu 1.0 --memory 2.0Gi \
  --replica-timeout 600 \
  --replica-retry-limit 1 \
  --registry-server $ACR.azurecr.io --registry-identity $IDENTITY_ID \
  --mi-user-assigned $IDENTITY_ID \
  --env-vars COSMOS_DATABASE=TSAPI COSMOS_CONTAINER=BankTaiwanSpotRate \
             KEY_VAULT_URL=https://yanyuekeyvault.vault.azure.net/ \
             AZURE_CLIENT_ID=$CLIENT_ID

# 6. Cosmos 連線字串設為 secret
CONN=$(az cosmosdb keys list -g $RG -n yycosmos --type connection-strings \
  --query "connectionStrings[?description=='Primary SQL Connection String'].connectionString | [0]" -o tsv)
az containerapp job secret set -g $RG -n $JOB --secrets "cosmos-conn=$CONN"
az containerapp job update -g $RG -n $JOB \
  --set-env-vars COSMOS_CONNECTION_STRING=secretref:cosmos-conn

# 7. 手動跑一次驗證
az containerapp job start -g $RG -n $JOB
az containerapp job execution list -g $RG -n $JOB -o table
```

## 持續部署

`.github/workflows/BotRateJob.yml` 會在 `BotRateJob/**` 有變動時自動建置並更新 job。
需要在 repo 設定 `AZURE_CREDENTIALS` secret（服務主體 JSON），
並確認 workflow 裡的 `ACR_NAME` / `JOB_NAME` / `RESOURCE_GROUP` 與實際資源相符。

## 成本

一天執行一次、每次約 1 分鐘，1 vCPU / 2GiB：
每月約 1,800 vCPU-秒、3,600 GiB-秒，遠低於 Container Apps 每月免費額度
（180,000 vCPU-秒、360,000 GiB-秒），運算費用實質為 0。
額外成本只有 ACR Basic（約 US$5/月），若改用 Docker Hub 公開儲存庫可省下。

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
