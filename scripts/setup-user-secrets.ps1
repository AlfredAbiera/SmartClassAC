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
    [switch] $ConfigurePasswordReset
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

$configurePasswordReset = $ConfigurePasswordReset.IsPresent
if (-not $configurePasswordReset) {
    $resetChoice = Read-Host "Configure SMS password-reset base URL? [y/N]"
    $configurePasswordReset = $resetChoice -match '^[Yy]'
}

if ($configurePasswordReset) {
    $publicBaseUrl = Read-Host "Public base URL (e.g. http://localhost:5062 for local dev)"
    dotnet user-secrets set "PasswordReset:PublicBaseUrl" $publicBaseUrl --project $project
    Write-Host "SMS password-reset URL saved." -ForegroundColor Green
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Ensure XAMPP MySQL/MariaDB is running"
Write-Host "  2. Database/tables are created automatically on first start (or run Migrations/*.sql)"
Write-Host "  3. Start the app:  dotnet run"
Write-Host "  4. Open http://localhost:5062/first-access"
Write-Host "  5. Sign in at http://localhost:5062/login and configure the GSM modem in Settings"
Write-Host "  6. Device enroll/scan: POST /api/device/fingerprint/enroll|scan with header X-Device-Api-Key"
Write-Host ""
