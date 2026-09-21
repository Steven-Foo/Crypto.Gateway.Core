<#
.SYNOPSIS
  Switches the staging secret store between AWS KMS and the in-memory store. Dry run unless -Force.

.DESCRIPTION
  Staging (the testnet tier) can hold HD-wallet secrets in either of two stores, set by KeyManagement:Kms:Enabled:

    InMemory   the default: public xpubs in config, dev seeds derived in the process. No AWS needed.
    Kms        AWS KMS envelope custody: a seed is sealed under a CMK, only its ciphertext is stored in the database,
               and it is decrypted in memory only to derive a key. The same custody code Production runs.

  A host registers exactly one store, and every wallet row records the store holding its secret. So on boot the
  MerchantGateway archives the active wallets of the store that is off and restores the ones it archived last time
  (CustodyModeReconciler). This script changes the setting on all three hosts and restarts them in the right order:

    1. Checks the deployed build contains the switch, and (for Kms) the key ARNs, the region, and that the box has
       an EC2 instance role and no static AWS keys.
    2. Shows what changes: which wallets are archived or restored, and how many addresses become receive-only.
    3. Stops the portal, Ops and gateway app pools; backs up and updates each host's appsettings.Local.json.
    4. Starts the gateway first and confirms in the database that no wallet of the other store is still active.
       If the gateway fails to start, restores the backups and restarts on the previous setting.
    5. Starts Ops and the portal.

  What switching means for money:
    - Deposits keep working. Detection needs no key, so every existing address still receives and credits.
    - An address whose wallet is archived cannot be swept, and a hot wallet whose wallet is archived cannot pay out,
      until you switch back. Payouts with no signable hot wallet wait in AwaitingFunds with the reserve held.
    - In Kms mode the gateway creates a new KMS withdrawal wallet and hot pool on boot. That pool starts empty:
      fund it on Nile before testing a payout. Merchant deposit wallets are created under KMS on their next address.
    - Energy staking stays inert in Kms mode (its seeder imports a private key, and there is no Energy CMK).

  Use CMKs created for testnet only. Anyone who controls this box can decrypt whatever its instance role allows.

  ASCII only, saved with a BOM: Windows PowerShell 5.1 misreads a BOM-less UTF-8 script.

.EXAMPLE
  .\Switch-StagingCustody.ps1 -Mode Kms -Region ap-southeast-1 -DepositKeyArn arn:aws:kms:... -WithdrawalKeyArn arn:aws:kms:...
.EXAMPLE
  .\Switch-StagingCustody.ps1 -Mode Kms -Force            # ARNs and region already in appsettings.Local.json
.EXAMPLE
  .\Switch-StagingCustody.ps1 -Mode InMemory -Force
#>
param(
    [Parameter(Mandatory = $true)] [ValidateSet('Kms', 'InMemory')] [string] $Mode,
    [string] $Region,
    [string] $DepositKeyArn,
    [string] $WithdrawalKeyArn,
    [int] $TimeoutSeconds = 180,
    [switch] $SkipAwsCheck,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$HostDlls = @{
    'CryptoPaymentEngine.Api.MerchantGateway.dll'   = 'Gateway'
    'CryptoPaymentEngine.Api.OperationsApi.dll'     = 'Ops'
    'CryptoPaymentEngine.Api.MerchantPortalApi.dll' = 'Portal'
}
$WarmUpPath = @{ Gateway = '/pay/00000000-0000-0000-0000-000000000000/info'; Ops = '/api/v1/ops/auth/me'; Portal = '/api/v1/portal/auth/me' }
$Kind = if ($Mode -eq 'Kms') { 'AwsKmsEnvelope' } else { 'InMemoryDevelopment' }
$OtherKind = if ($Mode -eq 'Kms') { 'InMemoryDevelopment' } else { 'AwsKmsEnvelope' }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host 'Run this from an elevated PowerShell (it stops and starts IIS app pools).' -ForegroundColor Red; exit 1 }

$failures = New-Object System.Collections.Generic.List[string]
function Section([string] $title) { Write-Host ''; Write-Host "== $title ==" -ForegroundColor Cyan }
function Bad([string] $text) { Write-Host "  !! $text" -ForegroundColor Red; $failures.Add($text) }
function Warn([string] $text) { Write-Host "  ?? $text" -ForegroundColor Yellow }
function Ok([string] $text) { Write-Host "  ok $text" }
function Stop-Here([int] $code) { if ($conn) { $conn.Close() }; exit $code }

# ---- Config helpers ---------------------------------------------------------------------------------------------
function Read-Json([string] $file) {
    if (-not (Test-Path $file)) { return $null }
    $raw = [IO.File]::ReadAllText($file)
    $raw = [regex]::Replace($raw, '(?m)^\s*//.*$', '')
    $raw = [regex]::Replace($raw, ',(\s*[}\]])', '$1')
    return $raw | ConvertFrom-Json
}
function Get-Setting([object[]] $layers, [string] $path) {
    $value = $null
    foreach ($layer in $layers) {
        if ($null -eq $layer) { continue }
        $node = $layer
        foreach ($part in $path.Split(':')) { if ($null -eq $node) { break }; $node = $node.PSObject.Properties[$part].Value }
        if ($null -ne $node -and "$node" -ne '') { $value = $node }
    }
    return $value
}
function Get-HostLayers($h) {
    return @((Read-Json (Join-Path $h.Folder 'appsettings.json')), (Read-Json (Join-Path $h.Folder "appsettings.$($h.Environment).json")), (Read-Json (Join-Path $h.Folder 'appsettings.Local.json')))
}
function Set-JsonValue($root, [string] $path, $value) {
    $parts = $path.Split(':'); $node = $root
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        $child = $node.PSObject.Properties[$parts[$i]]
        if (-not $child -or $null -eq $child.Value -or $child.Value -isnot [System.Management.Automation.PSCustomObject]) {
            $node | Add-Member -NotePropertyName $parts[$i] -NotePropertyValue ([pscustomobject]@{}) -Force
        }
        $node = $node.PSObject.Properties[$parts[$i]].Value
    }
    $node | Add-Member -NotePropertyName $parts[-1] -NotePropertyValue $value -Force
}

# =================================================================================================================
Section "Switching staging custody to $Mode"
Write-Host "  Mode: $(if ($Force) { 'APPLY' } else { 'DRY RUN - nothing is changed; add -Force to apply' })" -ForegroundColor $(if ($Force) { 'Yellow' } else { 'Green' })

Section '1. Hosts and build'
Import-Module WebAdministration
$hosts = @()
foreach ($site in Get-Website) {
    $folder = [Environment]::ExpandEnvironmentVariables($site.physicalPath)
    $webConfig = Join-Path $folder 'web.config'
    if (-not (Test-Path $webConfig)) { continue }
    [xml] $xml = Get-Content $webConfig -Raw
    $aspNetCore = $xml.SelectSingleNode('//aspNetCore'); if (-not $aspNetCore) { continue }
    $dll = $HostDlls.Keys | Where-Object { "$($aspNetCore.arguments) $($aspNetCore.processPath)" -like "*$([IO.Path]::GetFileNameWithoutExtension($_))*" } | Select-Object -First 1
    if (-not $dll) { continue }
    if ($site.state -eq 'Stopped' -and -not $site.serverAutoStart) { Write-Host "  -- $($site.name) is stopped and not auto-start: left alone."; continue }
    $envNode = $xml.SelectSingleNode("//environmentVariable[@name='ASPNETCORE_ENVIRONMENT']")
    $binding = @($site.bindings.Collection | Where-Object { $_.protocol -eq 'http' } | Select-Object -First 1)
    $parts = if ($binding) { $binding[0].bindingInformation.Split(':') } else { @('', '80', '') }
    $hosts += [pscustomobject]@{
        Site = $site.name; Pool = $site.applicationPool; Kind = $HostDlls[$dll]; Folder = $folder
        Environment = if ($envNode) { $envNode.value } else { 'Production' }; Port = [int]$parts[1]; HostHeader = $parts[2]
    }
}
$gateway = @($hosts | Where-Object Kind -eq 'Gateway')
if ($gateway.Count -ne 1) { Bad "Expected exactly one running gateway site, found $($gateway.Count)."; Stop-Here 1 }
$gateway = $gateway[0]

foreach ($h in $hosts) {
    $km = Join-Path $h.Folder 'CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.dll'
    $hasSwitch = (Test-Path $km) -and ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($km)) -match 'AddTestnetKeyCustody')
    $layers = Get-HostLayers $h
    $current = if ("$(Get-Setting $layers 'KeyManagement:Kms:Enabled')" -eq 'True') { 'Kms' } else { 'InMemory' }
    Write-Host "  $($h.Site) ($($h.Kind), $($h.Environment)): currently $current$(if (-not $hasSwitch) { ', build WITHOUT the custody switch' })"
    if (-not $hasSwitch) { Bad "$($h.Site) runs a build without the custody switch. Deploy the new build to $($h.Folder) first." }
    if ($h.Environment -notin @('Development', 'Staging')) { Bad "$($h.Site) runs as $($h.Environment). This switch is for the testnet tier only; Production custody is always KMS." }
}
$sharedPools = $hosts | Group-Object Pool | Where-Object Count -gt 1
foreach ($p in $sharedPools) { Bad "App pool '$($p.Name)' is shared by $($p.Group.Site -join ', ')." }
if ($failures.Count) { Stop-Here 1 }

# ---- KMS identifiers -----------------------------------------------------------------------------------------------
if ($Mode -eq 'Kms') {
    Section '2. KMS identifiers and AWS access'
    $gwLayers = Get-HostLayers $gateway
    if (-not $Region) { $Region = Get-Setting $gwLayers 'KeyManagement:Kms:Region' }
    if (-not $DepositKeyArn) { $DepositKeyArn = Get-Setting $gwLayers 'KeyManagement:Kms:KeyArns:Deposit' }
    if (-not $WithdrawalKeyArn) { $WithdrawalKeyArn = Get-Setting $gwLayers 'KeyManagement:Kms:KeyArns:Withdrawal' }
    $arnPattern = '^arn:aws:kms:(?<region>[a-z0-9-]+):(?<account>\d{12}):key/[0-9a-f-]{36}$'
    foreach ($pair in @(@{ Name = 'Deposit'; Arn = $DepositKeyArn }, @{ Name = 'Withdrawal'; Arn = $WithdrawalKeyArn })) {
        if (-not $pair.Arn) { Bad "No $($pair.Name) key ARN. Pass -$($pair.Name)KeyArn."; continue }
        $m = [regex]::Match($pair.Arn, $arnPattern)
        if (-not $m.Success) { Bad "$($pair.Name) key ARN is not a KMS key ARN (arn:aws:kms:<region>:<account>:key/<id>). An alias ARN is not accepted."; continue }
        if ($Region -and $m.Groups['region'].Value -ne $Region) { Bad "$($pair.Name) key is in $($m.Groups['region'].Value) but -Region is $Region." }
        Write-Host "  $($pair.Name) CMK: $($pair.Arn)"
    }
    if (-not $Region) { Bad 'No region. Pass -Region (e.g. ap-southeast-1).' }
    if ($DepositKeyArn -and $DepositKeyArn -eq $WithdrawalKeyArn) { Bad 'Deposit and withdrawal must be different CMKs, so one purpose''s key cannot reach the other''s seeds.' }

    foreach ($name in 'AWS_ACCESS_KEY_ID', 'AWS_SECRET_ACCESS_KEY', 'AWS_SESSION_TOKEN') {
        foreach ($scope in 'Machine', 'User', 'Process') {
            if ([Environment]::GetEnvironmentVariable($name, $scope)) { Bad "$name is set ($scope). Static AWS keys must not be used; the instance role supplies credentials. Remove it." }
        }
    }
    if (Test-Path (Join-Path $env:SystemDrive 'Windows\System32\config\systemprofile\.aws\credentials')) { Warn 'A credentials file exists for the SYSTEM profile. Make sure it holds no static keys.' }

    if (-not $SkipAwsCheck) {
        try {
            $token = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 3
            $role = Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/iam/security-credentials/' -Headers @{ 'X-aws-ec2-metadata-token' = $token } -TimeoutSec 3
            if ($role) { Ok "EC2 instance role attached: $role" } else { Bad 'No IAM instance role is attached to this instance. See docs/kms-go-live.md steps 1-2.' }
        } catch { Bad "Cannot read the EC2 instance role from instance metadata ($($_.Exception.Message)). See docs/kms-go-live.md step 2, or -SkipAwsCheck off-EC2." }

        if (Get-Command aws -ErrorAction SilentlyContinue) {
            foreach ($arn in @($DepositKeyArn, $WithdrawalKeyArn) | Where-Object { $_ }) {
                $state = & aws kms describe-key --key-id $arn --region $Region --query 'KeyMetadata.KeyState' --output text 2>&1
                if ($LASTEXITCODE -eq 0 -and "$state".Trim() -eq 'Enabled') { Ok "CMK reachable and enabled: $arn" }
                else { Bad "CMK $arn is not usable from this box: $state" }
            }
            Warn 'describe-key proves the key exists and is visible. Encrypt/Decrypt rights are only proven by docs/kms-go-live.md step 3.'
        } else { Warn 'AWS CLI not installed, so the CMKs were not checked. Run docs/kms-go-live.md step 3 before relying on KMS mode.' }
    }
    if ($failures.Count) { Stop-Here 1 }
}

# ---- What changes --------------------------------------------------------------------------------------------------
Section '3. What changes'
$connectionString = Get-Setting (Get-HostLayers $gateway) 'Db:ConnectionString'
$conn = New-Object System.Data.SqlClient.SqlConnection $connectionString
$conn.Open()
function Query([string] $sql, [hashtable] $params = @{}) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql
    foreach ($k in $params.Keys) { [void]$cmd.Parameters.AddWithValue($k, $params[$k]) }
    $t = New-Object System.Data.DataTable; $t.Load($cmd.ExecuteReader()); return ,$t
}
function Show($table) { ($table | Format-Table -AutoSize | Out-String -Width 200).TrimEnd() -split "`n" | ForEach-Object { Write-Host "     $_" } }
$walletSummarySql = "SELECT SecretProvider, Purpose, CASE WHEN MerchantId IS NULL THEN 'platform' ELSE 'merchant' END AS Owner, Status, COUNT(*) AS Wallets FROM keymgmt.HdWallet GROUP BY SecretProvider, Purpose, CASE WHEN MerchantId IS NULL THEN 'platform' ELSE 'merchant' END, Status ORDER BY SecretProvider, Purpose, Owner, Status"

Write-Host '  HD wallets now:'
Show (Query $walletSummarySql)
$affected = Query @"
SELECT w.WalletType, COUNT(*) AS Addresses, SUM(CASE WHEN w.DepositsReceivedCount > 0 THEN 1 ELSE 0 END) AS Funded
FROM wallet.Wallet w
JOIN keymgmt.DerivedKey k ON k.Id = w.DerivedKeyId
JOIN keymgmt.HdWallet h ON h.Id = k.HdWalletId
WHERE h.Status = 'Active' AND h.SecretProvider = @other
GROUP BY w.WalletType
"@ @{ '@other' = $OtherKind }
if ($affected.Rows.Count) {
    Warn "These addresses belong to $OtherKind wallets and become RECEIVE-ONLY (no sweep, no payout) until you switch back:"
    Show $affected
} else { Ok "No active $OtherKind wallet owns an address, so nothing becomes receive-only." }
$restorable = Query "SELECT Purpose, COUNT(*) AS Wallets FROM keymgmt.HdWallet WHERE Status = 'Archived' AND SecretProvider = @kind GROUP BY Purpose" @{ '@kind' = $Kind }
if ($restorable.Rows.Count) { Ok "Archived $Kind wallets from an earlier switch will be restored where no active wallet exists:"; Show $restorable }
$inFlight = (Query "SELECT (SELECT COUNT(*) FROM withdrawal.Withdrawal WHERE Status IN ('Signing','Broadcast')) AS Withdrawals, (SELECT COUNT(*) FROM sweep.Sweep WHERE Status IN ('Signing','Broadcast')) AS Sweeps").Rows[0]
if ($inFlight.Withdrawals -gt 0 -or $inFlight.Sweeps -gt 0) {
    Warn "In flight: $($inFlight.Withdrawals) withdrawal(s), $($inFlight.Sweeps) sweep(s). Already signed, so they still broadcast and confirm; nothing is re-signed."
}

if (-not $Force) {
    Write-Host ''
    Write-Host "Dry run complete. Re-run with -Force to switch to $Mode." -ForegroundColor Green
    Stop-Here 0
}

# ---- Apply ---------------------------------------------------------------------------------------------------------
Section '4. Applying'
$order = @($hosts | Where-Object Kind -eq 'Portal') + @($hosts | Where-Object Kind -eq 'Ops') + @($gateway)
foreach ($h in $order) {
    if ((Get-WebAppPoolState -Name $h.Pool).Value -ne 'Stopped') { Stop-WebAppPool -Name $h.Pool }
}
foreach ($h in $order) {
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-WebAppPoolState -Name $h.Pool).Value -ne 'Stopped' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
}
Ok 'Portal, Ops and gateway app pools stopped.'

$stamp = '{0:yyyyMMdd-HHmmss}' -f (Get-Date)
$backups = @{}
foreach ($h in $hosts) {
    $local = Join-Path $h.Folder 'appsettings.Local.json'
    if (Test-Path $local) { $backup = "$local.bak-$stamp"; Copy-Item $local $backup; $backups[$local] = $backup } else { $backups[$local] = $null }
    $json = Read-Json $local; if ($null -eq $json) { $json = [pscustomobject]@{} }
    Set-JsonValue $json 'KeyManagement:Kms:Enabled' ($Mode -eq 'Kms')
    if ($Mode -eq 'Kms') {
        Set-JsonValue $json 'KeyManagement:Kms:Region' $Region
        Set-JsonValue $json 'KeyManagement:Kms:KeyArns:Deposit' $DepositKeyArn
        Set-JsonValue $json 'KeyManagement:Kms:KeyArns:Withdrawal' $WithdrawalKeyArn
    }
    [IO.File]::WriteAllText($local, ($json | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding $false))
    Ok "$($h.Site): KeyManagement:Kms:Enabled=$($Mode -eq 'Kms') written to appsettings.Local.json$(if ($backups[$local]) { " (backup: $([IO.Path]::GetFileName($backups[$local])))" })"
}

function Send-WarmUp($h) {
    $request = [Net.HttpWebRequest]::Create("http://127.0.0.1:$($h.Port)$($WarmUpPath[$h.Kind])")
    if ($h.HostHeader) { $request.Host = $h.HostHeader }
    $request.Timeout = $TimeoutSeconds * 1000
    try { $r = $request.GetResponse(); $code = [int]$r.StatusCode; $r.Close(); return $code }
    catch [Net.WebException] { if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode; $_.Exception.Response.Close(); return $code }; return 0 }
}
function Start-HostAndCheck($h) {
    Start-WebAppPool -Name $h.Pool
    $code = Send-WarmUp $h
    return ($code -ge 200 -and $code -lt 500)
}

Section '5. Starting the gateway (it runs the wallet switch on boot)'
$gatewayUp = Start-HostAndCheck $gateway
$stillActive = [int](Query "SELECT COUNT(*) AS N FROM keymgmt.HdWallet WHERE Status = 'Active' AND SecretProvider = @other" @{ '@other' = $OtherKind }).Rows[0].N
if (-not $gatewayUp -or $stillActive -gt 0) {
    if (-not $gatewayUp) { Bad 'The gateway did not start.' } else { Bad "The gateway started but $stillActive $OtherKind wallet(s) are still active." }
    $log = Get-ChildItem (Join-Path $gateway.Folder 'logs') -Filter 'stdout*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) { Write-Host '  last errors in the gateway log:'; Select-String -Path $log.FullName -Pattern 'Custody|KeyManagement:Kms|\[(ERR|FTL)\]|crit:|Unhandled' | Select-Object -Last 8 | ForEach-Object { Write-Host "     $($_.Line.Trim())" } }

    Warn 'Rolling back: restoring each host''s previous appsettings.Local.json and restarting.'
    Stop-WebAppPool -Name $gateway.Pool -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    foreach ($local in $backups.Keys) {
        if ($backups[$local]) { Copy-Item $backups[$local] $local -Force } else { Remove-Item $local -Force -ErrorAction SilentlyContinue }
    }
    foreach ($h in $order[($order.Count - 1)..0]) { [void](Start-HostAndCheck $h) }
    Bad 'Switch rolled back. Fix the cause above and run again.'
    Stop-Here 1
}
Ok "Gateway is up and no $OtherKind wallet is active."

Section '6. Starting Ops and the portal'
foreach ($h in @($hosts | Where-Object Kind -ne 'Gateway')) {
    if (Start-HostAndCheck $h) { Ok "$($h.Site) is up." } else { Bad "$($h.Site) did not start. Check $(Join-Path $h.Folder 'logs')." }
}

Section '7. Result'
Write-Host '  HD wallets now:'
Show (Query $walletSummarySql)
if ($Mode -eq 'Kms') {
    $kmsPool = [int](Query "SELECT COUNT(*) AS N FROM keymgmt.HdWallet WHERE Status = 'Active' AND SecretProvider = 'AwsKmsEnvelope' AND Purpose = 'Withdrawal' AND MerchantId IS NULL").Rows[0].N
    $sealed = [int](Query "SELECT COUNT(*) AS N FROM keymgmt.SecretMaterial").Rows[0].N
    if ($kmsPool -gt 0) { Ok "KMS withdrawal wallet active; $sealed sealed seed(s) stored. Fund the new hot pool on Nile before testing a payout." }
    else { Warn 'No KMS withdrawal wallet yet. The hot pool seeder creates it on boot when Treasury:HotWalletPool is configured; if it is, check the gateway log for a KMS error (kms:Encrypt denied?).' }
    Warn 'Merchant deposit wallets are created under KMS on each merchant''s next deposit address.'
}
if ($failures.Count -eq 0) { Write-Host "  Staging custody is now $Mode." -ForegroundColor Green }
Stop-Here $(if ($failures.Count -eq 0) { 0 } else { 1 })
