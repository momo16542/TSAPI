<#
.SYNOPSIS
  建立 TGOS 轉發器的 GCP 基礎設施（2026-09-06）：靜態 IP、e2-micro VM、防火牆。
  只建不裝——程式部署走 deploy-relay.ps1。可重跑：每步先查存在再建。

.NOTES
  前提：gcloud auth login 已完成；專案已綁帳單帳戶。
  用法：.\gcp-relay.ps1            （全部跑）
        .\gcp-relay.ps1 -WhatIf    （只印指令不執行）
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Project = "tgos-507813",
    [string]$Region  = "asia-east1",      # 彰化：TGOS 只接受中華民國 IP（使用規定第七條第 2 款）
    [string]$Zone    = "asia-east1-b",
    [string]$Vm      = "tgos-relay",
    [string]$Ip      = "tgos-relay-ip",
    [string]$Machine = "e2-micro",
    [string]$DnsHost = "tgos-relay.yanyue.io"   # 只用來提醒 DNS；不在這支腳本內設定
)

$ErrorActionPreference = "Stop"
function Run([string]$desc, [string[]]$gcloudArgs) {
    if ($PSCmdlet.ShouldProcess($desc, "gcloud $($gcloudArgs -join ' ')")) {
        Write-Host ">> $desc" -ForegroundColor Cyan
        & gcloud.cmd @gcloudArgs
        if ($LASTEXITCODE -ne 0) { throw "gcloud 失敗：$desc" }
    }
}

# 0. 專案：用環境變數指定，-WhatIf 乾跑時查詢類指令也有專案可用（不動使用者的 gcloud 全域設定）
$env:CLOUDSDK_CORE_PROJECT = $Project
Write-Host "專案：$Project"

# 1. Compute API（首次要等 1～2 分鐘）
$enabled = gcloud.cmd services list --enabled --filter="name:compute.googleapis.com" --format="value(config.name)" --verbosity=error
if (-not $enabled) { Run "啟用 Compute API" @("services","enable","compute.googleapis.com") }
else { Write-Host "Compute API 已啟用" }

# 2. 靜態外部 IP（這顆就是送 TGOS 申請要登記的 IP；VM 刪掉重建 IP 也不會變）
$ipExists = gcloud.cmd compute addresses list --filter="name=$Ip AND region:$Region" --format="value(name)" --verbosity=error
if (-not $ipExists) {
    Run "保留靜態 IP $Ip（$Region）" @("compute","addresses","create",$Ip,"--region",$Region,"--network-tier","PREMIUM")
} else { Write-Host "靜態 IP $Ip 已存在" }

# 3. 防火牆：只開 443（Caddy）與 80（Let's Encrypt HTTP-01 驗證用）。SSH 走 IAP 隧道，不對公網開 22。
$fwExists = gcloud.cmd compute firewall-rules list --filter="name=allow-tgos-relay-https" --format="value(name)" --verbosity=error
if (-not $fwExists) {
    Run "防火牆 allow-tgos-relay-https" @("compute","firewall-rules","create","allow-tgos-relay-https",
        "--direction","INGRESS","--action","ALLOW","--rules","tcp:80,tcp:443",
        "--source-ranges","0.0.0.0/0","--target-tags","tgos-relay")
} else { Write-Host "防火牆規則已存在" }
$iapExists = gcloud.cmd compute firewall-rules list --filter="name=allow-iap-ssh" --format="value(name)" --verbosity=error
if (-not $iapExists) {
    Run "防火牆 allow-iap-ssh（僅 Google IAP 來源 35.235.240.0/20）" @("compute","firewall-rules","create","allow-iap-ssh",
        "--direction","INGRESS","--action","ALLOW","--rules","tcp:22",
        "--source-ranges","35.235.240.0/20","--target-tags","tgos-relay")
} else { Write-Host "IAP SSH 規則已存在" }

# 4. VM：Debian 12、10GB 標準碟、綁靜態 IP、不給服務帳戶任何 API 權限（它只需要出站打 TGOS）
$vmExists = gcloud.cmd compute instances list --filter="name=$Vm AND zone:$Zone" --format="value(name)" --verbosity=error
if (-not $vmExists) {
    Run "建 VM $Vm（$Machine，$Zone）" @("compute","instances","create",$Vm,
        "--zone",$Zone,"--machine-type",$Machine,
        "--image-family","debian-12","--image-project","debian-cloud",
        "--boot-disk-size","10GB","--boot-disk-type","pd-standard",
        "--address",$Ip,"--tags","tgos-relay",
        "--no-service-account","--no-scopes",
        "--metadata","enable-oslogin=TRUE")
} else { Write-Host "VM $Vm 已存在" }

# 5. 印結果（-WhatIf 乾跑時 IP 還不存在，跳過）
if ($WhatIfPreference) { Write-Host "（乾跑結束，未建立任何資源）"; return }
$addr = gcloud.cmd compute addresses describe $Ip --region $Region --format="value(address)" --verbosity=error
Write-Host ""
Write-Host "==== 完成 ====" -ForegroundColor Green
Write-Host "靜態 IP（送 TGOS 申請、DNS A 記錄都填這個）：$addr"
Write-Host "下一步：到 yanyue.io 的 DNS 加 A 記錄  $DnsHost -> $addr  （Cloudflare 請設 DNS only）"
Write-Host "然後跑 deploy-relay.ps1 安裝 Caddy 與轉發器。"
