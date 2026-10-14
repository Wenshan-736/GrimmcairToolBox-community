param([string]$ExecutablePath = '', [switch]$ResolveOnly)
$ErrorActionPreference = 'Stop'

# Opt-in .NET 10 host tracing, for failures before App can write its ordinary safe log.
# Runtime traces can contain local installation paths/environment details. Inspect before sharing.
# Official variables: https://learn.microsoft.com/dotnet/core/tools/dotnet-environment-variables#dotnet_host_trace
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $applicationDirectory = Split-Path -Parent $PSScriptRoot
    $ExecutablePath = Join-Path $applicationDirectory 'GrimmcairToolBox.exe'
}
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw 'Application not found. Place this scripts folder next to GrimmcairToolBox.exe, or pass -ExecutablePath explicitly.'
}
$applicationDirectory = Split-Path -Parent $ExecutablePath
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$markerPath = Join-Path $applicationDirectory 'grimmcair.install.json'
if (Test-Path -LiteralPath $markerPath) {
    $markerItem = Get-Item -LiteralPath $markerPath -Force
    if ($markerItem.PSIsContainer -or $markerItem.Length -gt 4096 -or ($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Invalid installation marker; no data directory was changed.'
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($marker.schemaVersion -ne 1 -or $marker.dataMode -ne 'installed' -or $marker.edition -notin @('Community', 'Dedicated')) {
        throw 'Invalid installation marker; no data directory was changed.'
    }
    # Host traces live at the fixed edition root, outside switchable datasets. Upgrades do not create program-side UserData.
    $candidates = @((Join-Path $localAppData ('Grimmcair/' + $marker.edition + '/logs')))
} else {
    $candidates = @((Join-Path $applicationDirectory 'UserData/logs'), (Join-Path $localAppData 'Grimmcair/Logs'))
}
if ($ResolveOnly) { $candidates | ConvertTo-Json -Compress; exit 0 }
$diagnosticRoot = $null
foreach ($candidate in $candidates) {
    try {
        $candidate = [IO.Path]::GetFullPath($candidate)
        $ancestor = [IO.DirectoryInfo]::new($candidate)
        while ($null -ne $ancestor) {
            if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Diagnostic links are not accepted.' }
            $ancestor = $ancestor.Parent
        }
        [IO.Directory]::CreateDirectory($candidate) | Out-Null
        $probe = Join-Path $candidate ('diagnostic-probe-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        $stream = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $stream.Dispose()
        Remove-Item -LiteralPath $probe
        $diagnosticRoot = $candidate
        break
    } catch { }
}
if ($null -eq $diagnosticRoot) { throw 'Neither diagnostics directory is writable. The application was not started.' }

$tracePaths = @('host-trace.log', 'host-trace.1.log', 'host-trace.2.log') | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $diagnosticRoot $_)) }
$rootPrefix = $diagnosticRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($path in $tracePaths) {
    if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid diagnostics target.' }
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Diagnostic links are not accepted.'
    }
}
$traceMutex = [Threading.Mutex]::new($false, 'Local\Grimmcair.OptionalHostTrace')
$ownsTraceMutex = $false
$savedEnvironment = @{}
$exitCode = 1
try {
    try { $ownsTraceMutex = $traceMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsTraceMutex = $true }
    if (-not $ownsTraceMutex) { throw 'Another diagnostic launch is still running. Close that application before retrying.' }
    if (Test-Path -LiteralPath $tracePaths[2]) { Remove-Item -LiteralPath $tracePaths[2] -Force }
    if (Test-Path -LiteralPath $tracePaths[1]) { Move-Item -LiteralPath $tracePaths[1] -Destination $tracePaths[2] }
    if (Test-Path -LiteralPath $tracePaths[0]) { Move-Item -LiteralPath $tracePaths[0] -Destination $tracePaths[1] }
    $variables = @{
        'DOTNET_HOST_TRACE' = '1'
        'DOTNET_HOST_TRACEFILE' = $tracePaths[0]
        'DOTNET_HOST_TRACE_VERBOSITY' = '3'
        'GRIMMCAIR_DIAGNOSTICS_DIRECTORY' = $diagnosticRoot
    }
    foreach ($name in $variables.Keys) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $variables[$name], 'Process')
    }
    Write-Host 'Diagnostic launch: close the application when the issue has been reproduced.'
    Write-Host 'Host traces may include local paths/environment details; inspect before sharing.'
    Write-Host ('Log directory: ' + $diagnosticRoot)
    # This is the user's interactive application, not a hidden helper. No user-supplied app arguments are passed.
    $process = Start-Process -FilePath $ExecutablePath -WorkingDirectory $applicationDirectory -WindowStyle Normal -PassThru -Wait
    $exitCode = $process.ExitCode
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if ($ownsTraceMutex) {
        try {
            # Retain only the final 2 MiB after this opt-in run; the host's live trace has no size-limit switch.
            if ((Test-Path -LiteralPath $tracePaths[0] -PathType Leaf) -and (Get-Item -LiteralPath $tracePaths[0]).Length -gt 2MB) {
                $stream = [IO.File]::OpenRead($tracePaths[0])
                try {
                    [void]$stream.Seek(-2MB, [IO.SeekOrigin]::End)
                    $buffer = New-Object byte[] 2MB
                    $offset = 0
                    while ($offset -lt $buffer.Length) {
                        $read = $stream.Read($buffer, $offset, $buffer.Length - $offset)
                        if ($read -eq 0) { break }
                        $offset += $read
                    }
                } finally { $stream.Dispose() }
                [IO.File]::WriteAllBytes($tracePaths[0], $buffer)
            }
        } catch { Write-Host 'The optional host trace could not be trimmed after exit.' }
        $traceMutex.ReleaseMutex()
    }
    $traceMutex.Dispose()
}
Write-Host ('Application exit code: ' + $exitCode)
exit $exitCode
