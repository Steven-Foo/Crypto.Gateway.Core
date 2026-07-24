<#
.SYNOPSIS
  Brings the dev database up to date on a SQL Server at -SqlHost (a native install or the Docker Compose
  container), so an end-to-end run works and DBeaver can connect to it. Mirrors the EC2 layout.

.DESCRIPTION
  Point -SqlHost at any reachable SQL Server (default localhost,1433). Waits for it to accept connections
  via sqlcmd, then applies every module's EF Core migrations (each module owns its own DbContext + schema);
  EF creates the CryptoPaymentEngine database on the first update. Mongo collections come from db/mongo;
  Redis needs no schema.

  DEV/STAGING ONLY. The SA password default is the fixed dev secret — never production.

.EXAMPLE
  # Native SQL Server (e.g. localhost or EC2):
  ./tools/dev/Setup-LocalEnv.ps1
  ./tools/dev/Setup-LocalEnv.ps1 -SqlHost "10.0.0.5,1433" -SaPassword "<staging-sa-pwd>"
#>
[CmdletBinding()]
param(
    [string]$SaPassword = "Cpe_Dev_Passw0rd!",
    [string]$SqlHost    = "localhost,1433"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $repoRoot

$connection = "Server=$SqlHost;Database=CryptoPaymentEngine;User Id=sa;Password=$SaPassword;TrustServerCertificate=True;Encrypt=False"
$host_ = "src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway"

# module context -> Infrastructure project, applied in build order (later modules reference earlier
# ones by opaque Guid only, but this order matches how they were built).
$modules = [ordered]@{
    "BlockchainDbContext"     = "src/Gateway.Core/Blockchain/Infrastructure"
    "MerchantDbContext"       = "src/Gateway.Core/Merchant/Infrastructure"
    "KeyManagementDbContext"  = "src/Gateway.Core/KeyManagement/Infrastructure"
    "WalletDbContext"         = "src/Gateway.Core/AssetManagement/Wallet/Infrastructure"
    "LedgerDbContext"         = "src/Gateway.Core/Financial/Ledger/Infrastructure"
    "DepositDbContext"        = "src/Gateway.Core/PaymentProcessing/Deposit/Infrastructure"
    "WithdrawalDbContext"     = "src/Gateway.Core/PaymentProcessing/Withdrawal/Infrastructure"
    "PaymentIntentDbContext"  = "src/Gateway.Core/PaymentProcessing/PaymentIntent/Infrastructure"
    "EnergyDbContext"         = "src/Gateway.Core/AssetManagement/Energy/Infrastructure"
    "IdentityDbContext"       = "src/Gateway.Core/Platform/Identity/Infrastructure"
}

Write-Host "Waiting for SQL Server on $SqlHost ..." -ForegroundColor Cyan
$ready = $false
foreach ($i in 1..30) {
    try {
        sqlcmd -S $SqlHost -U sa -P $SaPassword -C -Q "SELECT 1" -b 1>$null 2>$null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    } catch { }
    Start-Sleep -Seconds 3
}
if (-not $ready) {
    throw "SQL Server on $SqlHost did not accept connections. Is it running (native install, or 'docker compose up -d'), and is the sa password correct?"
}
Write-Host "SQL Server is ready." -ForegroundColor Green

# EF creates the CryptoPaymentEngine database on the first `database update`; each module then applies
# its own migrations into its own schema + __EFMigrationsHistory.
$env:CPE_DB_CONNECTION = $connection
foreach ($ctx in $modules.Keys) {
    $proj = $modules[$ctx]
    Write-Host "Applying migrations: $ctx" -ForegroundColor Cyan
    dotnet ef database update --context $ctx -p $proj -s $host_ | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw "Migration failed for $ctx." }
}

Write-Host ""
Write-Host "Local dev database is ready." -ForegroundColor Green
Write-Host "  DBeaver:  host=localhost  port=1433  user=sa  password=$SaPassword  db=CryptoPaymentEngine  (enable 'Trust server certificate')"
Write-Host "  Next:     add your TronGrid API key to appsettings.Local.json, then run the host (see docs/dev-mainnet-deposit.md)."
