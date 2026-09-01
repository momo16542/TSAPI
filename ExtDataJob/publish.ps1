# 容器建置前的發佈步驟（使用者裁決 A2，2026-09-01）。
#
# 本 job 用相對路徑引用另一個 repo（ERPV2）的共用專案，Docker build context 只有
# TSAPI 這個資料夾、拿不到那些原始碼，所以改成「在建置機 publish、映像只 COPY 產出」。
#
# 用法： .\publish.ps1  然後  docker build -t extdatajob .
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (Test-Path publish) { Remove-Item publish -Recurse -Force }

# framework-dependent（映像已含 .NET runtime），linux-x64 對應容器平台
dotnet publish -c Release -r linux-x64 --self-contained false -o publish

$dll = Join-Path $PSScriptRoot 'publish\ExtDataJob.dll'
if (-not (Test-Path $dll)) { throw "發佈失敗：找不到 $dll" }

$count = (Get-ChildItem publish -Recurse -File).Count
Write-Host "發佈完成：publish\ 共 $count 個檔案。接著執行 docker build -t extdatajob ." -ForegroundColor Green
