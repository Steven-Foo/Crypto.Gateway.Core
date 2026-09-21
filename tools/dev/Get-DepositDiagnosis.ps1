<#
.SYNOPSIS
  Finds where a TRON deposit stopped on a deployed MerchantGateway. Read-only.

.DESCRIPTION
  A deposit goes scanner -> confirmation worker -> outbox relay -> invoice match. Each step leaves a trace in
  its own table or log, so this checks them in order and says which one is stuck:

    1. TronGrid       does the node accept the host's configured API key?
    2. Transaction    (with -TxHash) which token moved, to which address, in which block, and is that address ours?
    3. Scanner        deposit.ScanCursor against the live Nile tip.
    4. Deposits       deposit.Deposit rows (for -Address, or the latest ones).
    5. Outbox         unsent deposit / paymentintent outbox rows and their last error.
    6. Invoices       paymentintent.PaymentIntent rows.
    7. Logs           errors in the host's stdout logs.

  Nothing is written anywhere. The connection string and API key are read from the host's own config and are
  never printed. Safe to paste the output into a chat.

  ASCII only, saved with a BOM: Windows PowerShell 5.1 misreads a BOM-less UTF-8 script.

.EXAMPLE
  .\Get-DepositDiagnosis.ps1 -Folder C:\Projects\DGP-API2 -TxHash 383dd538e0d8...
.EXAMPLE
  .\Get-DepositDiagnosis.ps1 -Folder C:\Projects\DGP-API2 -Address TXyz...
#>
param(
    [Parameter(Mandatory = $true)] [string] $Folder,
    [string] $Address,
    [string] $TxHash,
    [string] $Environment,
    # Overrides the node from the host's config, e.g. to check a Nile hash from a host configured for mainnet.
    [string] $RpcBaseUrl
)

$ErrorActionPreference = 'Stop'
$UsdtTransferTopic = '0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef'

function Section([string] $title) { Write-Host ''; Write-Host "== $title ==" -ForegroundColor Cyan }
function Bad([string] $text) { Write-Host "  !! $text" -ForegroundColor Red }
function Warn([string] $text) { Write-Host "  ?? $text" -ForegroundColor Yellow }
function Ok([string] $text) { Write-Host "  ok $text" }

# ---- Config: same load order as the host (json -> env json -> Local.json) --------------------------------------
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
        foreach ($part in $path.Split(':')) {
            if ($null -eq $node) { break }
            if ($part -match '^\d+$' -and $node -is [array]) { $node = $node[[int]$part] }
            else { $node = $node.PSObject.Properties[$part].Value }
        }
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
$layers = @(
    (Read-Json (Join-Path $Folder 'appsettings.json')),
    (Read-Json (Join-Path $Folder "appsettings.$Environment.json")),
    (Read-Json (Join-Path $Folder 'appsettings.Local.json'))
)
$connectionString = Get-Setting $layers 'Db:ConnectionString'
$rpcBase = if ($RpcBaseUrl) { $RpcBaseUrl } else { Get-Setting $layers 'Chains:Tron:RpcBaseUrl' }
if (-not $rpcBase) { $rpcBase = 'https://api.trongrid.io' }
$rpcBase = $rpcBase.TrimEnd('/')
$apiKey = Get-Setting $layers 'Chains:Tron:ApiKey'
$confirmations = Get-Setting $layers 'Deposit:Policies:Tron:Confirmations'
$minDeposit = Get-Setting $layers 'Deposit:Policies:Tron:MinDepositBaseUnits'
$usdtContract = $null
for ($i = 0; $i -lt 10; $i++) {
    if ((Get-Setting $layers "Blockchain:Assets:$i`:Symbol") -eq 'USDT') { $usdtContract = Get-Setting $layers "Blockchain:Assets:$i`:ContractAddress" }
}

Write-Host "Host folder : $Folder"
Write-Host "Environment : $Environment"
Write-Host "Node        : $rpcBase"
Write-Host "USDT        : $usdtContract    confirmations: $confirmations    minimum deposit (base units): $minDeposit"
if (-not $connectionString) { Bad 'No Db:ConnectionString found in this folder''s config. Pass -Environment if web.config is not the source.'; return }

# ---- TRON address helpers (Base58Check <-> 20-byte hex) ------------------------------------------------------
$Alphabet = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz'
function Convert-HexToTron([string] $hex20) {
    $hex20 = $hex20 -replace '^0x', ''
    $bytes = [byte[]](@(0x41) + (0..19 | ForEach-Object { [Convert]::ToByte($hex20.Substring($_ * 2, 2), 16) }))
    $sha = [Security.Cryptography.SHA256]::Create()
    $check = $sha.ComputeHash($sha.ComputeHash($bytes))[0..3]
    $full = [byte[]]($bytes + $check)
    $num = [Numerics.BigInteger]::Zero
    foreach ($b in $full) { $num = $num * 256 + $b }
    $sb = New-Object Text.StringBuilder
    while ($num -gt 0) { $r = [int]($num % 58); $num = [Numerics.BigInteger]::Divide($num, 58); [void]$sb.Insert(0, $Alphabet[$r]) }
    return $sb.ToString()
}
function Convert-TronToHex([string] $address) {
    $num = [Numerics.BigInteger]::Zero
    foreach ($c in $address.ToCharArray()) {
        $idx = $Alphabet.IndexOf($c)
        if ($idx -lt 0) { throw "Not a Base58 character: $c" }
        $num = $num * 58 + $idx
    }
    $hex = $num.ToString('x').TrimStart('0')
    $hex = $hex.PadLeft(50, '0')
    return $hex.Substring(2, 40)
}

# ---- 1. TronGrid -----------------------------------------------------------------------------------------------
Section '1. TronGrid access with the configured key'
$headers = @{}
if ($apiKey) { $headers['TRON-PRO-API-KEY'] = $apiKey }
function Invoke-Tron([string] $method, [object[]] $params) {
    $body = @{ jsonrpc = '2.0'; id = 1; method = $method; params = $params } | ConvertTo-Json -Depth 6 -Compress
    return Invoke-RestMethod -Uri "$rpcBase/jsonrpc" -Method Post -Body $body -ContentType 'application/json' -Headers $headers -TimeoutSec 20
}
$tip = $null
try {
    $r = Invoke-Tron 'eth_blockNumber' @()
    $tip = [Convert]::ToInt64($r.result.Substring(2), 16)
    Ok "Node accepts the host's key (key $(if ($apiKey) { 'set' } else { 'NOT set' })). Tip block $tip."
} catch {
    $status = $null
    if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
    Bad "Node REFUSED the call with the host's key (HTTP $status): $($_.Exception.Message)"
    Bad 'The scanner makes this same call every 10 seconds, so it cannot see any transfer. Replace Chains:Tron:ApiKey.'
    $headers.Remove('TRON-PRO-API-KEY')
    try { $tip = [Convert]::ToInt64((Invoke-Tron 'eth_blockNumber' @()).result.Substring(2), 16); Warn "Without a key the node answers (tip $tip), so it is the key being refused." } catch { }
}

# ---- 2. The tester's transaction --------------------------------------------------------------------------------
$txAddress = $null; $txBlock = $null
if ($TxHash) {
    Section '2. The tester''s transaction'
    $h = if ($TxHash.StartsWith('0x')) { $TxHash } else { "0x$TxHash" }
    try {
        $receipt = (Invoke-Tron 'eth_getTransactionReceipt' @($h)).result
        if (-not $receipt) {
            Bad 'Not found on this node. Wrong network (mainnet vs Nile), or a mistyped hash.'
        } else {
            $txBlock = [Convert]::ToInt64($receipt.blockNumber.Substring(2), 16)
            Write-Host "  block $txBlock   status $($receipt.status)   confirmations now: $(if ($tip) { $tip - $txBlock + 1 } else { '?' })"
            if ($receipt.status -ne '0x1') { Bad 'The transaction FAILED on-chain. No tokens moved.' }
            $transfers = @($receipt.logs | Where-Object { $_.topics.Count -eq 3 -and $_.topics[0] -eq $UsdtTransferTopic })
            if ($transfers.Count -eq 0) { Bad 'No token Transfer event. A native TRX send, or not a token transfer. The USDT scanner does not see it.' }
            foreach ($log in $transfers) {
                $contract = Convert-HexToTron $log.address
                $to = Convert-HexToTron ($log.topics[2].Substring($log.topics[2].Length - 40))
                $amount = [Numerics.BigInteger]::Parse('0' + ($log.data -replace '^0x', ''), 'AllowHexSpecifier')
                Write-Host "  token $contract  ->  to $to   amount (base units) $amount"
                if ($usdtContract -and $contract -ne $usdtContract) { Bad "Token is NOT the configured USDT ($usdtContract). The scanner ignores it." }
                else { Ok 'Token is the configured USDT.' }
                if ($minDeposit -and $amount -lt [Numerics.BigInteger]::Parse("$minDeposit")) { Bad "Amount is below the minimum deposit ($minDeposit base units); it is recorded as Ignored." }
                $txAddress = $to
            }
        }
    } catch { Bad "Lookup failed: $($_.Exception.Message)" }
}
if (-not $Address -and $txAddress) { $Address = $txAddress }

# ---- SQL -------------------------------------------------------------------------------------------------------
$conn = New-Object System.Data.SqlClient.SqlConnection $connectionString
try { $conn.Open() } catch { Bad "Cannot open the database: $($_.Exception.Message)"; return }
function Query([string] $sql, [hashtable] $params = @{}) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 30
    foreach ($k in $params.Keys) { [void]$cmd.Parameters.AddWithValue($k, $params[$k]) }
    $table = New-Object System.Data.DataTable
    $table.Load($cmd.ExecuteReader())
    return ,$table
}
function Show($table) {
    if ($table.Rows.Count -eq 0) { Write-Host '  (no rows)'; return }
    ($table | Format-Table -AutoSize | Out-String -Width 250).TrimEnd() -split "`n" | ForEach-Object { "  $_" } | Write-Host
}

# ---- 3. Scanner -------------------------------------------------------------------------------------------------
Section '3. Scanner cursor'
$cursor = Query "SELECT Chain, LastScannedBlock, UpdatedAt, DATEDIFF(SECOND, UpdatedAt, SYSDATETIMEOFFSET()) AS SecondsAgo FROM deposit.ScanCursor"
Show $cursor
$tron = $cursor.Rows | Where-Object { $_.Chain -eq 'Tron' }
if (-not $tron) { Bad 'No Tron cursor: the scanner has never completed a pass.' }
else {
    if ($tron.SecondsAgo -gt 120) { Bad "Cursor last moved $($tron.SecondsAgo)s ago. The scanner is failing or the site is not running; see the logs below." }
    else { Ok "Cursor moved $($tron.SecondsAgo)s ago." }
    if ($tip) {
        $lag = $tip - $tron.LastScannedBlock
        if ($lag -gt 1000) { Bad "Cursor is $lag blocks behind the tip. At 500 blocks per 10s it needs ~$([math]::Ceiling($lag / 500 * 10 / 60)) min to catch up." }
        elseif ($lag -lt -5) { Bad "Cursor is AHEAD of the real tip by $(-$lag) blocks. It was written by a different chain (e.g. the in-memory fake), so real blocks up to $($tron.LastScannedBlock) are skipped." }
        else { Ok "Cursor is $lag blocks behind the tip." }
    }
    if ($txBlock -and $tron.LastScannedBlock -lt $txBlock) { Warn "The transfer's block $txBlock has not been scanned yet." }
}

# ---- 4. Deposits ------------------------------------------------------------------------------------------------
Section "4. Deposits$(if ($Address) { " to $Address" } else { ' (latest 10)' })"
$top = if ($Address) { '' } else { 'TOP 10' }
$where = if ($Address) { 'WHERE Address = @a' } else { '' }
$deps = Query "SELECT $top Address, Status, Amount, Fee, Confirmations, BlockNumber, LEFT(TransactionHash, 16) AS TxHash, DetectedAt, ConfirmedAt, FinalizedAt FROM deposit.Deposit $where ORDER BY CreatedAt DESC" @{ '@a' = "$Address" }
Show $deps
if ($Address) {
    $wallet = Query "SELECT WalletType, Status, MerchantId, DepositsReceivedCount FROM wallet.Wallet WHERE Chain = 'Tron' AND Address = @a" @{ '@a' = $Address }
    if ($wallet.Rows.Count -eq 0) { Bad "$Address is NOT one of our wallets. The scanner ignores transfers to it." }
    else { Write-Host '  wallet:'; Show $wallet }
    if ($deps.Rows.Count -eq 0 -and $wallet.Rows.Count -gt 0) { Warn 'Our address, but no deposit recorded. Check the cursor, token and logs above/below.' }
    foreach ($d in $deps.Rows) {
        switch ($d.Status) {
            'Ignored'  { Bad 'Recorded as Ignored: below the minimum deposit.' }
            'Detected' { Warn "Detected, $($d.Confirmations) confirmation(s). If this never grows, the confirmation worker is failing (logs)." }
            'Orphaned' { Bad 'Marked Orphaned: the confirmation worker could not find its block on the chain it reads. Is another host with a different chain writing to this database?' }
        }
    }
}

# ---- 5. Outbox --------------------------------------------------------------------------------------------------
Section '5. Unsent events (outbox)'
foreach ($schema in 'deposit', 'paymentintent') {
    $pending = Query "SELECT COUNT(*) AS Unsent, MIN(CreatedAt) AS OldestUnsent FROM [$schema].OutboxMessage WHERE ProcessedOnUtc IS NULL"
    $row = $pending.Rows[0]
    if ($row.Unsent -gt 0) {
        Bad "$schema outbox: $($row.Unsent) unsent, oldest $($row.OldestUnsent). The relay is not running (Redis lock?) or a handler keeps failing:"
        Show (Query "SELECT TOP 5 RIGHT(LEFT(Type, CHARINDEX(',', Type + ',') - 1), 50) AS Type, CreatedAt, RetryCount, LEFT(Error, 150) AS Error FROM [$schema].OutboxMessage WHERE ProcessedOnUtc IS NULL ORDER BY Seq")
    } else { Ok "$schema outbox: nothing unsent." }
}

# ---- 6. Invoices ------------------------------------------------------------------------------------------------
Section "6. Invoices$(if ($Address) { " on $Address" } else { ' (latest 10)' })"
Show (Query "SELECT $top PublicReference, Status, Kind, ExpectedAmount, AmountMatched, MatchedDepositId, CreatedAt, ExpiresAt, GraceExpiresAt FROM paymentintent.PaymentIntent $where ORDER BY CreatedAt DESC" @{ '@a' = "$Address" })
$conn.Close()

# ---- 7. Logs ----------------------------------------------------------------------------------------------------
Section '7. Host logs (newest stdout files)'
$logDir = Join-Path $Folder 'logs'
$logs = @(Get-ChildItem $logDir -Filter 'stdout*' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3)
if ($logs.Count -eq 0) {
    Warn "No stdout logs under $logDir. Set stdoutLogEnabled=""true"" stdoutLogFile="".\logs\stdout"" in web.config and restart the site."
} else {
    Write-Host "  files: $(($logs | ForEach-Object { "$($_.Name) ($($_.LastWriteTime))" }) -join ', ')"

    # IIS writes one stdout file per worker-process start. If the newest one stopped growing long ago, the process
    # is not running - and with it every background worker (scanner, confirmation, outbox relay).
    $newest = $logs[0]
    $idleMinutes = [int]((Get-Date) - $newest.LastWriteTime).TotalMinutes
    if ($idleMinutes -gt 10) {
        Bad "Newest log was last written $idleMinutes min ago. The host process is probably NOT RUNNING, so no worker is scanning."
        Bad 'IIS starts an in-process app only on an HTTP request and stops it when the app pool idles or recycles.'
    } else { Ok "Newest log written $idleMinutes min ago - the process is running." }

    # Errors only, newest file first. Deliberately narrow: EF's SQL echo mentions "Confirmations" on every query.
    $pattern = 'Deposit scan failed|Deposit confirmation .*failed|Outbox dispatch pass failed|Failed to dispatch outbox|\[(ERR|FTL)\]|\bfail:|\bcrit:|Unhandled exception|Application is shutting down|Application started'
    foreach ($file in $logs) {
        $hits = @(Select-String -Path $file.FullName -Pattern $pattern | Select-Object -Last 15)
        Write-Host "  -- $($file.Name): $(if ($hits.Count) { "last $($hits.Count) matching lines" } else { 'no errors' })"
        foreach ($h in $hits) {
            $line = $h.Line.Trim()
            if ($line.Length -gt 200) { $line = $line.Substring(0, 200) + '...' }
            Write-Host "     $($h.LineNumber): $line"
        }
    }
}
