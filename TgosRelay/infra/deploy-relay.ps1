<#
.SYNOPSIS
  發佈 tgos-relay 到 GCP VM：dotnet publish（linux-x64 self-contained 單檔）→ IAP scp → VM 上跑 install.sh。
  可重跑；每次都重裝 systemd unit 與 Caddyfile，但 /etc/tgos-relay.env 只在不存在時建立（金鑰不會被覆蓋）。
#>
param(
    [string]$Project = "tgos-507813",
    [string]$Zone = "asia-east1-b",
    [string]$Vm = "tgos-relay"
)
$ErrorActionPreference = "Stop"
$env:CLOUDSDK_CORE_PROJECT = $Project
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "bin\publish-linux"

Write-Host ">> dotnet publish" -ForegroundColor Cyan
dotnet publish (Join-Path $root "TgosRelay.csproj") -c Release -r linux-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out --nologo -v:q
if ($LASTEXITCODE -ne 0) { throw "publish 失敗" }

Write-Host ">> 上傳到 VM（IAP 隧道）" -ForegroundColor Cyan
& gcloud.cmd compute ssh $Vm --zone $Zone --tunnel-through-iap --quiet --command "mkdir -p ~/relay-upload" --verbosity=error
if ($LASTEXITCODE -ne 0) { throw "ssh 失敗" }
$files = @(
    (Join-Path $out "tgos-relay"),
    (Join-Path $PSScriptRoot "install.sh"),
    (Join-Path $PSScriptRoot "tgos-relay.service"),
    (Join-Path $PSScriptRoot "tgos-relay.env.example"),
    (Join-Path $PSScriptRoot "Caddyfile")
)
& gcloud.cmd compute scp @files "${Vm}:relay-upload/" --zone $Zone --tunnel-through-iap --quiet --verbosity=error
if ($LASTEXITCODE -ne 0) { throw "scp 失敗" }

Write-Host ">> VM 上安裝／重啟" -ForegroundColor Cyan
$remote = "sed -i 's/\r$//' ~/relay-upload/install.sh ~/relay-upload/Caddyfile ~/relay-upload/tgos-relay.service ~/relay-upload/tgos-relay.env.example && bash ~/relay-upload/install.sh"
& gcloud.cmd compute ssh $Vm --zone $Zone --tunnel-through-iap --quiet --command $remote --verbosity=error
if ($LASTEXITCODE -ne 0) { throw "install.sh 失敗" }
Write-Host "完成。對外驗證：curl https://tgos-relay.yanyue.io/healthz" -ForegroundColor Green
