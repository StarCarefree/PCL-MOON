#Requires -Version 7.0
[CmdletBinding()]
param([string]$DotNetPath = '', [switch]$SkipLauncherBuild, [switch]$CheckMappings)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$candidates = @($DotNetPath, $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }), $((Get-Command dotnet -ErrorAction SilentlyContinue).Source))
$dotnet = $candidates | Where-Object { $_ -and (Test-Path $_) -and (Test-Path (Join-Path (Split-Path $_) 'sdk')) } | Select-Object -First 1
if (!$dotnet) { throw 'Provide a .NET 10 SDK via -DotNetPath.' }
$env:DOTNET_ROOT = Split-Path $dotnet
$base = if ($env:PI_SCRATCH_DIR) { $env:PI_SCRATCH_DIR } else { [IO.Path]::GetTempPath() }
$scratch = Join-Path $base ('game-link-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $scratch | Out-Null
function Check-Exit([string]$Step) { if ($LASTEXITCODE -ne 0) { throw "$Step failed; reports: $scratch" } }
& (Join-Path $repo 'tools\multiplayer-agent\build.ps1')
if (!$SkipLauncherBuild) {
    & $dotnet build (Join-Path $repo 'Plain Craft Launcher 2\Plain Craft Launcher 2.csproj') --no-restore -c Debug -v minimal *> (Join-Path $scratch 'launcher-build.log')
    Check-Exit 'Launcher build'
}
$previous = $env:PCL_MOON_TEST_LINK_MAPPINGS
try {
    if ($CheckMappings) { $env:PCL_MOON_TEST_LINK_MAPPINGS = '1' }
    else { $env:PCL_MOON_TEST_LINK_MAPPINGS = '' }
    & $dotnet test (Join-Path $repo 'PCL.Core.Test\PCL.Core.Test.csproj') --no-restore --filter 'FullyQualifiedName~GameLink' --logger 'trx;LogFileName=game-link.trx' --results-directory $scratch *> (Join-Path $scratch 'core-tests.log')
    Get-Content (Join-Path $scratch 'core-tests.log') -Tail 8
    Check-Exit 'Game link core tests'
} finally { $env:PCL_MOON_TEST_LINK_MAPPINGS = $previous }
Write-Output "GAME_LINK_CHECK_PASS · reports: $scratch"
$global:LASTEXITCODE = 0
