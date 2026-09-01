<#
    ExtDataJob 部署（2026-09-01）

    造映像 → 推 Docker Hub → 建立/更新 Container Apps Job（排程）。可重跑。

    **不需要安裝 Docker**：用 .NET SDK 內建的容器發佈（csproj 裡的 EnableSdkContainerSupport）。
    推送憑證讀 ~/.docker/config.json——沒有 Docker 也可以只放這個檔，本腳本會在
    環境變數 DOCKERHUB_TOKEN 有值時自動幫你生成（token 只寫進檔案，不會印出來）。

    為什麼不像 BotRateJob 那樣走 GitHub Actions：
      本 job 用相對路徑引用 ERPV2（Azure DevOps 私有 repo）的四個共用專案，
      CI 要建置就得先有那個 repo 的存取權杖。改成在開發機建置推送，少一份長期憑證。
      將來要接 CI，就在 workflow 裡加一個 Azure DevOps PAT 的 secret 並 clone ERPV2。

    用法：
      $env:DOCKERHUB_TOKEN = "<Docker Hub 存取權杖，需 Read & Write>"
      .\deploy.ps1                # 造映像＋推送＋建立/更新 job
      .\deploy.ps1 -SkipPush      # 只更新 job 設定，不重造映像
      .\deploy.ps1 -RunNow        # 部署完立刻手動觸發一次
#>
[CmdletBinding()]
param(
    [switch]$SkipPush,
    [switch]$RunNow,
    [string]$DockerHubUser = 'momo16542',
    [string]$ResourceGroup = 'Yanyue',
    [string]$Environment   = 'yanyue-aca-env',
    [string]$JobName       = 'extdata-job',
    # 每天台北 09:00（cron 為 UTC）。行情是每日報價、且沒有歷史可回補，
    # 少跑一天就永遠少一天，所以排每日；公會資料每月才更新一次，
    # 本期已抓過會回報 Skipped 走既有檔案，不會重複下載。
    [string]$Cron          = '0 1 * * *'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$identityName = 'extdata-job-identity'
$identityClientId = 'a91612f0-df85-4335-8fee-2de1bc4b7d3c'
$identityResourceId = "/subscriptions/0d48a058-d7c8-494e-9afd-0e29273131c5/resourcegroups/$ResourceGroup/providers/Microsoft.ManagedIdentity/userAssignedIdentities/$identityName"

# 映像標籤用 git sha，才知道線上跑的是哪一版（同 botrate-job 的慣例）。
# **兩個 repo 都要帶**：映像內容同時取決於 TSAPI（job 本體）與 ERPV2（四個共用專案），
# 只用 TSAPI 的 sha 的話，改了 ERPV2 而 TSAPI 沒動就會推出同名但內容不同的映像。
$shaJob  = (git rev-parse --short=12 HEAD).Trim()
$shaCore = (git -C "$PSScriptRoot\..\..\..\ERPV2" rev-parse --short=12 HEAD).Trim()
$sha = "$shaJob-$shaCore"
$image = "docker.io/$DockerHubUser/extdatajob:$sha"

if (-not $SkipPush) {
    # ── Docker Hub 憑證：SDK 容器發佈讀 ~/.docker/config.json ──────────────
    $cfgDir = Join-Path $env:USERPROFILE '.docker'
    $cfgFile = Join-Path $cfgDir 'config.json'
    if ($env:DOCKERHUB_TOKEN) {
        if (-not (Test-Path $cfgDir)) { New-Item -ItemType Directory -Path $cfgDir | Out-Null }
        $auth = [Convert]::ToBase64String(
            [Text.Encoding]::UTF8.GetBytes("${DockerHubUser}:$($env:DOCKERHUB_TOKEN)"))
        @{ auths = @{ 'https://index.docker.io/v1/' = @{ auth = $auth } } } |
            ConvertTo-Json -Depth 5 | Set-Content $cfgFile -Encoding utf8
        Write-Host "已寫入 Docker Hub 憑證（$cfgFile）" -ForegroundColor DarkGray
    }
    elseif (-not (Test-Path $cfgFile)) {
        throw "找不到 $cfgFile，且環境變數 DOCKERHUB_TOKEN 未設定——無法推送映像。"
    }

    Write-Host "造映像並推送 $image ..." -ForegroundColor Cyan
    dotnet publish -c Release -t:PublishContainer -p:ContainerImageTag=$sha -nologo -v:m
    if ($LASTEXITCODE -ne 0) { throw "映像建置／推送失敗" }
}

# ── Container Apps Job：不存在就建立，存在就更新 ──────────────────────────
# PowerShell 5.1 的兩個地雷，都會讓 az 根本沒被呼叫或誤判失敗：
#   1. 原生命令的 stderr 會包成 ErrorRecord，配上 ErrorActionPreference='Stop'
#      就算 az 回 exit 0 也會中止（containerapp 擴充每次都印一行 WARNING）。
#   2. 參數若逐個傳給函式，`-o` 會被當成 -OutVariable/-OutBuffer 的縮寫而報 ambiguous。
# 所以：自己看 $LASTEXITCODE，且參數整包當**陣列**傳，PowerShell 不去解析裡面的內容。
$ErrorActionPreference = 'Continue'
function Invoke-Az([string[]]$AzArgs) {
    $out = & az @AzArgs 2>&1
    $clean = $out | Where-Object { "$_" -notmatch '^WARNING' }
    if ($LASTEXITCODE -ne 0) {
        $clean | ForEach-Object { Write-Host $_ }
        throw "az $($AzArgs -join ' ') 失敗（exit $LASTEXITCODE）"
    }
    $clean
}

$envVars = @(
    "CENTRAL_SQL_SERVER=yyerp.database.windows.net"
    "CENTRAL_SQL_DATABASE=YanyueCentral"
    # 使用者指派的受控識別要靠這個變數指定是哪一個，缺了就取不到 SQL 權杖
    "AZURE_CLIENT_ID=$identityClientId"
    # 容器檔案系統是暫時的，原始檔落地在這裡即可（下一次執行會重抓或走 Skipped）
    "EXTDATA_ROOT=/tmp/extdata"
    "KEY_VAULT_URL=https://yanyuekeyvault.vault.azure.net/"
    "TELEGRAM_CHAT_ID=-1003784964525"
)

& az containerapp job show -g $ResourceGroup -n $JobName -o none 2>&1 | Out-Null
$found = ($LASTEXITCODE -eq 0)

if (-not $found) {
    Write-Host "建立 job $JobName（cron $Cron，UTC）..." -ForegroundColor Cyan
    Invoke-Az (@(
        'containerapp', 'job', 'create',
        '-g', $ResourceGroup, '-n', $JobName, '--environment', $Environment,
        '--trigger-type', 'Schedule', '--cron-expression', $Cron,
        '--replica-timeout', '1800', '--replica-retry-limit', '1',
        '--parallelism', '1', '--replica-completion-count', '1',
        '--image', $image, '--cpu', '0.5', '--memory', '1Gi',
        '--mi-user-assigned', $identityResourceId,
        '--env-vars') + $envVars + @('-o', 'none'))
}
else {
    Write-Host "更新 job $JobName ..." -ForegroundColor Cyan
    Invoke-Az (@(
        'containerapp', 'job', 'update',
        '-g', $ResourceGroup, '-n', $JobName,
        '--image', $image, '--cron-expression', $Cron,
        '--replica-timeout', '1800',
        '--set-env-vars') + $envVars + @('-o', 'none'))
}

Write-Host "`n目前設定：" -ForegroundColor Green
Invoke-Az @(
    'containerapp', 'job', 'show', '-g', $ResourceGroup, '-n', $JobName,
    '--query', '{name:name, cron:properties.configuration.scheduleTriggerConfig.cronExpression, timeout:properties.configuration.replicaTimeout, image:properties.template.containers[0].image}',
    '-o', 'yaml')

if ($RunNow) {
    Write-Host "`n手動觸發一次..." -ForegroundColor Cyan
    Invoke-Az @('containerapp', 'job', 'start', '-g', $ResourceGroup, '-n', $JobName, '-o', 'none')
    Write-Host "已送出。看執行狀況：" -ForegroundColor Green
    Write-Host "  az containerapp job execution list -g $ResourceGroup -n $JobName -o table"
}
