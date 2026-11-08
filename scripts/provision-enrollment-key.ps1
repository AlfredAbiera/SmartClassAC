param(
    [Parameter(Mandatory = $true)][string]$DeviceId,
    [Parameter(Mandatory = $true)][string]$Port,
    [Parameter(Mandatory = $true)][string]$ApplicationDll
)

$ErrorActionPreference = 'Stop'
if ($DeviceId -cnotmatch '^[0-9]{1,23}$') { throw 'DeviceId must contain 1 to 23 digits.' }
if ($Port -notmatch '^COM[1-9][0-9]*$') { throw 'Specify a COM port such as COM3.' }
$dll = (Resolve-Path -LiteralPath $ApplicationDll).Path
if (-not [System.IO.File]::Exists($dll)) { throw 'Application DLL not found.' }

function Wait-ProtocolLine($serial, [string]$prefix, [int]$seconds) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $seconds) {
        try {
            $line = $serial.ReadLine().Trim()
            if ($line.StartsWith($prefix, [StringComparison]::Ordinal)) { return $line }
            if ($line.StartsWith('SC-KEY-ERROR', [StringComparison]::Ordinal)) { throw 'Scanner rejected provisioning.' }
        } catch [System.TimeoutException] { }
    }
    throw "Timed out waiting for $prefix. Check the port and scanner firmware."
}

$serial = New-Object System.IO.Ports.SerialPort $Port,115200,None,8,one
$serial.ReadTimeout = 1000
$serial.WriteTimeout = 10000
$key = $null
$challenge = New-Object byte[] 32
$payload = New-Object byte[] 64
try {
    $serial.Open()
    $found = $false
    for ($i = 0; $i -lt 180; $i++) {
        $serial.Write("SC-KEY-INFO`n")
        try {
            $info = Wait-ProtocolLine $serial 'SC-KEY-INFO ' 1
            $found = $true
            break
        } catch {
            if ($_.Exception.Message -notlike 'Timed out waiting*') { throw }
        }
    }
    if (-not $found) { throw 'No compatible scanner responded. No key was sent.' }
    if ($info -cne "SC-KEY-INFO 1 $DeviceId") {
        throw 'Scanner ID does not match the requested device, or scanner is unpaired. No key was sent.'
    }

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = 'dotnet'
    $start.Arguments = ('"{0}" --enrollment-local-key {1}' -f $dll, $DeviceId)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $key = New-Object byte[] 32
        $stream = $process.StandardOutput.BaseStream
        $offset = 0
        while ($offset -lt $key.Length) {
            $read = $stream.Read($key, $offset, $key.Length - $offset)
            if ($read -eq 0) { break }
            $offset += $read
        }
        $extra = $stream.ReadByte()
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0 -or $offset -ne 32 -or $extra -ne -1) {
            throw "Could not obtain a scanner key from the server. $errorText"
        }
    } finally { $process.Dispose() }

    $serial.Write("SC-KEY-BEGIN`n")
    $ready = Wait-ProtocolLine $serial 'SC-KEY-READY' 10
    if ($ready -cne 'SC-KEY-READY') { throw 'Unexpected scanner response.' }
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($challenge) } finally { $rng.Dispose() }
    [Array]::Copy($key, 0, $payload, 0, 32)
    [Array]::Copy($challenge, 0, $payload, 32, 32)
    $serial.Write($payload, 0, $payload.Length)
    $reply = Wait-ProtocolLine $serial 'SC-KEY-OK ' 15
    $nonceHex = ([BitConverter]::ToString($challenge)).Replace('-', '').ToLowerInvariant()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $emptyHash = ([BitConverter]::ToString($sha.ComputeHash((New-Object byte[] 0)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $message = "SmartClassAC-enroll-v1`nresponse`n$DeviceId`nprovision`n$nonceHex`n$emptyHash`n"
    $hmac = New-Object System.Security.Cryptography.HMACSHA256 (,$key)
    try { $expected = $hmac.ComputeHash([Text.Encoding]::ASCII.GetBytes($message)) } finally { $hmac.Dispose() }
    $actualHex = $reply.Substring('SC-KEY-OK '.Length)
    if ($actualHex -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid scanner proof. Retry provisioning.' }
    $actual = New-Object byte[] 32
    for ($i = 0; $i -lt 32; $i++) { $actual[$i] = [Convert]::ToByte($actualHex.Substring($i * 2, 2), 16) }
    $difference = 0
    for ($i = 0; $i -lt 32; $i++) { $difference = $difference -bor ($actual[$i] -bxor $expected[$i]) }
    if ($difference -ne 0) { throw 'Scanner key verification failed. Do not enroll; investigate the scanner.' }
    Write-Output 'Scanner key provisioned and verified. Fingerprint enrollment remains disabled.'
} finally {
    if ($key) { [Array]::Clear($key, 0, $key.Length) }
    [Array]::Clear($payload, 0, $payload.Length)
    [Array]::Clear($challenge, 0, $challenge.Length)
    if ($serial.IsOpen) { $serial.Close() }
    $serial.Dispose()
}
