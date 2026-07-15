<#
.SYNOPSIS
    Signs and sends a request to the merchant API (/api/v1) using the partner's HMAC scheme.

.DESCRIPTION
    DEV HELPER. Computes X-Signature = HMAC-SHA256(hexDecode(SigningSecret), "{X-Timestamp}`n{body}") and
    POSTs with X-Api-Key / X-Timestamp / X-Signature. Defaults match the seeded dev merchant
    (Merchant:DevSeed in appsettings.Development.json), so with the host running you can call it with no
    arguments to hit /api/v1/deposit end to end.

.EXAMPLE
    ./Invoke-MerchantRequest.ps1
    # POST /api/v1/deposit with the default dev merchant + a sample USDT-TRON body.

.EXAMPLE
    ./Invoke-MerchantRequest.ps1 -Path '/api/v1/balance' -Body '{}'

.NOTES
    The host prints its actual port on startup ("Now listening on: http://localhost:NNNNN"); override -BaseUrl
    if yours differs. Do not use real production credentials with this script.
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://localhost:51079',
    [string] $Path = '/api/v1/deposit',
    [string] $Body = '{"paymentMethod":"usdt","transactionId":"dev-tx-001","userId":"dev-user-1","expectedAmount":10.5,"callbackUrl":"https://example.com/callback"}',
    [string] $ApiKey = 'cpe_dev_merchant',
    [string] $SigningSecret = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
)

$ErrorActionPreference = 'Stop'

# Hex-decode the 64-char signing secret to the raw 32-byte HMAC key (matches the server's Convert.FromHexString).
$keyBytes = [byte[]]::new($SigningSecret.Length / 2)
for ($i = 0; $i -lt $keyBytes.Length; $i++) {
    $keyBytes[$i] = [Convert]::ToByte($SigningSecret.Substring($i * 2, 2), 16)
}

$timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
$message = "$timestamp`n$Body"   # `n is LF, matching the server's "{timestamp}\n{body}"

$hmac = [System.Security.Cryptography.HMACSHA256]::new($keyBytes)
try {
    $hash = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($message))
} finally {
    $hmac.Dispose()
}
$signature = ([BitConverter]::ToString($hash) -replace '-', '').ToLower()

$headers = @{
    'X-Api-Key'   = $ApiKey
    'X-Timestamp' = $timestamp
    'X-Signature' = $signature
}

Write-Host "POST $BaseUrl$Path" -ForegroundColor Cyan
Write-Host "  X-Api-Key:   $ApiKey"
Write-Host "  X-Timestamp: $timestamp"
Write-Host "  X-Signature: $signature"
Write-Host "  Body:        $Body"
Write-Host ''

try {
    $response = Invoke-RestMethod -Uri "$BaseUrl$Path" -Method Post -Body $Body `
        -ContentType 'application/json' -Headers $headers
    $response | ConvertTo-Json -Depth 10
} catch {
    Write-Host "Request failed: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ErrorDetails.Message) { Write-Host $_.ErrorDetails.Message }
    exit 1
}
