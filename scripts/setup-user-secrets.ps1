# Configures .NET User Secrets for local SmartClass AC development.
# Run from the repo root:  .\scripts\setup-user-secrets.ps1
# Or with parameters:     .\scripts\setup-user-secrets.ps1 -SupabasePassword '...' -AdminPassword '...'

[CmdletBinding()]
param(
    [string] $ProjectRef,
    [string] $SupabaseHost="https://xwoejfubhmgcnfmzodlu.supabase.co",
    [int] $SupabasePort = 5432,
    [string] $SupabaseUser = "postgres",
    [string] $SupabasePassword,
    [string] $AdminUsername = "admin",
    [string] $AdminPassword = "R3m0vabl3!2028",
    [switch] $UseSessionPooler,
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

Write-Host "SmartClass AC - User Secrets setup" -ForegroundColor Cyan
Write-Host "Project: $project`n"

dotnet user-secrets init --project $project | Out-Null

if (-not $ProjectRef -and -not $SupabaseHost) {
    $connectionChoice = Read-Host "Use (D)irect connection or (S)ession pooler? [D/S]"
    $UseSessionPooler = $connectionChoice -match '^[Ss]'
}

if ($UseSessionPooler) {
    if (-not $SupabaseHost) {
        $SupabaseHost = Read-Host "Session pooler host (e.g. aws-0-ap-southeast-1.pooler.supabase.com)"
    }
        if ($SupabasePort -eq 5432) {
            $portInput = Read-Host "Session pooler port [5432]"
            if ($portInput) { $SupabasePort = [int]$portInput } else { $SupabasePort = 5432 }
    }
    if ($SupabaseUser -eq "postgres") {
        $SupabaseUser = Read-Host "Session pooler username (e.g. postgres.YOUR_PROJECT_REF)"
    }
}
else {
    if (-not $ProjectRef) {
        $ProjectRef = Read-Host "Supabase project ref (from db.YOUR_REF.supabase.co)"
    }
    if (-not $SupabaseHost) {
        $SupabaseHost = "db.$ProjectRef.supabase.co"
    }
}

if (-not $SupabasePassword) {
    $SupabasePassword = ConvertTo-PlainText (Read-SecretValue "Supabase database password" -AsSecureString)
}

if (-not $AdminPassword) {
    $AdminPassword = ConvertTo-PlainText (Read-SecretValue "Default admin password (used on first seed only)" -AsSecureString)
}

$connectionString = "Host=$SupabaseHost;Port=$SupabasePort;Database=postgres;Username=$SupabaseUser;Password=$SupabasePassword;SSL Mode=Require;"

dotnet user-secrets set "ConnectionStrings:SmartClassSupabase" "$connectionString" --project $project
dotnet user-secrets set "DefaultAdmin:Username" "$AdminUsername" --project $project
dotnet user-secrets set "DefaultAdmin:Password" "$AdminPassword" --project $project

Write-Host "`nCore secrets saved." -ForegroundColor Green

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
Write-Host "  1. Run SUPABASE-SQL-SETUP.sql once in Supabase Dashboard SQL Editor"
Write-Host "  2. Start the app:  dotnet run"
Write-Host "  3. Open http://localhost:5062/first-access"
Write-Host "  4. Sign in at http://localhost:5062/login"
Write-Host ""
