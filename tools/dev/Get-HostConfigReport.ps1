<#
.SYNOPSIS
  Reports the EFFECTIVE configuration of each deployed CryptoPaymentEngine host, with secrets masked.

.DESCRIPTION
  Run on the server (elevated, for IIS access). For every IIS site whose folder holds one of our host DLLs,
  or for the folders passed with -Path, it:
    1. works out which ASPNETCORE_ENVIRONMENT the app really runs as (web.config, then machine/user env vars;
       unset means Production, which is ASP.NET Core's default),
    2. lists which appsettings*.json files are actually deployed,
    3. merges them in the order the host loads them:
         appsettings.json -> appsettings.{Environment}.json -> environment variables -> appsettings.Local.json
       (our Program.cs adds Local.json AFTER the builder's defaults, so Local.json beats env vars),
    4. prints the settings the deposit flow depends on, plus a fingerprint of the shared merchant secrets so
       you can check the hosts agree WITHOUT revealing them.

  Secret VALUES are never printed. Anything whose key looks like a password, secret, key, pepper or
  connection string is shown as <set, N chars, fp XXXXXXXX> (fp = first 8 hex chars of its SHA-256), so two
  hosts with the same secret show the same fp. Safe to paste the output into a chat.

  ASCII only, saved with a BOM: Windows PowerShell 5.1 misreads a BOM-less UTF-8 script.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Get-HostConfigReport.ps1
.EXAMPLE
  .\Get-HostConfigReport.ps1 -Path 'C:\inetpub\gateway','C:\inetpub\opsapi'
#>
param(
    [string[]] $Path
)

$ErrorActionPreference = 'Stop'

$HostDlls = @{
    'CryptoPaymentEngine.Api.MerchantGateway.dll'   = 'MerchantGateway (deposits, scanner, workers)'
    'CryptoPaymentEngine.Api.OperationsApi.dll'     = 'OperationsApi (admin back-office)'
    'CryptoPaymentEngine.Api.MerchantPortalApi.dll' = 'MerchantPortalApi (merchant portal)'
}

$SecretPattern = '(?i)(password|secret|apikey|api_key|pepper|connectionstring|keys:|token|xpub|privatekey)'

# Keys worth showing, per purpose. Prefix match, case-insensitive.
$Interesting = @(
    'Chains:Tron:', 'Withdrawal:LiveTron', 'Deposit:Policies:Tron', 'Blockchain:Assets',
    'Gateway:BaseUrl', 'PaymentIntent:', 'Redis:', 'Db:', 'Mongo:',
    'Merchant:ApiCredentials', 'Merchant:SigningSecrets', 'Merchant:DevSeed:Enabled',
    'MerchantIdentity:DevSeed:Enabled', 'StaffAuth:DevSeed:Enabled', 'DevSampleData:Enabled',
    'KeyManagement:Kms:Enabled', 'Cors:AllowedOrigins', 'Compliance:Enabled'
)

function Get-Fingerprint([string] $value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($value))
        return (($bytes[0..3] | ForEach-Object { $_.ToString('x2') }) -join '')
    } finally { $sha.Dispose() }
}

function Format-Value([string] $key, $value) {
    if ($null -eq $value) { return '<null>' }
    $text = [string] $value
    if ($key -match $SecretPattern -and $key -notmatch '(?i)(CurrentHashVersion|CurrentKeyVersion|__comment)') {
        if ($text.Length -eq 0) { return '<empty>' }
        return "<set, $($text.Length) chars, fp $(Get-Fingerprint $text)>"
    }
    return $text
}

# Flattens a ConvertFrom-Json object into "A:B:0:C" keys, the same shape .NET configuration uses.
function Add-Flattened($node, [string] $prefix, [hashtable] $into) {
    if ($null -eq $node) { $into[$prefix] = $null; return }
    if ($node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($p in $node.PSObject.Properties) {
            $k = if ($prefix) { "$prefix`:$($p.Name)" } else { $p.Name }
            Add-Flattened $p.Value $k $into
        }
    } elseif ($node -is [System.Array]) {
        for ($i = 0; $i -lt $node.Count; $i++) { Add-Flattened $node[$i] "$prefix`:$i" $into }
    } else {
        $into[$prefix] = $node
    }
}

function Read-JsonFlat([string] $file) {
    $flat = @{}
    $raw = [System.IO.File]::ReadAllText($file)          # strips a UTF-8 BOM
    # .NET config tolerates // comments and trailing commas; ConvertFrom-Json in 5.1 does not.
    $raw = [regex]::Replace($raw, '(?m)^\s*//.*$', '')
    $raw = [regex]::Replace($raw, ',(\s*[}\]])', '$1')
    try {
        Add-Flattened ($raw | ConvertFrom-Json) '' $flat
    } catch {
        Write-Host "    !! $([System.IO.Path]::GetFileName($file)) is NOT valid JSON: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "       The host fails to start, or ignores it, when this happens." -ForegroundColor Red
    }
    return $flat
}

function Get-SiteTargets {
    $targets = @()
    try {
        Import-Module WebAdministration -ErrorAction Stop
        foreach ($site in Get-Website) {
            $folders = @([Environment]::ExpandEnvironmentVariables($site.physicalPath))
            foreach ($app in Get-WebApplication -Site $site.name) {
                $folders += [Environment]::ExpandEnvironmentVariables($app.PhysicalPath)
            }
            foreach ($f in $folders | Select-Object -Unique) {
                $targets += [pscustomobject]@{ Site = $site.name; State = $site.state; Folder = $f; Bindings = ($site.bindings.Collection | ForEach-Object { $_.bindingInformation }) -join ', ' }
            }
        }
    } catch {
        Write-Host "IIS module not available ($($_.Exception.Message)). Pass -Path with the deployed folders." -ForegroundColor Yellow
    }
    return $targets
}

function Get-WebConfigEnvironment([string] $folder) {
    $vars = @{}
    $webConfig = Join-Path $folder 'web.config'
    if (-not (Test-Path $webConfig)) { return $vars }
    [xml] $xml = Get-Content $webConfig -Raw
    foreach ($n in $xml.SelectNodes('//aspNetCore/environmentVariables/environmentVariable')) {
        $vars[$n.name] = $n.value
    }
    return $vars
}

$targets = @()
if ($Path) {
    $targets = $Path | ForEach-Object { [pscustomobject]@{ Site = '(path)'; State = ''; Folder = $_; Bindings = '' } }
} else {
    $targets = Get-SiteTargets
}

$fingerprints = @()

foreach ($t in $targets) {
    if (-not (Test-Path $t.Folder)) { continue }

    # The DLL IIS actually starts is named in web.config's <aspNetCore arguments>. A folder can hold more than
    # one host DLL (a publish copied over another), so guessing from which DLL files exist mislabels the site.
    $present = @($HostDlls.Keys | Where-Object { Test-Path (Join-Path $t.Folder $_) })
    if ($present.Count -eq 0) { continue }
    $dll = $null
    $webConfigPath = Join-Path $t.Folder 'web.config'
    if (Test-Path $webConfigPath) {
        [xml] $wc = Get-Content $webConfigPath -Raw
        $aspNetCore = $wc.SelectSingleNode('//aspNetCore')
        if ($aspNetCore) {
            $launch = "$($aspNetCore.processPath) $($aspNetCore.arguments)"
            $dll = $HostDlls.Keys | Where-Object { $launch -like "*$_*" -or $launch -like "*$([IO.Path]::GetFileNameWithoutExtension($_)).exe*" } | Select-Object -First 1
        }
    }
    if (-not $dll) { $dll = $present[0] }

    Write-Host ''
    Write-Host ('=' * 100)
    Write-Host "$($HostDlls[$dll])"
    Write-Host "  IIS site : $($t.Site) [$($t.State)]  bindings: $($t.Bindings)"
    Write-Host "  Folder   : $($t.Folder)"
    $dllInfo = Get-Item (Join-Path $t.Folder $dll)
    Write-Host "  Build    : $($dll) last written $($dllInfo.LastWriteTime)"
    if ($present.Count -gt 1) {
        Write-Host "    !! This folder holds more than one host DLL: $($present -join ', ')." -ForegroundColor Red
        Write-Host "       web.config decides which one runs. If it is ever pointed at the other, that host runs with this site's config." -ForegroundColor Red
    }

    # --- Environment: web.config wins over machine/user env vars; unset = Production -----------------------
    $webEnv = Get-WebConfigEnvironment $t.Folder
    $envName = $null; $envSource = $null
    if ($webEnv.ContainsKey('ASPNETCORE_ENVIRONMENT')) { $envName = $webEnv['ASPNETCORE_ENVIRONMENT']; $envSource = 'web.config' }
    elseif ([Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Machine')) { $envName = [Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Machine'); $envSource = 'machine environment variable' }
    elseif ($webEnv.ContainsKey('DOTNET_ENVIRONMENT')) { $envName = $webEnv['DOTNET_ENVIRONMENT']; $envSource = 'web.config (DOTNET_ENVIRONMENT)' }
    else { $envName = 'Production'; $envSource = 'NOT SET - ASP.NET Core defaults to Production' }
    Write-Host "  Environment: $envName   (from $envSource)" -ForegroundColor Cyan

    # --- Which files are deployed ------------------------------------------------------------------------
    $files = Get-ChildItem $t.Folder -Filter 'appsettings*.json' | Select-Object -ExpandProperty Name
    Write-Host "  Deployed config files: $(if ($files) { $files -join ', ' } else { 'NONE' })"
    $envFile = "appsettings.$envName.json"
    if ($files -notcontains $envFile) {
        Write-Host "    !! $envFile is not deployed, so nothing environment-specific is loaded." -ForegroundColor Yellow
    }

    # --- Merge in load order ------------------------------------------------------------------------------
    $effective = @{}; $origin = @{}
    $layers = @(
        @{ Name = 'appsettings.json'; File = 'appsettings.json' },
        @{ Name = $envFile; File = $envFile }
    )
    foreach ($layer in $layers) {
        $f = Join-Path $t.Folder $layer.File
        if (Test-Path $f) {
            $flat = Read-JsonFlat $f
            foreach ($k in $flat.Keys) { $effective[$k] = $flat[$k]; $origin[$k] = $layer.Name }
        }
    }
    # Environment variables: web.config <environmentVariables> and machine-level, "A__B" = "A:B".
    $envVars = @{}
    foreach ($e in [Environment]::GetEnvironmentVariables('Machine').GetEnumerator()) { $envVars[$e.Key] = $e.Value }
    foreach ($k in $webEnv.Keys) { $envVars[$k] = $webEnv[$k] }
    foreach ($k in $envVars.Keys) {
        if ($k -notlike '*__*') { continue }
        $ck = $k -replace '__', ':'
        if ($Interesting | Where-Object { $ck -like "$_*" }) { $effective[$ck] = $envVars[$k]; $origin[$ck] = 'env var' }
    }
    $local = Join-Path $t.Folder 'appsettings.Local.json'
    if (Test-Path $local) {
        $flat = Read-JsonFlat $local
        foreach ($k in $flat.Keys) { $effective[$k] = $flat[$k]; $origin[$k] = 'appsettings.Local.json' }
    }

    # --- Report ------------------------------------------------------------------------------------------
    Write-Host '  Effective settings (value  <-  where it came from):'
    $shown = $effective.Keys | Where-Object {
        $k = $_
        ($k -notmatch '__comment') -and ($Interesting | Where-Object { $k -like "$_*" })
    } | Sort-Object
    foreach ($k in $shown) {
        '    {0,-58} {1,-45} <- {2}' -f $k, (Format-Value $k $effective[$k]), $origin[$k] | Write-Host
    }
    foreach ($required in 'Gateway:BaseUrl', 'Chains:Tron:Live', 'Redis:ConnectionString') {
        if ($dll -like '*MerchantGateway*' -and -not $effective.ContainsKey($required)) {
            Write-Host ("    {0,-58} <NOT SET>" -f $required) -ForegroundColor Yellow
        }
    }

    # --- Diagnosis for the deposit host ------------------------------------------------------------------
    if ($dll -like '*MerchantGateway*') {
        Write-Host '  Deposit-flow checks:' -ForegroundColor Cyan
        $testnetTier = $envName -in @('Development', 'Staging')
        $live = "$($effective['Chains:Tron:Live'])" -eq 'True' -or "$($effective['Withdrawal:LiveTron'])" -eq 'True'
        $rpc = if ($effective['Chains:Tron:RpcBaseUrl']) { $effective['Chains:Tron:RpcBaseUrl'] } else { 'https://api.trongrid.io (code default)' }
        if (-not $testnetTier) {
            Write-Host "    !! Runs as $envName, so it takes the REAL chain adapter against $rpc and registers NO dev custody or merchant seed." -ForegroundColor Yellow
        } elseif (-not $live) {
            Write-Host "    !! Chains:Tron:Live is not true, so the host uses the IN-MEMORY FAKE chain. It will never see a real transfer." -ForegroundColor Red
        } else {
            Write-Host "    ok  Real TRON adapter against $rpc"
        }
        $usdt = $effective.Keys | Where-Object { $_ -match '^Blockchain:Assets:\d+:Symbol$' -and $effective[$_] -eq 'USDT' } | ForEach-Object {
            $effective[($_ -replace 'Symbol$', 'ContractAddress')]
        }
        foreach ($c in $usdt) {
            switch ($c) {
                'TXYZopYRdj2D9XRtbG411XZZ3kM5VkAeBf' { Write-Host "    ok  USDT contract is Nile test USDT ($c)" }
                'TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t' { Write-Host "    !! USDT contract is MAINNET ($c). On Nile the scanner never matches a transfer." -ForegroundColor Red }
                default { Write-Host "    ?? USDT contract $c - confirm it is the token the tester sends." -ForegroundColor Yellow }
            }
        }
        if ($rpc -like '*api.trongrid.io*' -and ($usdt -contains 'TXYZopYRdj2D9XRtbG411XZZ3kM5VkAeBf')) {
            Write-Host "    !! RPC is MAINNET but USDT is the Nile contract - these must be the same network." -ForegroundColor Red
        }
        $base = $effective['Gateway:BaseUrl']
        if (-not $base) { Write-Host "    !! Gateway:BaseUrl not set - payUrl is returned without a host." -ForegroundColor Red }
        elseif ($base -match 'localhost|127\.0\.0\.1') { Write-Host "    !! Gateway:BaseUrl is $base - every payUrl points at localhost." -ForegroundColor Red }
        else { Write-Host "    ok  payUrl host: $base" }
        if ("$($effective['Chains:Tron:ApiKey'])".Length -eq 0 -and $live) {
            Write-Host "    ?? No TronGrid API key - the scanner will be throttled." -ForegroundColor Yellow
        }
    }

    $fingerprints += [pscustomobject]@{
        Host      = "$($HostDlls[$dll].Split(' ')[0]) [$($t.Site)]"
        Running   = ($t.State -ne 'Stopped')
        Db        = Format-Value 'Db:ConnectionString' $effective['Db:ConnectionString']
        Pepper    = Format-Value 'pepper' $effective['Merchant:ApiCredentials:Peppers:1']
        SigningKey = Format-Value 'keys:' $effective['Merchant:SigningSecrets:Keys:1']
    }
}

if ($fingerprints.Count -eq 0) {
    Write-Host 'No CryptoPaymentEngine host found. Pass -Path with the deployed folders.' -ForegroundColor Yellow
    return
}

Write-Host ''
Write-Host ('=' * 100)
Write-Host 'Shared secrets across hosts (must be IDENTICAL - compare the fp values):' -ForegroundColor Cyan
$fingerprints | Format-Table -AutoSize | Out-String | Write-Host
# Compare running hosts only, and only values that are set: a stopped site or a host that simply lacks the
# setting is not evidence of a mismatch.
$running = @($fingerprints | Where-Object { $_.Running })
foreach ($col in 'Db', 'Pepper', 'SigningKey') {
    $distinct = @($running | ForEach-Object { $_.$col } | Where-Object { $_ -like '<set*' } | Select-Object -Unique)
    if ($distinct.Count -gt 1) {
        $who = ($running | ForEach-Object { "$($_.Host)=$($_.$col -replace '^<set, \d+ chars, fp ([0-9a-f]+)>$', '$1')" }) -join '; '
        Write-Host "!! $col differs between running hosts: $who" -ForegroundColor Red
    }
}

# --- Box-level dependencies ---------------------------------------------------------------------------------
Write-Host ''
Write-Host 'Local services:' -ForegroundColor Cyan
foreach ($svc in @(@{ Name = 'SQL Server'; Port = 1433 }, @{ Name = 'Redis'; Port = 6379 }, @{ Name = 'MongoDB'; Port = 27017 })) {
    $ok = $false
    try { $c = New-Object System.Net.Sockets.TcpClient; $ok = $c.ConnectAsync('127.0.0.1', $svc.Port).Wait(2000) -and $c.Connected; $c.Close() } catch { }
    '  {0,-11} 127.0.0.1:{1,-6} {2}' -f $svc.Name, $svc.Port, $(if ($ok) { 'listening' } else { 'NOT REACHABLE' }) | Write-Host
}
try {
    $body = '{"jsonrpc":"2.0","id":1,"method":"eth_blockNumber","params":[]}'
    $r = Invoke-RestMethod -Uri 'https://nile.trongrid.io/jsonrpc' -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 10
    Write-Host "  Nile node   reachable, tip block $([Convert]::ToInt64($r.result.Substring(2), 16))"
} catch {
    Write-Host "  Nile node   NOT REACHABLE from this box: $($_.Exception.Message)" -ForegroundColor Red
}
