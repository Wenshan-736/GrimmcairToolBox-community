param([Parameter(Mandatory = $true)][string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$installRoot = (Resolve-Path -LiteralPath $InstallDirectory -ErrorAction Stop).Path
$marker = Join-Path $installRoot 'grimmcair.install.json'
if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Choose the matching installed community application directory.' }
$installation = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
if ($installation.schemaVersion -ne 1 -or $installation.edition -ne 'Community' -or $installation.dataMode -ne 'installed') {
    throw 'The selected directory is not a community installation.'
}
$pairs = @(
    @('Grimmcair.SaveParser.dll', 'Dependencies/Grimmcair.SaveParser.dll'),
    @('Data/resources.gcpak', 'Data/resources.gcpak')
)
function Require-PlainPath([string]$Path) {
    $cursor = [IO.FileInfo]::new([IO.Path]::GetFullPath($Path))
    while ($null -ne $cursor) {
        if ($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked paths are not accepted.' }
        $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
    }
}
foreach ($pair in $pairs) {
    $source = Join-Path $installRoot $pair[0]
    $destination = Join-Path $projectRoot $pair[1]
    Require-PlainPath $source
    Require-PlainPath $destination
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing installation dependency: $($pair[0])" }
    if (Test-Path -LiteralPath $destination) { throw "Destination already exists; inspect it before replacing: $($pair[1])" }
}
$version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $installRoot 'Grimmcair.SaveParser.dll')).Version
if ($version.Major -ne 0 -or $version.Minor -ne 1 -or $version.Build -ne 8) { throw 'The parser dependency must be from community version 0.1.8.' }
foreach ($pair in $pairs) {
    $source = Join-Path $installRoot $pair[0]
    $destination = Join-Path $projectRoot $pair[1]
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    [IO.File]::Copy($source, $destination, $false)
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
        throw "Dependency verification failed: $($pair[1])"
    }
    Write-Host ("Copied " + $pair[1] + ' SHA256 ' + (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant())
}
