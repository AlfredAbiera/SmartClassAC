# Configures .NET User Secrets for local SmartClass AC development against XAMPP MariaDB.
# Run from the repo root:  .\scripts\setup-user-secrets.ps1
# Or with parameters:     .\scripts\setup-user-secrets.ps1 -AdminPassword '...'

[CmdletBinding()]
param(
    [string] $MariaDbServer = "127.0.0.1",
    [int] $MariaDbPort = 3306,
    [string] $MariaDbDatabase = "smartclassac",
    [string] $MariaDbUser = "root",
    [string] $MariaDbPassword = "",
    [string] $AdminUsername = "admin",
    [string] $AdminPassword = "R3m0vabl3!2028",
    [string] $DeviceApiKey,
    [switch] $ConfigureSmtp
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\SmartClassAC.csproj" | Resolve-Path

function Read-SecretValue {
    param(
        [string] $Prompt,
        [switch] $AsSecureString
    )

    if ($AsSecureString) {
        return (Read-Host -Prompt $Prompt -AsSecureString)
    }

    return Read-Host -Prompt $Prompt
}

function ConvertTo-PlainText {
    param([Security.SecureString] $SecureString)

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

Write-Host "SmartClass AC - User Secrets setup (MariaDB / XAMPP)" -ForegroundColor Cyan
Write-Host "Project: $project`n"

dotnet user-secrets init --project $project | Out-Null

if (-not $PSBoundParameters.ContainsKey('MariaDbPassword')) {
    $secure = Read-SecretValue "MariaDB password for '$MariaDbUser' (blank for XAMPP default)" -AsSecureString
    $MariaDbPassword = ConvertTo-PlainText $secure
}

if (-not $AdminPassword) {
    $AdminPassword = ConvertTo-PlainText (Read-SecretValue "Default admin password (used on first seed only)" -AsSecureString)
}

$connectionString = "Server=$MariaDbServer;Port=$MariaDbPort;Database=$MariaDbDatabase;User ID=$MariaDbUser;Password=$MariaDbPassword;"

dotnet user-secrets set "ConnectionStrings:SmartClassMariaDb" "$connectionString" --project $project
dotnet user-secrets set "DefaultAdmin:Username" "$AdminUsername" --project $project
dotnet user-secrets set "DefaultAdmin:Password" "$AdminPassword" --project $project

if (-not $DeviceApiKey) {
    $deviceChoice = Read-Host "Configure fingerprint device API key now? [Y/n]"
    if ($deviceChoice -notmatch '^[Nn]') {
        $DeviceApiKey = ConvertTo-PlainText (Read-SecretValue "Device API key (sent as X-Device-Api-Key)" -AsSecureString)
        if (-not $DeviceApiKey) {
            $DeviceApiKey = [guid]::NewGuid().ToString("N")
            Write-Host "Generated device API key: $DeviceApiKey" -ForegroundColor Yellow
        }
    }
}

if ($DeviceApiKey) {
    dotnet user-secrets set "DeviceApi:ApiKey" "$DeviceApiKey" --project $project
}

Write-Host "`nCore secrets saved." -ForegroundColor Green
Write-Host "Connection: Server=$MariaDbServer;Port=$MariaDbPort;Database=$MariaDbDatabase;User ID=$MariaDbUser"
if ($DeviceApiKey) {
    Write-Host "Device API key configured for /api/device/fingerprint/*"
}

$configureEmail = $ConfigureSmtp.IsPresent
if (-not $configureEmail) {
    $smtpChoice = Read-Host "Configure SMTP for forgot-password? [y/N]"
    $configureEmail = $smtpChoice -match '^[Yy]'
}

if ($configureEmail) {
    $publicBaseUrl = Read-Host "Public base URL (e.g. http://localhost:5062 for local dev)"
    $fromAddress = Read-Host "From email address"
    $fromName = Read-Host "From display name [SmartClass AC]"
    if (-not $fromName) { $fromName = "SmartClass AC" }
    $smtpHost = Read-Host "SMTP host"
    $smtpPort = Read-Host "SMTP port [587]"
    if (-not $smtpPort) { $smtpPort = "587" }
    $smtpUsername = Read-Host "SMTP username"
    $smtpPassword = ConvertTo-PlainText (Read-SecretValue "SMTP password" -AsSecureString)

    dotnet user-secrets set "PasswordReset:PublicBaseUrl" $publicBaseUrl --project $project
    dotnet user-secrets set "PasswordReset:FromAddress" $fromAddress --project $project
    dotnet user-secrets set "PasswordReset:FromName" $fromName --project $project
    dotnet user-secrets set "PasswordReset:SmtpHost" $smtpHost --project $project
    dotnet user-secrets set "PasswordReset:SmtpPort" $smtpPort --project $project
    dotnet user-secrets set "PasswordReset:SmtpUsername" $smtpUsername --project $project
    dotnet user-secrets set "PasswordReset:SmtpPassword" $smtpPassword --project $project
    dotnet user-secrets set "PasswordReset:EnableSsl" "true" --project $project

    Write-Host "SMTP secrets saved." -ForegroundColor Green
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Ensure XAMPP MySQL/MariaDB is running"
Write-Host "  2. Database/tables are created automatically on first start (or run Migrations/*.sql)"
Write-Host "  3. Start the app:  dotnet run"
Write-Host "  4. Open http://localhost:5062/first-access"
Write-Host "  5. Sign in at http://localhost:5062/login"
Write-Host "  6. Device enroll/scan: POST /api/device/fingerprint/enroll|scan with header X-Device-Api-Key"
Write-Host ""
