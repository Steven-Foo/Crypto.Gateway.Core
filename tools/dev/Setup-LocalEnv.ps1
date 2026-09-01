<#
.SYNOPSIS
  Brings the dev database up to date on a SQL Server at -SqlHost (a native install or the Docker Compose
  container), so an end-to-end run works and DBeaver can connect to it. Mirrors the EC2 layout.

.DESCRIPTION
  Point -SqlHost at any reachable SQL Server (default localhost,1433). Waits for it to accept connections
  via sqlcmd, then applies every module's EF Core migrations (each module owns its own DbContext + schema);
  EF creates the CryptoPaymentEngine database on the first update. Redis needs no schema.

  Mongo is NOT set up by this script and is NOT run from docker-compose: dev and local staging use the
  native Windows service on localhost:27017 (see db/README.md §2 — 8.2 works, 8.3 does not run on
  Windows 10). Apply db/mongo/00-bootstrap.js once per environment for validators + indexes; the
  collections themselves auto-create on first write, so a missed bootstrap is not fatal.

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

# `dotnet ef` needs a startup project that REFERENCES the migrations project, so the identity/audit modules
# cannot use the money host: MerchantGateway deliberately does not reference them (§4.7 — it composes only the
# modules it runs). Each context therefore names its own host.
$moneyHost   = "src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway"
$opsHost     = "src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi"
$portalHost  = "src/Api/MerchantPortalApi/CryptoPaymentEngine.Api.MerchantPortalApi"

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
    "SweepDbContext"          = "src/Gateway.Core/AssetManagement/Sweep/Infrastructure"
    "TreasuryDbContext"       = "src/Gateway.Core/AssetManagement/Treasury/Infrastructure"
    "NotificationDbContext"   = "src/Gateway.Core/Platform/Notification/Infrastructure"
    "IdentityDbContext"       = "src/Gateway.Core/Platform/Identity/Infrastructure|OPS"
    "AuditDbContext"          = "src/Gateway.Core/Platform/Audit/Infrastructure|OPS"
    "MerchantIdentityDbContext" = "src/Gateway.Core/Platform/MerchantIdentity/Infrastructure|PORTAL"
}

# DRIFT CHECK (this list has gone stale twice — each time a fresh environment booted onto a schema the code
# could not use, and the symptom was a confusing runtime error nowhere near the cause, e.g. "Invalid column
# name 'SettlementDelayDays'"). When a module gains a DbContext, add it here. To verify the list is complete:
#   grep -rhoE "class \w+DbContext" --include=*.cs src/Gateway.Core/ | sort -u
# Every context that command prints must appear above.

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
# The EF tooling's own host is net8 while these assemblies are net10, so it needs the roll-forward or every
# `dotnet ef` call fails before it reaches the database.
if (-not $env:DOTNET_ROLL_FORWARD) { $env:DOTNET_ROLL_FORWARD = "LatestMajor" }

foreach ($ctx in $modules.Keys) {
    # "<project>" or "<project>|OPS" / "<project>|PORTAL" — the suffix names the startup host to use.
    $parts = $modules[$ctx] -split '\|'
    $proj = $parts[0]
    $startup = switch ($parts[1]) {
        "OPS"    { $opsHost }
        "PORTAL" { $portalHost }
        default  { $moneyHost }
    }

    Write-Host "Applying migrations: $ctx" -ForegroundColor Cyan
    dotnet ef database update --context $ctx -p $proj -s $startup | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw "Migration failed for $ctx." }
}

# ── All three hosts must point at the database we just migrated ───────────────────────────────────────────
# The committed appsettings.Development.json defaults to LocalDB, but this script targets $SqlHost. A host
# without the override therefore runs against a DIFFERENT, near-empty database — and the symptom is not an
# error, it is a back office that shows none of the data you just seeded, which is a genuinely confusing
# afternoon. Create the override where it is missing; NEVER overwrite an existing one (it may hold a
# developer's TronGrid key or other secrets).
$hosts = @{
    "MerchantGateway"   = $moneyHost
    "OperationsApi"     = $opsHost
    "MerchantPortalApi" = $portalHost
}

foreach ($name in $hosts.Keys) {
    $localFile = Join-Path $hosts[$name] "appsettings.Local.json"

    if (-not (Test-Path $localFile)) {
        $payload = [ordered]@{ Db = [ordered]@{ ConnectionString = $connection } }
        ($payload | ConvertTo-Json -Depth 5) | Out-File -FilePath $localFile -Encoding utf8
        Write-Host "  Created $name/appsettings.Local.json pointing at $SqlHost." -ForegroundColor Green
        continue
    }

    # It exists — check it actually points here, and say so plainly if it does not.
    try {
        $existing = (Get-Content $localFile -Raw | ConvertFrom-Json).Db.ConnectionString
    } catch { $existing = $null }

    if ([string]::IsNullOrWhiteSpace($existing)) {
        Write-Warning "$name/appsettings.Local.json has no Db:ConnectionString - it will use the LocalDB default, NOT $SqlHost. Add: `"Db`": { `"ConnectionString`": `"$connection`" }"
    } elseif ($existing -notlike "*$($SqlHost.Split(',')[0])*") {
        Write-Warning "$name/appsettings.Local.json points somewhere other than $SqlHost - it will not see the data seeded here."
    }
}

Write-Host ""
Write-Host "Local dev database is ready." -ForegroundColor Green
Write-Host "  DBeaver:  host=localhost  port=1433  user=sa  password=$SaPassword  db=CryptoPaymentEngine  (enable 'Trust server certificate')"
Write-Host "  Next:     add your TronGrid API key to appsettings.Local.json, then run the host (see docs/dev-mainnet-deposit.md)."
