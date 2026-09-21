<#
.SYNOPSIS
    Exercises the MistTrack address-screening integration against the live API, and measures what a
    screening actually costs against the daily quota.

.DESCRIPTION
    ASCII-only on purpose. Windows PowerShell 5.1 reads a file with no byte-order mark as ANSI, which
    turns a UTF-8 em dash into a character it accepts as a STRING DELIMITER - the script then fails to
    parse with a misleading "string is missing the terminator". The file also carries a BOM; keeping it
    ASCII means it survives even if the BOM is ever stripped by an editor or a merge.

    Three modes:

    1. Entitlement (-Mode Entitlement). Answers whether /v3/risk_score is included in the plan at all.
       ANSWERED 2026-09-11: yes, it is. Kept so the check can be repeated after any plan change.
       Costs 2 calls.

    2. Quota (-Mode Quota). Measures how much ONE risk_score call consumes.

       There is no way to read this from the API. Verified 2026-09-11: the response carries no rate-limit
       or quota headers, and there is no usage endpoint (v1/quota, v1/usage, v1/user_info, v1/account,
       v1/api_quota, v1/remaining_quota and v1/balance all return "PageNotFound"). The dashboard is the
       only source, so this makes exactly ONE metered call with a clear before-and-after so the reading
       is unambiguous.

       Only risk_score is measured, because it is the only endpoint the gateway calls. The pay-as-you-go
       price list charges ten times more for it than for address_labels, which is the reason to suspect
       the subscription might weight them too.

    3. Sandbox (-Mode Sandbox). KNOWN BROKEN with a production key: sandbox-api.misttrack.io returns
       HTTP 400 with an empty body for every request while openapi.misttrack.io succeeds with the same
       key. Either the sandbox needs its own separately issued key or it is not part of the Standard
       plan. Left in place so the behaviour is diagnosed rather than rediscovered.

    The API key is read from the environment, from appsettings.Local.json, or passed in. It is never
    written to disk and never echoed (section 10).

.PARAMETER ApiKey
    Defaults to $env:MISTTRACK_API_KEY, then to Compliance:MistTrack:ApiKey in the OperationsApi
    appsettings.Local.json (which is git-ignored).

.PARAMETER Mode
    Entitlement (default), Quota, or Sandbox.

.PARAMETER DailyQuota
    Your plan's daily call allowance, used to turn a measured weight into a screenings-per-day figure.
    Standard is 10000.

.EXAMPLE
    ./tools/dev/Test-AddressScreening.ps1 -Mode Quota
#>
[CmdletBinding()]
param(
    [string]$ApiKey,
    [ValidateSet('Entitlement', 'Quota', 'Sandbox')]
    [string]$Mode = 'Entitlement',
    [int]$DailyQuota = 10000
)

$ErrorActionPreference = 'Stop'

function Resolve-ApiKey {
    param([string]$Supplied)

    if (-not [string]::IsNullOrWhiteSpace($Supplied)) { return $Supplied }
    if (-not [string]::IsNullOrWhiteSpace($env:MISTTRACK_API_KEY)) { return $env:MISTTRACK_API_KEY }

    $local = Join-Path $PSScriptRoot '..\..\src\Api\OperationsApi\CryptoPaymentEngine.Api.OperationsApi\appsettings.Local.json'
    if (Test-Path $local) {
        try {
            $cfg = Get-Content $local -Raw | ConvertFrom-Json
            if ($cfg.Compliance -and $cfg.Compliance.MistTrack -and -not [string]::IsNullOrWhiteSpace($cfg.Compliance.MistTrack.ApiKey)) {
                return $cfg.Compliance.MistTrack.ApiKey
            }
        }
        catch { }
    }

    return $null
}

$ApiKey = Resolve-ApiKey -Supplied $ApiKey

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Host "No API key. Set `$env:MISTTRACK_API_KEY, pass -ApiKey, or put it in the git-ignored" -ForegroundColor Red
    Write-Host "appsettings.Local.json under Compliance:MistTrack:ApiKey." -ForegroundColor Red
    exit 1
}

$baseUrl = if ($Mode -eq 'Sandbox') { 'https://sandbox-api.misttrack.io' } else { 'https://openapi.misttrack.io' }

# A real, widely used TRX address. Deliberately one whose live response is already understood: it scores 3
# out of 100 yet carries an INDIRECT sanctioned_entity entry three hops out, which is the case that proves
# indirect exposure must never force a Block.
$probe = 'TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e'

function Invoke-MistTrack {
    param([string]$Path, [hashtable]$Query)

    $pairs = @("api_key=$([uri]::EscapeDataString($ApiKey))")
    foreach ($k in $Query.Keys) { $pairs += "$k=$([uri]::EscapeDataString([string]$Query[$k]))" }
    $uri = "$baseUrl/$Path`?$($pairs -join '&')"

    try {
        # MistTrack reports business failures with HTTP 200 and success:false, so the caller must inspect
        # the body rather than trusting the status code.
        return Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec 20
    }
    catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        return [pscustomobject]@{ success = $false; msg = "HTTP $status $($_.Exception.Message)" }
    }
}

Write-Host "MistTrack $Mode  ->  $baseUrl" -ForegroundColor Cyan
Write-Host ""

# ---- Sandbox -----------------------------------------------------------------------------------------
if ($Mode -eq 'Sandbox') {
    $r = Invoke-MistTrack -Path 'v3/risk_score' -Query @{ coin = 'TRX'; address = $probe }

    if ($r.success) {
        Write-Host "  Sandbox answered. It is working again - update the docs, which record it as broken." -ForegroundColor Green
        exit 0
    }

    Write-Host "  Sandbox refused: $($r.msg)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Expected with a production key. Verified 2026-09-11: the sandbox host returns HTTP 400" -ForegroundColor Gray
    Write-Host "  with an empty body while the live host succeeds with the SAME key. Either the sandbox" -ForegroundColor Gray
    Write-Host "  needs its own separately issued key, or it is not part of the Standard plan. Worth asking" -ForegroundColor Gray
    Write-Host "  the vendor. Until then, screen locally with the in-memory provider (leave the key unset)." -ForegroundColor Gray
    exit 1
}

# ---- Entitlement -------------------------------------------------------------------------------------
if ($Mode -eq 'Entitlement') {
    Write-Host "This spends 2 calls of the daily quota." -ForegroundColor Yellow
    Write-Host ""

    Write-Host "1. address_labels" -ForegroundColor Cyan
    $labels = Invoke-MistTrack -Path 'v1/address_labels' -Query @{ coin = 'TRX'; address = $probe }
    if ($labels.success) {
        Write-Host "   OK - available on this plan." -ForegroundColor Green
    }
    else {
        Write-Host "   Refused: $($labels.msg)" -ForegroundColor Red
    }

    Start-Sleep -Seconds 2

    Write-Host ""
    Write-Host "2. risk_score - the endpoint the gateway actually depends on" -ForegroundColor Cyan
    $risk = Invoke-MistTrack -Path 'v3/risk_score' -Query @{ coin = 'TRX'; address = $probe }
    if ($risk.success) {
        Write-Host "   OK - included in this plan. Score $($risk.data.score), level $($risk.data.risk_level)." -ForegroundColor Green
    }
    else {
        Write-Host "   Refused: $($risk.msg)" -ForegroundColor Red
        Write-Host "   If this says the plan does not cover it, risk scoring sits above Standard and the" -ForegroundColor Yellow
        Write-Host "   integration plan needs revisiting before any payout path depends on it." -ForegroundColor Yellow
    }

    exit $(if ($risk.success) { 0 } else { 1 })
}

# ---- Quota -------------------------------------------------------------------------------------------
Write-Host "QUOTA MEASUREMENT" -ForegroundColor Yellow
Write-Host ""
Write-Host "  There is no quota endpoint and no rate-limit header - the dashboard is the only source." -ForegroundColor Gray
Write-Host "  Open it now and note the REMAINING daily quota before continuing." -ForegroundColor Gray
Write-Host ""
Write-Host "  Press Enter when you have the number written down." -ForegroundColor Cyan
[void](Read-Host)

Write-Host ""
Write-Host "Making exactly ONE risk_score call..." -ForegroundColor Cyan
$before = Get-Date
$r = Invoke-MistTrack -Path 'v3/risk_score' -Query @{ coin = 'TRX'; address = $probe }
$elapsed = [int]((Get-Date) - $before).TotalMilliseconds

if (-not $r.success) {
    Write-Host "  Call failed: $($r.msg)" -ForegroundColor Red
    Write-Host "  Nothing was measured. Quota may or may not have been consumed." -ForegroundColor Yellow
    exit 1
}

Write-Host "  OK - score $($r.data.score), level $($r.data.risk_level), ${elapsed}ms." -ForegroundColor Green
Write-Host ""
Write-Host "  Now refresh the dashboard and read the remaining quota again." -ForegroundColor Cyan
$delta = Read-Host "  How much did it drop by"

$weight = 0
if (-not [int]::TryParse($delta, [ref]$weight) -or $weight -le 0) {
    Write-Host "  Not a number. Re-read the dashboard and run this again." -ForegroundColor Yellow
    exit 1
}

$perDay = [math]::Floor($DailyQuota / $weight)
$perSecondCap = 86400          # the 1 call/sec limit, as a hard ceiling on calls per day
$effective = [math]::Min($perDay, $perSecondCap)

Write-Host ""
Write-Host "RESULT" -ForegroundColor Cyan
Write-Host "  One risk_score call costs      : $weight unit(s)" -ForegroundColor White
Write-Host "  Daily quota                    : $DailyQuota units" -ForegroundColor White
Write-Host "  Screenings per day (quota)     : $perDay" -ForegroundColor White
Write-Host "  Screenings per day (1/sec cap) : $perSecondCap" -ForegroundColor White
Write-Host "  Binding limit                  : $effective per day" -ForegroundColor Green
Write-Host ""

if ($weight -eq 1) {
    Write-Host "  Flat: every endpoint costs one unit." -ForegroundColor Gray
}
else {
    Write-Host "  WEIGHTED. Capacity is $perDay screenings/day, not $DailyQuota. Re-check the caching" -ForegroundColor Yellow
    Write-Host "  assumptions in docs/address-screening.md before enabling screening broadly." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "  Record this in docs/address-screening.md - it is the number every capacity" -ForegroundColor Gray
Write-Host "  decision about screening depends on." -ForegroundColor Gray
