param(
    [string]$ApiBase = "http://localhost:8080",
    [string]$Secret = "dev_test_webhook_secret_change_me",
    [switch]$Refund
)

$ErrorActionPreference = "Stop"

function Post-Json {
    param([string]$Path, [object]$Body)

    $json = $Body | ConvertTo-Json -Depth 8 -Compress
    return Invoke-RestMethod -Method Post -Uri "$ApiBase$Path" -ContentType "application/json" -Body $json
}

function New-Signature {
    param([string]$Body, [long]$Timestamp)

    $mac = New-Object System.Security.Cryptography.HMACSHA256
    $mac.Key = [System.Text.Encoding]::UTF8.GetBytes($Secret)
    try {
        $hash = $mac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("$Timestamp.$Body"))
        $hex = ($hash | ForEach-Object { $_.ToString("x2") }) -join ""
        return "t=$Timestamp,v1=$hex"
    }
    finally {
        $mac.Dispose()
    }
}

function Send-Webhook {
    param([string]$Type, [decimal]$Amount)

    $timestamp = [DateTimeOffset]::UtcNow
    $payload = [ordered]@{
        id = "evt_$([Guid]::NewGuid().ToString('N'))"
        type = $Type
        createdAt = $timestamp.ToString("o")
        data = [ordered]@{
            paymentId = $attempt.providerPaymentId
            invoiceId = $invoice.id
            amount = $Amount
            currency = $invoice.currency
        }
    }

    $body = $payload | ConvertTo-Json -Depth 8 -Compress
    $signature = New-Signature -Body $body -Timestamp $timestamp.ToUnixTimeSeconds()

    Write-Host "-> $Type $($payload.id)" -ForegroundColor Cyan
    Invoke-RestMethod -Method Post -Uri "$ApiBase/api/webhooks/test" `
        -ContentType "application/json" `
        -Headers @{ "X-Billing-Signature" = $signature } `
        -Body $body
}

Write-Host "Creating organization..." -ForegroundColor Yellow
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$organization = Post-Json "/api/organizations" @{ name = "Demo Org $suffix" }

Write-Host "Creating starter subscription..." -ForegroundColor Yellow
$subscription = Post-Json "/api/organizations/$($organization.id)/subscriptions" @{ planCode = "starter" }

Write-Host "Creating invoice..." -ForegroundColor Yellow
$invoice = Post-Json "/api/invoices" @{
    organizationId = $organization.id
    subscriptionId = $subscription.id
    currency = $subscription.currency
    lines = @(
        @{
            description = "Starter plan"
            productCode = $subscription.productCode
            quantity = 1
            unitAmount = $subscription.price
        }
    )
}

Write-Host "Creating payment attempt..." -ForegroundColor Yellow
$attempt = Invoke-RestMethod -Method Post `
    -Uri "$ApiBase/api/invoices/$($invoice.id)/payment-attempts" `
    -Headers @{ "Idempotency-Key" = "demo-$([Guid]::NewGuid().ToString('N'))" } `
    -ContentType "application/json" -Body "{}"

$paid = Send-Webhook "payment.succeeded" ([decimal]$invoice.total)
Write-Host "Webhook: $($paid.status), duplicate=$($paid.duplicate)" -ForegroundColor Green

if ($Refund) {
    $refunded = Send-Webhook "payment.refunded" ([decimal]$invoice.total)
    Write-Host "Refund: $($refunded.status), duplicate=$($refunded.duplicate)" -ForegroundColor Green
}

$invoice = Invoke-RestMethod "$ApiBase/api/invoices/$($invoice.id)"
$subscription = Invoke-RestMethod "$ApiBase/api/subscriptions/$($subscription.id)"
$access = Invoke-RestMethod "$ApiBase/api/organizations/$($organization.id)/access"

Write-Host ""
Write-Host "Invoice:      $($invoice.status) paid=$($invoice.amountPaid) refunded=$($invoice.amountRefunded) $($invoice.currency)"
Write-Host "Subscription: $($subscription.status)"
Write-Host "Access:"
$access | ForEach-Object { Write-Host "  - $($_.productCode): $($_.status) (invoice $($_.invoiceId))" }
