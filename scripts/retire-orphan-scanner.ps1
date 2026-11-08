param(
    [Parameter(Mandatory = $true)][string]$OldDeviceId,
    [Parameter(Mandatory = $true)][string]$Port,
    [Parameter(Mandatory = $true)][string]$ApplicationDll,
    [Parameter(Mandatory = $true)][string]$DatabaseName,
    [Parameter(Mandatory = $true)][string]$ExpectedPortalUrl
)

$ErrorActionPreference = 'Stop'
if ($OldDeviceId -cnotmatch '^[0-9]{1,23}$') { throw 'Specify the old numeric device ID.' }
if ($Port -notmatch '^COM[1-9][0-9]*$') { throw 'Specify the trusted scanner USB COM port.' }
if ($DatabaseName -cnotmatch '^[A-Za-z0-9_]+$') { throw 'Specify the exact database name using letters, numbers and underscores.' }
$dll = (Resolve-Path -LiteralPath $ApplicationDll).Path
if (-not [IO.File]::Exists($dll)) { throw 'Application DLL not found.' }
$uri = $null
if (-not [Uri]::TryCreate($ExpectedPortalUrl, [UriKind]::Absolute, [ref]$uri) -or
    $uri.Scheme -notin @('http', 'https')) { throw 'Specify the intended portal URL.' }

function Confirm-DeletedDevice {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = 'dotnet'
    $start.Arguments = ('"{0}" --enrollment-retire-check {1} {2}' -f $dll, $OldDeviceId, $DatabaseName)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            throw 'Database retirement check timed out. No sensor command was sent.'
        }
        $output = $outputTask.GetAwaiter().GetResult().Trim()
        $errorText = $errorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or $output -cne "SC-RETIRE-CHECK 1 $OldDeviceId") {
            throw "Database retirement check failed. $errorText"
        }
    } finally { $process.Dispose() }
}

function Wait-RetirementLine($serial, [string]$prefix, [int]$seconds) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $seconds) {
        try {
            $line = $serial.ReadLine().Trim()
            if ($line.StartsWith('SC-RETIRE-ERROR', [StringComparison]::Ordinal)) { throw $line }
            if ($line.StartsWith($prefix, [StringComparison]::Ordinal)) { return $line }
        } catch [TimeoutException] { }
    }
    throw 'USB retirement response timed out. Do not pair or erase storage; reconnect and investigate.'
}

Write-Warning "DESTRUCTIVE: this will erase ALL sensor fingerprints and remove the old scanner key and ID ($OldDeviceId)."
Write-Output "Confirm that database $DatabaseName belongs to $ExpectedPortalUrl. Stop other operators from changing device records during retirement."
Write-Output 'This tool is only for a deleted old device record, on a trusted local USB connection. It does not migrate templates or delete portal data.'
$confirmation = Read-Host "Type RETIRE $OldDeviceId ERASE to continue"
if ($confirmation -cne "RETIRE $OldDeviceId ERASE") { throw 'Retirement cancelled. No scanner changes made.' }
Confirm-DeletedDevice

$serial = New-Object IO.Ports.SerialPort $Port,115200,None,8,one
$serial.ReadTimeout = 1000
$serial.WriteTimeout = 10000
try {
    $serial.Open()
    $info = $null
    for ($i = 0; $i -lt 180; $i++) {
        $serial.Write("SC-RETIRE-INFO`n")
        try { $info = Wait-RetirementLine $serial 'SC-RETIRE-INFO ' 1; break }
        catch {
            if ($_.Exception.Message -notlike 'USB retirement response timed out*') { throw }
        }
    }
    if ($info -cne "SC-RETIRE-INFO 1 $OldDeviceId $ExpectedPortalUrl") {
        throw 'Scanner identity or portal URL mismatch. No erase command was sent.'
    }
    Confirm-DeletedDevice
    $serial.Write("SC-RETIRE-ERASE $OldDeviceId`n")
    $reply = Wait-RetirementLine $serial 'SC-RETIRE-OK ' 90
    if ($reply -cne "SC-RETIRE-OK 1 $OldDeviceId reboot-required") {
        throw 'Unexpected retirement result. Do not pair; investigate.'
    }
    Write-Output 'Sensor verified empty; old key and saved ID removed. Reboot, pair the new portal device, then provision its fresh key.'
} finally {
    if ($serial.IsOpen) { $serial.Close() }
    $serial.Dispose()
}
