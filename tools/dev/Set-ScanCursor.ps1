<#
.SYNOPSIS
  Moves the TRON deposit scanner cursor on a deployed MerchantGateway, safely. Dry run unless -Force.

.DESCRIPTION
  The scanner resumes from deposit.ScanCursor.LastScannedBlock + 1. After a long outage the cursor can sit far
  behind the tip (the scanner catches up at 500 blocks per 10 seconds), or hold a block number written by a
  different chain. This script sets it to a block you choose, in this order:

    1. Reads the host's own config (connection string and node) from its folder. Nothing secret is printed.
    2. Reads the current cursor and the live tip, and works out the target:
         -FromTxHash <hash>  the block of that transaction, minus one, so it is scanned again (nothing lost)
         -Block <n>          exactly that block (the scanner resumes at n + 1)
         (default)           tip - 20
    3. Moving FORWARD skips blocks, and a payment in a skipped block is never detected. The script prints the
       skipped range and every invoice created while the cursor was stuck, since those payments may be in it.
    4. Stops the IIS app pool, so an in-flight scan pass cannot write its own cursor over ours.
    5. Updates the cursor, restarts the pool, sends one request to start the app, and watches the cursor move.

  Moving BACKWARD is always safe: recording a deposit is idempotent on (chain, tx hash, log index), so a
  re-scanned transfer is never recorded or credited twice.

  ASCII only, saved with a BOM: Windows PowerShell 5.1 misreads a BOM-less UTF-8 script.

.EXAMPLE
  .\Set-ScanCursor.ps1 -Folder C:\Projects\DGP-API2                              # dry run, target tip - 20
.EXAMPLE
  .\Set-ScanCursor.ps1 -Folder C:\Projects\DGP-API2 -Force                       # do it
.EXAMPLE
  .\Set-ScanCursor.ps1 -Folder C:\Projects\DGP-API2 -FromTxHash 7c0463f1... -Force   # rescan from a payment
#>
param(
    [Parameter(Mandatory = $true)] [string] $Folder,
    [long] $Block = 0,
    [string] $FromTxHash,
    [string] $Site = 'DGP-API2',
    [string] $Environment,
    # Overrides the node from the host's config.
    [string] $RpcBaseUrl,
    [switch] $NoRestart,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
function Section([string] $title) { Write-Host ''; Write-Host "== $title ==" -ForegroundColor Cyan }
function Bad([string] $text) { Write-Host "  !! $text" -ForegroundColor Red }
function Warn([string] $text) { Write-Host "  ?? $text" -ForegroundColor Yellow }
function Ok([string] $text) { Write-Host "  ok $text" }

# ---- Config (json -> env json -> Local.json, as the host loads it) --------------------------------------------
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
if (-not $Environment) {
    $Environment = 'Production'
    $wc = Join-Path $Folder 'web.config'
    if (Test-Path $wc) {
        [xml] $xml = Get-Content $wc -Raw
        $n = $xml.SelectSingleNode("//environmentVariable[@name='ASPNETCORE_ENVIRONMENT']")
        if ($n) { $Environment = $n.value }
    }
}
$layers = @((Read-Json (Join-Path $Folder 'appsettings.json')), (Read-Json (Join-Path $Folder "appsettings.$Environment.json")), (Read-Json (Join-Path $Folder 'appsettings.Local.json')))
$connectionString = Get-Setting $layers 'Db:ConnectionString'
$rpcBase = if ($RpcBaseUrl) { $RpcBaseUrl } else { Get-Setting $layers 'Chains:Tron:RpcBaseUrl' }; if (-not $rpcBase) { $rpcBase = 'https://api.trongrid.io' }; $rpcBase = $rpcBase.TrimEnd('/')
$apiKey = Get-Setting $layers 'Chains:Tron:ApiKey'
$baseUrl = Get-Setting $layers 'Gateway:BaseUrl'
if (-not $connectionString) { Bad "No Db:ConnectionString in $Folder (environment $Environment)."; exit 1 }

Write-Host "Host folder : $Folder   (environment $Environment)"
Write-Host "Node        : $rpcBase"
Write-Host "Mode        : $(if ($Force) { 'APPLY' } else { 'DRY RUN - nothing is changed; add -Force to apply' })" -ForegroundColor $(if ($Force) { 'Yellow' } else { 'Green' })

# ---- Chain ---------------------------------------------------------------------------------------------------
$headers = @{}; if ($apiKey) { $headers['TRON-PRO-API-KEY'] = $apiKey }
function Invoke-Tron([string] $method, [object[]] $params) {
    $body = @{ jsonrpc = '2.0'; id = 1; method = $method; params = $params } | ConvertTo-Json -Depth 6 -Compress
    $r = Invoke-RestMethod -Uri "$rpcBase/jsonrpc" -Method Post -Body $body -ContentType 'application/json' -Headers $headers -TimeoutSec 20
    if ($r.error) { throw "$method failed: $($r.error | ConvertTo-Json -Compress)" }
    return $r.result
}
function HexToLong([string] $hex) { return [Convert]::ToInt64($hex.Substring(2), 16) }

# ---- DB ------------------------------------------------------------------------------------------------------
$conn = New-Object System.Data.SqlClient.SqlConnection $connectionString
$conn.Open()
function Query([string] $sql, [hashtable] $params = @{}) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql
    foreach ($k in $params.Keys) { [void]$cmd.Parameters.AddWithValue($k, $params[$k]) }
    $t = New-Object System.Data.DataTable; $t.Load($cmd.ExecuteReader()); return ,$t
}

try {
    Section 'Current state'
    $tip = HexToLong (Invoke-Tron 'eth_blockNumber' @())
    $cursorRows = Query "SELECT LastScannedBlock, UpdatedAt FROM deposit.ScanCursor WHERE Chain = 'Tron'"
    $current = if ($cursorRows.Rows.Count) { [long]$cursorRows.Rows[0].LastScannedBlock } else { $null }
    $stuckSince = if ($cursorRows.Rows.Count) { $cursorRows.Rows[0].UpdatedAt } else { $null }
    Write-Host "  chain tip      : $tip"
    Write-Host "  cursor         : $(if ($null -ne $current) { "$current (last moved $stuckSince, $($tip - $current) blocks behind)" } else { 'none - the scanner has never run; it will start at the tip on its own' })"

    # ---- Target ----------------------------------------------------------------------------------------------
    Section 'Target'
    if ($FromTxHash) {
        $h = if ($FromTxHash.StartsWith('0x')) { $FromTxHash } else { "0x$FromTxHash" }
        if ($h.Length -ne 66) { Bad "-FromTxHash must be a 64-hex-character transaction hash, not an address. Got '$FromTxHash'."; exit 1 }
        $receipt = Invoke-Tron 'eth_getTransactionReceipt' @($h)
        if (-not $receipt) { Bad 'Transaction not found on this node (wrong network, or not mined yet).'; exit 1 }
        $target = (HexToLong $receipt.blockNumber) - 1
        Write-Host "  transaction is in block $($target + 1); cursor -> $target so that block is scanned again"
    } elseif ($Block -gt 0) {
        $target = $Block
        Write-Host "  cursor -> $target (scanning resumes at $($target + 1))"
    } else {
        $target = $tip - 20
        Write-Host "  cursor -> $target (tip - 20)"
    }
    if ($target -gt $tip) { Bad "Target $target is beyond the chain tip $tip. The scanner would wait for blocks that do not exist."; exit 1 }

    if ($null -ne $current -and $target -gt $current) {
        $skipped = $target - $current
        Warn "Moving FORWARD skips blocks $($current + 1)..$target ($skipped blocks). A payment in that range is NEVER detected."
        $invoices = Query "SELECT PublicReference, Kind, Status, ExpectedAmount, Address, CreatedAt FROM paymentintent.PaymentIntent WHERE CreatedAt >= @since ORDER BY CreatedAt" @{ '@since' = $stuckSince }
        if ($invoices.Rows.Count -gt 0) {
            Warn "$($invoices.Rows.Count) invoice(s) were created while the cursor was stuck. If any was paid, rescan from that payment with -FromTxHash instead:"
            ($invoices | Format-Table -AutoSize | Out-String -Width 250).TrimEnd() -split "`n" | ForEach-Object { Write-Host "     $_" }
        } else { Ok 'No invoices were created while the cursor was stuck.' }
    } elseif ($null -ne $current) {
        Ok "Moving backward by $($current - $target) blocks: re-scanned transfers are deduplicated, nothing is recorded twice."
        if (($tip - $target) -gt 1000) { Warn "The scanner needs ~$([math]::Ceiling(($tip - $target) / 500 * 10 / 60)) min to reach the tip from there." }
    }

    if (-not $Force) {
        Write-Host ''
        Write-Host 'Dry run complete. Re-run with -Force to stop the site, move the cursor and restart.' -ForegroundColor Green
        return
    }

    # ---- Stop the host, so a running scan pass cannot overwrite the new cursor ---------------------------------
    $pool = $null
    if (-not $NoRestart) {
        Section 'Stopping the host'
        Import-Module WebAdministration
        $pool = (Get-Website -Name $Site).applicationPool
        if (-not $pool) { Bad "IIS site '$Site' not found. Pass -Site, or -NoRestart to skip IIS."; exit 1 }
        if ((Get-WebAppPoolState -Name $pool).Value -ne 'Stopped') { Stop-WebAppPool -Name $pool }
        for ($i = 0; $i -lt 30 -and (Get-WebAppPoolState -Name $pool).Value -ne 'Stopped'; $i++) { Start-Sleep -Seconds 1 }
        Ok "App pool '$pool' stopped."
    } else {
        Warn 'NoRestart: if the host is running, a scan pass in progress can overwrite this cursor. Check it moved from the new value.'
    }

    # ---- Update ----------------------------------------------------------------------------------------------
    Section 'Updating the cursor'
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
MERGE deposit.ScanCursor AS t
USING (SELECT 'Tron' AS Chain) AS s ON t.Chain = s.Chain
WHEN MATCHED THEN UPDATE SET LastScannedBlock = @b, UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN INSERT (Chain, LastScannedBlock, UpdatedAt) VALUES ('Tron', @b, SYSDATETIMEOFFSET());
"@
    [void]$cmd.Parameters.AddWithValue('@b', [long]$target)
    [void]$cmd.ExecuteNonQuery()
    $after = (Query "SELECT LastScannedBlock FROM deposit.ScanCursor WHERE Chain = 'Tron'").Rows[0].LastScannedBlock
    Ok "Cursor was $current, now $after."

    # ---- Restart and watch ----------------------------------------------------------------------------------
    if ($pool) {
        Section 'Starting the host'
        Start-WebAppPool -Name $pool
        Ok "App pool '$pool' started."
        # An in-process IIS app only starts on a request (unless preload + AlwaysRunning are configured).
        if ($baseUrl) {
            try { Invoke-WebRequest -Uri "$($baseUrl.TrimEnd('/'))/pay/00000000-0000-0000-0000-000000000000/info" -UseBasicParsing -TimeoutSec 60 | Out-Null } catch { }
            Ok "Sent a warm-up request to $baseUrl."
        } else { Warn 'No Gateway:BaseUrl - send any request to the site yourself so IIS starts the app.' }

        Write-Host '  watching the cursor for up to 90 seconds...'
        $moved = $false
        for ($i = 0; $i -lt 18; $i++) {
            Start-Sleep -Seconds 5
            $now = [long](Query "SELECT LastScannedBlock FROM deposit.ScanCursor WHERE Chain = 'Tron'").Rows[0].LastScannedBlock
            if ($now -gt $target) { Ok "Scanner is running: cursor $target -> $now."; $moved = $true; break }
        }
        if (-not $moved) {
            Bad 'The cursor did not move in 90 seconds. The app did not start, or every scan pass fails.'
            Bad "Check the newest file in $(Join-Path $Folder 'logs'), or run Get-DepositDiagnosis.ps1."
        }
    }
} finally {
    $conn.Close()
}
