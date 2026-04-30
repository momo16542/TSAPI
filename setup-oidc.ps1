#Requires -Version 5.1
<#
.SYNOPSIS
  Set up Azure AD Service Principal + GitHub OIDC federated credential for YYAPI deploy.

.DESCRIPTION
  Idempotent script:
    1. Verifies current az login.
    2. Locates the YYAPI Function App (or uses the supplied -ResourceGroup).
    3. Creates the SP if missing, otherwise reuses the existing one.
    4. Ensures contributor role on the RG.
    5. Adds federated credential trusting <owner>/<repo>:ref:refs/heads/<branch> if missing.
    6. Prints the three values to paste into GitHub Secrets.

.PARAMETER FunctionAppName
  Function App name (used to auto-discover RG). Default: YYAPI.

.PARAMETER ResourceGroup
  RG name. If omitted, script tries to find it via the Function App.

.PARAMETER SpName
  Display name for the Service Principal. Default: YYAPI-github-actions.

.PARAMETER GitHubRepo
  GitHub repo in form owner/repo. Default: momo16542/TSAPI.

.PARAMETER Branch
  Branch the federated credential trusts. Default: master.

.EXAMPLE
  ./setup-oidc.ps1
  ./setup-oidc.ps1 -ResourceGroup Yanyue -Branch main
#>
[CmdletBinding()]
param(
  [string]$FunctionAppName = "YYAPI",
  [string]$ResourceGroup,
  [string]$SpName          = "YYAPI-github-actions",
  [string]$GitHubRepo      = "momo16542/TSAPI",
  [string]$Branch          = "master"
)

# az CLI writes warnings to stderr; PS would treat that as error under Stop preference.
# Use Continue and check $LASTEXITCODE explicitly after each native call.
$ErrorActionPreference = "Continue"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  OK $msg"  -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "  !! $msg"  -ForegroundColor Yellow }

# Run an az command, swallow stderr, throw if exit code is non-zero, return parsed JSON.
function Invoke-AzJson {
  $argList = $args
  $stdout = & az @argList 2>$null
  if ($LASTEXITCODE -ne 0) {
    throw "az command failed (exit $LASTEXITCODE): az $($argList -join ' ')"
  }
  if (-not $stdout) { return $null }
  return ($stdout | Out-String | ConvertFrom-Json)
}

# Run an az command, swallow stderr, throw if non-zero. No JSON parse.
function Invoke-Az {
  $argList = $args
  & az @argList 2>$null | Out-Null
  if ($LASTEXITCODE -ne 0) {
    throw "az command failed (exit $LASTEXITCODE): az $($argList -join ' ')"
  }
}

# ---- 1. Verify az login ----
Write-Step "Checking Azure CLI login"
$acct = Invoke-AzJson account show
if (-not $acct) { throw "az not logged in. Run: az login" }
$SUB_ID    = $acct.id
$TENANT_ID = $acct.tenantId
Write-Ok "Subscription : $($acct.name) ($SUB_ID)"
Write-Ok "Tenant       : $TENANT_ID"
Write-Ok "User         : $($acct.user.name)"

# ---- 2. Locate Function App / RG ----
if (-not $ResourceGroup) {
  Write-Step "Looking up Function App '$FunctionAppName' to discover RG"
  $found = Invoke-AzJson functionapp list --query "[?name=='$FunctionAppName'].{name:name,rg:resourceGroup}" -o json
  $foundArr = @($found)
  if ($foundArr.Count -eq 0) {
    throw "Function App '$FunctionAppName' not found in subscription '$SUB_ID'. Use -ResourceGroup, or switch subscription with: az account set --subscription <id>"
  }
  if ($foundArr.Count -gt 1) {
    throw "More than one Function App named '$FunctionAppName' found. Specify -ResourceGroup."
  }
  $ResourceGroup = $foundArr[0].rg
}
Write-Ok "Resource Group : $ResourceGroup"
$scope = "/subscriptions/$SUB_ID/resourceGroups/$ResourceGroup"

# ---- 3. Create or reuse SP ----
Write-Step "Ensuring Service Principal '$SpName' exists"
$sps = @(Invoke-AzJson ad sp list --display-name $SpName -o json)
if ($sps.Count -gt 1) {
  Write-Warn2 "Multiple SPs share the name '$SpName':"
  $sps | ForEach-Object { Write-Host ("    appId={0}" -f $_.appId) }
  throw "Resolve the duplicates first (delete or use -SpName <unique>)."
}
if ($sps.Count -eq 1) {
  $APP_ID = $sps[0].appId
  Write-Ok "Reusing existing SP. appId = $APP_ID"

  Write-Step "Ensuring 'Contributor' role on $scope"
  $stdout = & az role assignment list --assignee $APP_ID --scope $scope `
    --query "[?roleDefinitionName=='Contributor'] | length(@)" -o tsv 2>$null
  if ($LASTEXITCODE -ne 0) { throw "az role assignment list failed." }
  $hasRole = [int]($stdout | Out-String).Trim()
  if ($hasRole -gt 0) {
    Write-Ok "Already has Contributor role."
  } else {
    Invoke-Az role assignment create --assignee $APP_ID --role contributor --scope $scope
    Write-Ok "Granted Contributor role."
  }
} else {
  Write-Step "Creating new SP scoped to $scope"
  $newSp = Invoke-AzJson ad sp create-for-rbac `
    --name $SpName `
    --role contributor `
    --scopes $scope
  $APP_ID = $newSp.appId
  Write-Ok "Created SP. appId = $APP_ID"
}

# ---- 4. Federated credential ----
$fedName = "github-$Branch"
$subject = "repo:${GitHubRepo}:ref:refs/heads/$Branch"

Write-Step "Ensuring federated credential '$fedName' on the SP"
$existing = @(Invoke-AzJson ad app federated-credential list --id $APP_ID -o json)
$match = $existing | Where-Object { $_.subject -eq $subject -and $_.issuer -eq "https://token.actions.githubusercontent.com" }
if ($match) {
  Write-Ok "Federated credential already present (name=$($match.name))."
} else {
  $tmpFile = New-TemporaryFile
  try {
    $json = @"
{
  "name": "$fedName",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "$subject",
  "audiences": ["api://AzureADTokenExchange"]
}
"@
    [System.IO.File]::WriteAllText($tmpFile.FullName, $json)
    Invoke-Az ad app federated-credential create --id $APP_ID --parameters "@$($tmpFile.FullName)"
    Write-Ok "Federated credential added (subject=$subject)."
  } finally {
    Remove-Item $tmpFile -ErrorAction SilentlyContinue
  }
}

# ---- 5. Output GitHub Secrets ----
Write-Host ""
Write-Host "================================================================" -ForegroundColor Magenta
Write-Host " Paste these into GitHub Secrets (Settings -> Secrets -> Actions)" -ForegroundColor Magenta
Write-Host "  https://github.com/$GitHubRepo/settings/secrets/actions"        -ForegroundColor Magenta
Write-Host "================================================================" -ForegroundColor Magenta
Write-Host ""
Write-Host "  AZURE_CLIENT_ID       = $APP_ID"
Write-Host "  AZURE_TENANT_ID       = $TENANT_ID"
Write-Host "  AZURE_SUBSCRIPTION_ID = $SUB_ID"
Write-Host ""
Write-Host "Optional (one-shot upload via gh CLI, requires 'gh auth login'):" -ForegroundColor DarkGray
Write-Host "  gh secret set AZURE_CLIENT_ID       --body `"$APP_ID`"       --repo $GitHubRepo" -ForegroundColor DarkGray
Write-Host "  gh secret set AZURE_TENANT_ID       --body `"$TENANT_ID`"    --repo $GitHubRepo" -ForegroundColor DarkGray
Write-Host "  gh secret set AZURE_SUBSCRIPTION_ID --body `"$SUB_ID`"       --repo $GitHubRepo" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Then either re-run the failed GitHub Actions run, or push a new commit." -ForegroundColor Green
