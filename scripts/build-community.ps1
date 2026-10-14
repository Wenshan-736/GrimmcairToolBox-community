param(
    [ValidateSet('Desktop', 'Mcp')][string]$Target = 'Desktop',
    [string]$PackageSource = '',
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'Grimmcair.Desktop.csproj'
$dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop
$sdkVersion = & $dotnet.Source --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') { throw 'Install the .NET 10 SDK and ensure dotnet is available on PATH.' }
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'Data/resources.gcpak') -PathType Leaf)) {
    throw 'Copy Data/resources.gcpak from the matching community installation first. See README.'
}
if ($Target -eq 'Desktop' -and -not (Test-Path -LiteralPath (Join-Path $projectRoot 'Dependencies/Grimmcair.SaveParser.dll') -PathType Leaf)) {
    throw 'Copy Grimmcair.SaveParser.dll to Dependencies from the matching community installation first. See README.'
}
$targetName = $Target.ToLowerInvariant()
$arguments = @('publish', $project, '-c', 'Release', '-p:DesktopEdition=Community', '-p:UseInternalSaveParser=false',
    '-p:NuGetAudit=false', '--artifacts-path', (Join-Path $projectRoot "artifacts/build-$targetName"),
    '-o', (Join-Path $projectRoot "artifacts/$targetName"), '--nologo')
if ($Target -eq 'Mcp') { $arguments += '-p:KnowledgeMcp=true' }
if ($SelfContained) { $arguments += @('-r', 'win-x64', '--self-contained', 'true') }
else { $arguments += @('--self-contained', 'false') }
if ($PackageSource) {
    $resolvedSource = (Resolve-Path -LiteralPath $PackageSource -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $resolvedSource -PathType Container)) { throw 'PackageSource must be a local directory containing NuGet packages.' }
    $arguments += @('--source', $resolvedSource)
}
& $dotnet.Source @arguments
if ($LASTEXITCODE -ne 0) { throw "Community $Target build failed (exit $LASTEXITCODE)." }
$noticeDirectory = Join-Path $projectRoot "artifacts/$targetName/ThirdPartyNotices"
[IO.Directory]::CreateDirectory($noticeDirectory) | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'ThirdPartyNotices/Markdig.LICENSE.txt') -Destination (Join-Path $noticeDirectory 'Markdig.LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $projectRoot "artifacts/$targetName/COMMUNITY-SOURCE-LICENSE.txt") -Force
Write-Host ("Build completed: " + (Join-Path $projectRoot "artifacts/$targetName"))
