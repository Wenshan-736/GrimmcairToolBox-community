param([Parameter(Mandatory = $true)][string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$installRoot = (Resolve-Path -LiteralPath $InstallDirectory -ErrorAction Stop).Path
function Require-PlainPath([string]$Path) {
    $cursor = [IO.FileInfo]::new([IO.Path]::GetFullPath($Path))
    while ($null -ne $cursor) {
        if ($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked paths are not accepted.' }
        $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
    }
}
$project = Join-Path $projectRoot 'Grimmcair.Desktop.csproj'
Require-PlainPath $project
[xml]$projectXml = Get-Content -LiteralPath $project -Raw -Encoding UTF8
$versionNodes = @($projectXml.SelectNodes('/Project/PropertyGroup/Version'))
if ($versionNodes.Count -ne 1 -or $versionNodes[0].HasAttribute('Condition') -or $versionNodes[0].InnerText -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw 'The source project must declare one unconditional release version.'
}
$expectedVersion = [Version]$versionNodes[0].InnerText
$marker = Join-Path $installRoot 'grimmcair.install.json'
Require-PlainPath $marker
if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Choose the matching installed community application directory.' }
$installation = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
if ($installation.schemaVersion -ne 1 -or $installation.edition -ne 'Community' -or $installation.dataMode -ne 'installed') {
    throw 'The selected directory is not a community installation.'
}
$pairs = @(
    @('Grimmcair.SaveParser.dll', 'Dependencies/Grimmcair.SaveParser.dll'),
    @('Data/resources.gcpak', 'Data/resources.gcpak')
)
foreach ($pair in $pairs) {
    $source = Join-Path $installRoot $pair[0]
    $destination = Join-Path $projectRoot $pair[1]
    Require-PlainPath $source
    Require-PlainPath $destination
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing installation dependency: $($pair[0])" }
    if (Test-Path -LiteralPath $destination) { throw "Destination already exists; inspect it before replacing: $($pair[1])" }
}
$desktopAssembly = Join-Path $installRoot 'GrimmcairToolBox.dll'
Require-PlainPath $desktopAssembly
if (-not (Test-Path -LiteralPath $desktopAssembly -PathType Leaf)) { throw 'The community installation is missing GrimmcairToolBox.dll.' }
$application = [Reflection.AssemblyName]::GetAssemblyName($desktopAssembly)
$expectedAssemblyVersion = [Version]::new($expectedVersion.Major, $expectedVersion.Minor, $expectedVersion.Build, [Math]::Max(0, $expectedVersion.Revision))
if ($application.Name -ne 'GrimmcairToolBox' -or $application.Version -ne $expectedAssemblyVersion) {
    throw "The installation application version must match source version $expectedVersion."
}
# The parser has an independent component version; verify its identity rather than requiring the app version.
$parser = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $installRoot 'Grimmcair.SaveParser.dll'))
if ($parser.Name -ne 'Grimmcair.SaveParser') { throw 'The parser dependency has an unexpected assembly identity.' }
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
