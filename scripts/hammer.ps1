<#
.SYNOPSIS
    Fires N rapid POST requests at the ingestion API, independent of the UI's "Fire 100" button -
    a from-outside check that the backend (not just the browser) holds up under a burst.
.PARAMETER BaseUrl
    Root URL of the API (or the nginx LB in distributed mode).
.PARAMETER Count
    Number of transactions to fire.
#>
param(
    [string]$BaseUrl = "http://localhost:5080",
    [int]$Count = 100
)

$statuses = @("Pending", "Completed", "Failed")
$jobs = 1..$Count | ForEach-Object {
    $body = @{
        amount   = [math]::Round((Get-Random -Minimum 1 -Maximum 10000) / 100, 2)
        currency = "USD"
        status   = $statuses[(Get-Random -Maximum 3)]
    } | ConvertTo-Json

    Start-ThreadJob -ScriptBlock {
        param($url, $payload)
        try {
            $response = Invoke-WebRequest -Uri $url -Method Post -Body $payload -ContentType "application/json" -UseBasicParsing
            [int]$response.StatusCode
        } catch {
            -1
        }
    } -ArgumentList "$BaseUrl/api/transactions", $body
}

$started = Get-Date
$results = $jobs | Receive-Job -Wait -AutoRemoveJob
$elapsedMs = ((Get-Date) - $started).TotalMilliseconds

$succeeded = ($results | Where-Object { $_ -eq 201 }).Count
Write-Host "Fired $Count transactions: $succeeded/$Count succeeded (HTTP 201) in $([math]::Round($elapsedMs))ms"
