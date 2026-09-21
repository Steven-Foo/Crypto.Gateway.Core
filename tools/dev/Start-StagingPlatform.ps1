<#
.SYNOPSIS
  Brings the staging platform back after the EC2 box was switched off: services, IIS hosts, and the deposit
  scanner cursor. Safe to run any time; every step is idempotent.

.DESCRIPTION
  Run elevated, or register it once as a startup task with -RegisterStartupTask. In order:

    1. Services     starts SQL Server, Redis, MongoDB and IIS if they are stopped, and waits until each one
                    really answers (SQL accepts a login, Redis answers PING), not just until the service says
                    Running.
    2. Hosts        finds the IIS sites running our three hosts from each site's web.config. A site that is
                    stopped AND not set to auto-start is left alone: someone stopped it on purpose.
    3. IIS tuning   sets each host's app pool to AlwaysRunning, no idle timeout, no scheduled recycle, and turns
                    preload on. Without this IIS stops the gateway after 20 idle minutes, and the deposit scanner,
                    confirmation worker and outbox relay stop with it.
    4. Cursor       with the gateway stopped, decides whether the deposit scanner should catch up or skip ahead:
                      - Catching up never loses a payment. The scanner reads 500 blocks per 10 seconds, so a
                        night (~14,000 blocks) takes ~5 minutes and a weekend (~72,000) ~25 minutes. If the
                        catch-up fits in -MaxCatchUpMinutes (default 60), the cursor is left alone.
                      - Only a longer gap skips ahead, and never past a block where an invoice could have been
                        paid: if an invoice was open when the box went down, or was created since, scanning
                        resumes from just before that time. Every skip is appended to scan-cursor-skips.csv so
                        the range can be rescanned later with Set-ScanCursor.ps1 -Block.
    5. Start        starts the app pools, sends one request to each site so IIS actually launches the app, and
                    reports an app that failed to start (HTTP 500.30).
    6. Verify       waits for the gateway's scanner to move the cursor, and prints the catch-up time.

  Nothing secret is printed. Output is also written to boot-logs\ next to this script. Exits 1 if any step
  failed, so a scheduled task shows the failure.

  ASCII only, saved with a BOM: Windows PowerShell 5.1 misreads a BOM-less UTF-8 script.

.EXAMPLE
  .\Start-StagingPlatform.ps1
.EXAMPLE
  .\Start-StagingPlatform.ps1 -RegisterStartupTask      # run it automatically after every boot
#>
param(
    [int] $MaxCatchUpMinutes = 60,
    [int] $ServiceTimeoutSeconds = 180,
    [switch] $SkipIisTuning,
    [switch] $NoCursorMove,
    [switch] $RegisterStartupTask
)

$ErrorActionPreference = 'Stop'
$ScannerBlocksPerSecond = 500 / 10          # DepositDetectionService.MaxBlocksPerScan per DepositWorkerOptions.ScanInterval
$TronBlocksPerSecond = 1 / 3
$HostDlls = @{
    'CryptoPaymentEngine.Api.MerchantGateway.dll'   = 'Gateway'
    'CryptoPaymentEngine.Api.OperationsApi.dll'     = 'Ops'
    'CryptoPaymentEngine.Api.MerchantPortalApi.dll' = 'Portal'
}
$WarmUpPath = @{
    Gateway = '/pay/00000000-0000-0000-0000-000000000000/info'
    Ops     = '/api/v1/ops/auth/me'
    Portal  = '/api/v1/portal/auth/me'
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host 'Run this from an elevated PowerShell (it starts services and changes IIS).' -ForegroundColor Red; exit 1 }

# ---- One-time: register as a startup task -------------------------------------------------------------------------
if ($RegisterStartupTask) {
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $trigger.Delay = 'PT1M'                                     # let Windows finish starting services first
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -StartWhenAvailable
    Register-ScheduledTask -TaskName 'CPE Staging Boot' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Write-Host "Registered scheduled task 'CPE Staging Boot': runs this script as SYSTEM one minute after every boot." -ForegroundColor Green
    Write-Host "Its output lands in $(Join-Path $PSScriptRoot 'boot-logs'). Run it now with: Start-ScheduledTask 'CPE Staging Boot'"
    exit 0
}

$logDir = Join-Path $PSScriptRoot 'boot-logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
Start-Transcript -Path (Join-Path $logDir ("boot-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))) | Out-Null

$failures = New-Object System.Collections.Generic.List[string]
function Section([string] $title) { Write-Host ''; Write-Host "== $title ==" -ForegroundColor Cyan }
function Bad([string] $text) { Write-Host "  !! $text" -ForegroundColor Red; $failures.Add($text) }
function Warn([string] $text) { Write-Host "  ?? $text" -ForegroundColor Yellow }
function Ok([string] $text) { Write-Host "  ok $text" }

function Test-Port([int] $port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { return $client.ConnectAsync('127.0.0.1', $port).Wait(2000) -and $client.Connected } catch { return $false } finally { $client.Close() }
}
function Wait-Until([scriptblock] $condition, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { if (& $condition) { return $true }; Start-Sleep -Seconds 3 } while ((Get-Date) -lt $deadline)
    return $false
}
function Test-RedisPing {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        if (-not $client.ConnectAsync('127.0.0.1', 6379).Wait(2000)) { return $false }
        $stream = $client.GetStream(); $stream.ReadTimeout = 2000
        $bytes = [Text.Encoding]::ASCII.GetBytes("PING`r`n"); $stream.Write($bytes, 0, $bytes.Length)
        $buffer = New-Object byte[] 64; $read = $stream.Read($buffer, 0, 64)
        $reply = [Text.Encoding]::ASCII.GetString($buffer, 0, $read)
        return ($reply -like '+PONG*' -or $reply -like '-NOAUTH*')     # NOAUTH still proves Redis is serving
    } catch { return $false } finally { $client.Close() }
}

# ---- Config reading (same order the hosts use) -------------------------------------------------------------------
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
function Get-HostLayers([string] $folder, [string] $environment) {
    return @((Read-Json (Join-Path $folder 'appsettings.json')), (Read-Json (Join-Path $folder "appsettings.$environment.json")), (Read-Json (Join-Path $folder 'appsettings.Local.json')))
}

# =================================================================================================================
Section '1. Services'
$serviceGroups = @(
    @{ Label = 'SQL Server'; Filter = { $_.Name -eq 'MSSQLSERVER' -or $_.Name -like 'MSSQL$*' }; Ready = { Test-Port 1433 } },
    @{ Label = 'Redis';      Filter = { $_.Name -match 'redis' };                               Ready = { Test-RedisPing } },
    @{ Label = 'MongoDB';    Filter = { $_.Name -match '^mongo' };                              Ready = { Test-Port 27017 } },
    @{ Label = 'IIS';        Filter = { $_.Name -eq 'WAS' -or $_.Name -eq 'W3SVC' };            Ready = { Test-Port 80 } }
)
foreach ($group in $serviceGroups) {
    $services = @(Get-Service | Where-Object $group.Filter)
    if ($services.Count -eq 0) { Warn "$($group.Label): no Windows service found; checking the port only." }
    foreach ($svc in $services | Sort-Object { if ($_.Name -eq 'WAS') { 0 } else { 1 } }) {
        if ($svc.StartType -eq 'Disabled') { Bad "$($group.Label): service '$($svc.Name)' is DISABLED and will not start."; continue }
        if ($svc.Status -ne 'Running') {
            Write-Host "  starting $($svc.Name)..."
            try { Start-Service -Name $svc.Name; $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds($ServiceTimeoutSeconds)) }
            catch { Bad "$($group.Label): service '$($svc.Name)' did not start: $($_.Exception.Message)"; continue }
        }
        if ($svc.StartType -ne 'Automatic') { Warn "$($group.Label): '$($svc.Name)' start type is $($svc.StartType); set it to Automatic so it survives the next shutdown." }
    }
    if (Wait-Until $group.Ready $ServiceTimeoutSeconds) { Ok "$($group.Label) is answering." }
    else { Bad "$($group.Label) is not answering after $ServiceTimeoutSeconds s." }
}

# =================================================================================================================
Section '2. Hosts'
Import-Module WebAdministration
$hosts = @()
foreach ($site in Get-Website) {
    $folder = [Environment]::ExpandEnvironmentVariables($site.physicalPath)
    $webConfig = Join-Path $folder 'web.config'
    if (-not (Test-Path $webConfig)) { continue }
    [xml] $xml = Get-Content $webConfig -Raw
    $aspNetCore = $xml.SelectSingleNode('//aspNetCore')
    if (-not $aspNetCore) { continue }
    $dll = $HostDlls.Keys | Where-Object { "$($aspNetCore.arguments) $($aspNetCore.processPath)" -like "*$([IO.Path]::GetFileNameWithoutExtension($_))*" } | Select-Object -First 1
    if (-not $dll) { continue }
    $envNode = $xml.SelectSingleNode("//environmentVariable[@name='ASPNETCORE_ENVIRONMENT']")
    $binding = @($site.bindings.Collection | Where-Object { $_.protocol -eq 'http' } | Select-Object -First 1)
    $parts = if ($binding) { $binding[0].bindingInformation.Split(':') } else { @('', '80', '') }
    $hosts += [pscustomobject]@{
        Site = $site.name; Pool = $site.applicationPool; Kind = $HostDlls[$dll]; Folder = $folder
        Environment = if ($envNode) { $envNode.value } else { 'Production' }
        AutoStart = [bool]$site.serverAutoStart; State = $site.state
        Port = [int]$parts[1]; HostHeader = $parts[2]
    }
}
$managed = @()
foreach ($h in $hosts) {
    if ($h.State -eq 'Stopped' -and -not $h.AutoStart) { Write-Host "  -- $($h.Site) ($($h.Kind)) is stopped and not auto-start: left alone."; continue }
    Write-Host "  $($h.Site): $($h.Kind), environment $($h.Environment), pool '$($h.Pool)', http://$($h.HostHeader):$($h.Port)"
    $managed += $h
}
$gateways = @($managed | Where-Object Kind -eq 'Gateway')
if ($managed.Count -eq 0) { Bad 'No running CryptoPaymentEngine site found in IIS.' }
if ($gateways.Count -gt 1) { Bad "More than one gateway site is set to run ($($gateways.Site -join ', ')). Two gateways on one database fight over the scan cursor and the outbox locks." }
$sharedPools = $managed | Group-Object Pool | Where-Object Count -gt 1
foreach ($p in $sharedPools) { Bad "App pool '$($p.Name)' is shared by $($p.Group.Site -join ', '). In-process ASP.NET Core needs one app per pool." }

# =================================================================================================================
Section '3. IIS always-running settings'
if ($SkipIisTuning) { Warn 'Skipped (-SkipIisTuning).' }
else {
    foreach ($h in $managed) {
        $poolPath = "IIS:\AppPools\$($h.Pool)"; $changes = @()
        if ((Get-ItemProperty $poolPath -Name startMode) -ne 'AlwaysRunning') { Set-ItemProperty $poolPath -Name startMode -Value 'AlwaysRunning'; $changes += 'startMode=AlwaysRunning' }
        if ((Get-ItemProperty $poolPath -Name processModel.idleTimeout.value) -ne [TimeSpan]::Zero) { Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero); $changes += 'idleTimeout=0' }
        if ((Get-ItemProperty $poolPath -Name recycling.periodicRestart.time.value) -ne [TimeSpan]::Zero) { Set-ItemProperty $poolPath -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero); $changes += 'periodicRestart=0' }
        if (-not (Get-ItemProperty "IIS:\Sites\$($h.Site)" -Name applicationDefaults.preloadEnabled)) { Set-ItemProperty "IIS:\Sites\$($h.Site)" -Name applicationDefaults.preloadEnabled -Value $true; $changes += 'preloadEnabled=true' }
        if ($changes) { Ok "$($h.Site): set $($changes -join ', ')" } else { Ok "$($h.Site): already always-running." }
    }
    if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
        if (-not (Get-WindowsFeature Web-AppInit).Installed) { Warn 'IIS Application Initialization is not installed, so preload does nothing. Install once: Install-WindowsFeature Web-AppInit' }
    }
}

# =================================================================================================================
Section '4. Deposit scanner cursor'
$gateway = $gateways | Select-Object -First 1
$cursorTarget = $null; $tip = $null; $conn = $null; $rpcBase = $null; $headers = @{}
if (-not $gateway) { Warn 'No gateway site to manage.' }
else {
    $layers = Get-HostLayers $gateway.Folder $gateway.Environment
    $connectionString = Get-Setting $layers 'Db:ConnectionString'
    $rpcBase = (Get-Setting $layers 'Chains:Tron:RpcBaseUrl'); if (-not $rpcBase) { $rpcBase = 'https://api.trongrid.io' }; $rpcBase = $rpcBase.TrimEnd('/')
    $apiKey = Get-Setting $layers 'Chains:Tron:ApiKey'; if ($apiKey) { $headers['TRON-PRO-API-KEY'] = $apiKey }

    # SQL being on the port is not SQL being ready: the database comes online after the listener.
    $conn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $sqlReady = Wait-Until { try { if ($conn.State -ne 'Open') { $conn.Open() }; $true } catch { $false } } $ServiceTimeoutSeconds
    if (-not $sqlReady) { Bad "The gateway's database does not accept its login after $ServiceTimeoutSeconds s."; $conn = $null }
    else { Ok 'Gateway database accepts its login.' }
}

function Invoke-Tron([string] $method, [object[]] $params) {
    $body = @{ jsonrpc = '2.0'; id = 1; method = $method; params = $params } | ConvertTo-Json -Depth 6 -Compress
    $r = Invoke-RestMethod -Uri "$rpcBase/jsonrpc" -Method Post -Body $body -ContentType 'application/json' -Headers $headers -TimeoutSec 20
    if ($r.error) { throw "$method failed: $($r.error | ConvertTo-Json -Compress)" }
    return $r.result
}
function HexToLong([string] $hex) { return [Convert]::ToInt64($hex.Substring(2), 16) }
function Get-BlockTime([long] $n) { return [DateTimeOffset]::FromUnixTimeSeconds((HexToLong (Invoke-Tron 'eth_getBlockByNumber' @(('0x{0:x}' -f $n), $false)).timestamp)) }
# Highest block whose timestamp is at or before $when (TRON block timestamps are seconds).
function Find-BlockAtOrBefore([DateTimeOffset] $when, [long] $low, [long] $high) {
    while ($low -lt $high) {
        $mid = [long][math]::Ceiling(($low + $high) / 2)
        if ((Get-BlockTime $mid) -le $when) { $low = $mid } else { $high = $mid - 1 }
    }
    return $low
}
function Query([string] $sql, [hashtable] $params = @{}) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql
    foreach ($k in $params.Keys) { [void]$cmd.Parameters.AddWithValue($k, $params[$k]) }
    $t = New-Object System.Data.DataTable; $t.Load($cmd.ExecuteReader()); return ,$t
}

if ($gateway -and $conn) {
    # Stop the gateway first: a scan pass running during the decision could write its own cursor over ours.
    if ((Get-WebAppPoolState -Name $gateway.Pool).Value -ne 'Stopped') {
        Stop-WebAppPool -Name $gateway.Pool
        [void](Wait-Until { (Get-WebAppPoolState -Name $gateway.Pool).Value -eq 'Stopped' } 60)
    }
    try {
        $tip = HexToLong (Invoke-Tron 'eth_blockNumber' @())
        $row = (Query "SELECT LastScannedBlock, UpdatedAt FROM deposit.ScanCursor WHERE Chain = 'Tron'").Rows | Select-Object -First 1
        if (-not $row) { Ok 'No cursor yet: the scanner starts at the tip on its own.' }
        else {
            $current = [long]$row.LastScannedBlock; $stoppedAt = [DateTimeOffset]$row.UpdatedAt
            $gap = $tip - $current
            $catchUpMinutes = [math]::Ceiling($gap / ($ScannerBlocksPerSecond - $TronBlocksPerSecond) / 60)
            Write-Host "  tip $tip, cursor $current (last moved $stoppedAt), $gap blocks behind, catch-up ~$catchUpMinutes min"

            if ($gap -lt 0) { Bad "Cursor is AHEAD of the chain tip by $(-$gap) blocks: it was written against another chain. Fix it with Set-ScanCursor.ps1 -Block <tip - 20>." }
            elseif ($NoCursorMove) { Warn 'Left alone (-NoCursorMove).' }
            elseif ($catchUpMinutes -le $MaxCatchUpMinutes) { Ok "Within $MaxCatchUpMinutes min: the scanner catches up on its own and no payment is skipped." }
            else {
                # How far back must scanning resume? From the moment the box went down if any invoice was still
                # payable then; otherwise from the first invoice created since; otherwise nothing could have been paid.
                $invoices = Query "SELECT MIN(CASE WHEN CreatedAt < @s THEN @s ELSE CreatedAt END) AS PayableFrom, COUNT(*) AS N FROM paymentintent.PaymentIntent WHERE GraceExpiresAt >= @s OR CreatedAt >= @s" @{ '@s' = $stoppedAt }
                $inv = $invoices.Rows[0]
                if ($inv.N -eq 0) {
                    $cursorTarget = $tip - 20
                    $reason = 'no invoice was payable during the downtime'
                } else {
                    $from = ([DateTimeOffset]$inv.PayableFrom).AddMinutes(-5)
                    $cursorTarget = [math]::Max($current, (Find-BlockAtOrBefore $from $current $tip) - 1)
                    $reason = "$($inv.N) invoice(s) were payable from $($inv.PayableFrom)"
                }
                if ($cursorTarget -le $current) {
                    $cursorTarget = $null
                    Ok "Not skipping: $reason, which is where the cursor already is. Catch-up takes ~$catchUpMinutes min."
                } else {
                    $cmd = $conn.CreateCommand()
                    $cmd.CommandText = "UPDATE deposit.ScanCursor SET LastScannedBlock = @b, UpdatedAt = SYSDATETIMEOFFSET() WHERE Chain = 'Tron'"
                    [void]$cmd.Parameters.AddWithValue('@b', [long]$cursorTarget); [void]$cmd.ExecuteNonQuery()
                    $skipLog = Join-Path $PSScriptRoot 'scan-cursor-skips.csv'
                    if (-not (Test-Path $skipLog)) { 'SkippedAtUtc,FromBlock,ToBlock,Reason' | Set-Content $skipLog }
                    '{0:u},{1},{2},"{3}"' -f (Get-Date).ToUniversalTime(), ($current + 1), $cursorTarget, $reason | Add-Content $skipLog
                    Warn "Skipped blocks $($current + 1)..$cursorTarget ($reason). A transfer there with no open invoice is not detected."
                    Warn "Recorded in $skipLog. To rescan later: Set-ScanCursor.ps1 -Folder '$($gateway.Folder)' -Block $current -Force"
                    Ok "Cursor $current -> $cursorTarget; catch-up now ~$([math]::Ceiling(($tip - $cursorTarget) / ($ScannerBlocksPerSecond - $TronBlocksPerSecond) / 60)) min."
                }
            }
        }
    } catch { Bad "Cursor check failed: $($_.Exception.Message)" }
}

# =================================================================================================================
Section '5. Starting the hosts'
function Send-WarmUp($h) {
    $url = "http://127.0.0.1:$($h.Port)$($WarmUpPath[$h.Kind])"
    $request = [Net.HttpWebRequest]::Create($url)
    if ($h.HostHeader) { $request.Host = $h.HostHeader }
    $request.Timeout = 120000
    try { $response = $request.GetResponse(); $code = [int]$response.StatusCode; $response.Close(); return @{ Code = $code; Body = '' } }
    catch [Net.WebException] {
        if (-not $_.Exception.Response) { return @{ Code = 0; Body = $_.Exception.Message } }
        $response = $_.Exception.Response; $code = [int]$response.StatusCode
        $body = (New-Object IO.StreamReader $response.GetResponseStream()).ReadToEnd(); $response.Close()
        return @{ Code = $code; Body = $body }
    }
}
foreach ($h in $managed) {
    if ((Get-WebAppPoolState -Name $h.Pool).Value -ne 'Started') { Start-WebAppPool -Name $h.Pool }
    if ((Get-WebsiteState -Name $h.Site).Value -ne 'Started') { Start-Website -Name $h.Site }
    $result = Send-WarmUp $h
    if ($result.Code -ge 200 -and $result.Code -lt 500) { Ok "$($h.Site) is up (HTTP $($result.Code) on the warm-up request)." }
    elseif ($result.Body -match '500\.3\d|failed to start') {
        Bad "$($h.Site) FAILED TO START (HTTP $($result.Code)). The reason is in the newest file under $(Join-Path $h.Folder 'logs')."
    } else { Bad "$($h.Site) did not answer correctly: HTTP $($result.Code) $($result.Body.Substring(0, [math]::Min(150, $result.Body.Length)))" }
}

# =================================================================================================================
Section '6. Verifying the deposit scanner'
if ($gateway -and $conn) {
    try {
        $before = [long](Query "SELECT LastScannedBlock FROM deposit.ScanCursor WHERE Chain = 'Tron'").Rows[0].LastScannedBlock
        $moved = Wait-Until { [long](Query "SELECT LastScannedBlock FROM deposit.ScanCursor WHERE Chain = 'Tron'").Rows[0].LastScannedBlock -gt $before } 120
        if ($moved) {
            $now = [long](Query "SELECT LastScannedBlock FROM deposit.ScanCursor WHERE Chain = 'Tron'").Rows[0].LastScannedBlock
            $left = $tip - $now
            Ok "Scanner is running ($before -> $now). $(if ($left -gt 100) { "~$([math]::Ceiling($left / ($ScannerBlocksPerSecond - $TronBlocksPerSecond) / 60)) min until it reaches the tip." } else { 'At the tip.' })"
        } else { Bad "The scanner did not move the cursor within 120 s. Run Get-DepositDiagnosis.ps1 -Folder '$($gateway.Folder)'." }
    } catch { Bad "Could not verify the scanner: $($_.Exception.Message)" }
    $conn.Close()
}

# =================================================================================================================
Section 'Result'
if ($failures.Count -eq 0) { Write-Host '  Staging is up.' -ForegroundColor Green }
else { Write-Host "  $($failures.Count) problem(s):" -ForegroundColor Red; $failures | ForEach-Object { Write-Host "   - $_" -ForegroundColor Red } }
Stop-Transcript | Out-Null
exit $(if ($failures.Count -eq 0) { 0 } else { 1 })
