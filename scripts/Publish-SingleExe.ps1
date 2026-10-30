#Requires -Version 7.0
<#
.SYNOPSIS
  把 PCL-CY 打成一个框架依赖的单 exe。本脚本不会在被点源时发布。

  产物只有一个 exe。启动文件作为普通嵌入资源放进 PE，不再把压缩包接到文件尾。
  文件尾大段附加载荷是银狐加载器的典型形状（小壳 + 超大 overlay），单包导出会被报成 Trojan.Win64.SilverFox。
  未安装对应架构的 .NET 10 Desktop Runtime、Visual C++ 2015-2022，或（未随包时）WebView2 时，先弹出缺少的组件；只有点「同意并下载」才会从微软官方地址下载并安装。
  体积靠「不打包 .NET 10、不压进单文件内部、外层 zip 不再二次压缩」来减。WPF 不修剪，原生库必须进包，缺了就失败。
  WebView2 Fixed Version 不是编译库。传入 -FixedRuntimeDirectory 才会打进同一个 exe；不传则 exe 更小，浏览页缺少时由同意下载补上。

  不要在其它 dotnet build / publish 还在跑时执行本脚本。

.EXAMPLE
  双击 scripts\Publish-SingleExe.bat
  或 .\scripts\Publish-SingleExe.ps1 -Architecture x64 -Configuration Release
.EXAMPLE
  .\scripts\Publish-SingleExe.ps1 -Architecture x64 -NoReadyToRun
 .EXAMPLE
   双击 scripts\Publish-SingleExe-NoR2R.bat
   或 .\scripts\Publish-SingleExe.ps1 -Architecture x64 -NoReadyToRun
   ReadyToRun 关闭，单 exe 更小，首次启动会 JIT。
#>
[CmdletBinding()]
param(
    [string]$Architecture = '',

    [switch]$SelfCheck,

    [ValidateSet('Debug', 'Release', 'Beta', 'CI')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory = '',

    [string]$DotNetPath = '',

    [string]$FixedRuntimeDirectory = '',

    # ReadyToRun 默认开启：预先 AOT 编译，避免用户首次启动时 JIT 造成的卡顿。
    # 仅在需要排查 JIT 相关问题时才用 -NoReadyToRun 关掉（会显著增加启动耗时）。
    [switch]$NoReadyToRun,

    # Troubleshooting: restore the old transient build output / server behavior.
    [switch]$NoBuildCache,

    [switch]$DisableBuildServers
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not [string]::IsNullOrWhiteSpace($Architecture)) { $Architecture = $Architecture.ToLowerInvariant() }
$script:RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$script:ProjectDir = Join-Path $script:RepoRoot 'Plain Craft Launcher 2'
$script:ProjectFile = Join-Path $script:ProjectDir 'Plain Craft Launcher 2.csproj'
$script:StubDir = Join-Path $PSScriptRoot 'single-exe'

. (Join-Path $PSScriptRoot 'single-exe\PackageLayout.ps1')
function Write-Step([string]$Message) { Write-Host "[single-exe] $Message" }
function Throw-Single([string]$Message) { throw "Publish-SingleExe: $Message" }

function Invoke-TimedStep([string]$Name, [scriptblock]$Action) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try { & $Action }
    finally { Write-Step ("{0}: {1:N2} 秒" -f $Name, $watch.Elapsed.TotalSeconds) }
}

function Get-ScratchRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:PI_SCRATCH_DIR)) {
        return [System.IO.Path]::GetFullPath($env:PI_SCRATCH_DIR)
    }
    return [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
}

function Assert-LastExitCode([string]$Name) {
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        Throw-Single "$Name 失败，exit code=$LASTEXITCODE"
    }
}

function Get-MsbuildPlatform([string]$Arch) {
    if ($Arch -eq 'arm64') { return 'ARM64' }
    return 'x64'
}

function Get-ExpectedPeMachine([string]$Arch) {
    if ($Arch -eq 'arm64') { return [uint16]0xAA64 }
    return [uint16]0x8664
}

function Get-PeMachine([string]$Path) {
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $br = [System.IO.BinaryReader]::new($fs)
        if ($br.ReadUInt16() -ne 0x5A4D) { Throw-Single "不是 PE: $Path" }
        $null = $fs.Seek(0x3C, [System.IO.SeekOrigin]::Begin)
        $peOff = $br.ReadInt32()
        $null = $fs.Seek($peOff, [System.IO.SeekOrigin]::Begin)
        if ($br.ReadUInt32() -ne 0x4550) { Throw-Single "不是 PE: $Path" }
        return $br.ReadUInt16()
    }
    finally { $fs.Dispose() }
}

function Test-DotNetHasSdk([string]$DotNetExe) {
    $sdk = Join-Path (Split-Path -Parent $DotNetExe) 'sdk'
    return Test-Path -LiteralPath $sdk -PathType Container
}

function Resolve-DotNetExe {
    $candidates = New-Object System.Collections.Generic.List[string]
    function Add-DotNet([string]$Path) {
        if ([string]::IsNullOrWhiteSpace($Path)) { return }
        $full = $Path
        if (Test-Path -LiteralPath $Path -PathType Container) { $full = Join-Path $Path 'dotnet.exe' }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { return }
        if (-not $candidates.Contains($full)) { $candidates.Add($full) }
    }
    Add-DotNet $DotNetPath
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { Add-DotNet $cmd.Source }
    foreach ($name in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_ARM64')) {
        Add-DotNet ([Environment]::GetEnvironmentVariable($name, 'Process'))
        Add-DotNet ([Environment]::GetEnvironmentVariable($name, 'User'))
        Add-DotNet ([Environment]::GetEnvironmentVariable($name, 'Machine'))
    }
    foreach ($hit in @(Get-Command dotnet -All -ErrorAction SilentlyContinue)) {
        if ($hit.Source) { Add-DotNet $hit.Source }
    }
    $withSdk = @($candidates | Where-Object { Test-DotNetHasSdk $_ })
    if ($withSdk.Count -gt 0) { return $withSdk[0] }
    if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
        Throw-Single "DotNetPath 旁边没有 SDK: $DotNetPath"
    }
    Throw-Single '未找到带 SDK 的 dotnet.exe。PATH 上的 dotnet 可能只是运行时，请用 -DotNetPath 指定 .NET 10 SDK。'
}

function Initialize-ExternalCaches {
    $nugetRoot = Join-Path $script:ScratchRoot 'pcl-cy-dotnet-cache'
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $nugetRoot 'nuget-packages' }
    $httpCache = if ($env:NUGET_HTTP_CACHE_PATH) { $env:NUGET_HTTP_CACHE_PATH } else { Join-Path $nugetRoot 'nuget-http-cache' }
    $cliHome = if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { Join-Path $nugetRoot 'dotnet-cli-home' }
    foreach ($p in @($packages, $httpCache, $cliHome, $script:WorkRoot)) {
        New-Item -ItemType Directory -Force -Path $p | Out-Null
    }
    $env:NUGET_PACKAGES = $packages
    $env:NUGET_HTTP_CACHE_PATH = $httpCache
    $env:DOTNET_CLI_HOME = $cliHome
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
}

function Resolve-OutputFile {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $dir = Join-Path $script:RepoRoot ("artifacts\single-exe-win-{0}" -f $Architecture)
    }
    elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
        $dir = [System.IO.Path]::GetFullPath($OutputDirectory)
    }
    else {
        $dir = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
    }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return (Join-Path $dir 'PCL-MOON.exe')
}

function Get-BuildOutputDirectory {
    if ($NoBuildCache) { return (Join-Path $script:WorkRoot 'build\') }
    # Keep build output across invocations; PID-based output invalidates WPF/C#
    # incremental checks even when no source changed. Never cache the publish
    # directory: a fresh publish/stage prevents deleted files leaking into a pack.
    $repoBytes = [System.Text.Encoding]::UTF8.GetBytes($script:RepoRoot.ToLowerInvariant())
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $repoKey = ([System.BitConverter]::ToString($sha.ComputeHash($repoBytes))).Replace('-', '').Substring(0, 16) }
    finally { $sha.Dispose() }
    $mode = if ($NoReadyToRun) { 'il' } else { 'r2r' }
    return (Join-Path $script:ScratchRoot "pcl-cy-single-exe-cache\$repoKey\$Architecture\$Configuration\$mode\build\")
}

function Invoke-DotNetPublish([string]$DotNetExe, [string]$OutDir) {
    $rid = "win-$Architecture"
    $platform = Get-MsbuildPlatform $Architecture
    $readyToRunValue = if ($NoReadyToRun) { 'false' } else { 'true' }
    $publishArgs = @(
        'publish', $script:ProjectFile,
        '-c', $Configuration,
        '-r', $rid,
        '--self-contained', 'false',
        '-p:SelfContained=false',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:IncludeAllContentForSelfExtract=false',
        '-p:EnableCompressionInSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:EnableTrimAnalyzer=false',
        "-p:PublishReadyToRun=$readyToRunValue",
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        '-p:CopyOutputSymbolsToPublishDirectory=false',
        '-p:SatelliteResourceLanguages=zh-CN%3Bzh-Hans%3Bzh-TW%3Bzh-Hant%3Ben%3Ben-US%3Ben-GB',
        '-p:TieredCompilation=true',
        '-p:TieredCompilationQuickJit=true',
        '-p:TieredCompilationQuickJitForLoops=true',
        '-p:TieredPGO=true',
        '-p:ServerGarbageCollection=false',
        '-p:DebuggerSupport=false',
        "-p:RuntimeIdentifier=$rid",
        "-p:Platform=$platform",
        ('-p:OutputPath=' + (Get-BuildOutputDirectory)),
        "-p:PclSecretsStampFile=$($script:SecretsStampFile)",
        '-o', $OutDir,
        '--nologo'
    )
    # 有密钥时必须禁用编译服务器：生成器读的是服务器进程自己的环境变量
    if ($DisableBuildServers -or -not [string]::IsNullOrWhiteSpace($env:PCL_WRITE_SECRET)) {
        $publishArgs += '--disable-build-servers'
    }
    Write-Step ("dotnet " + ($publishArgs -join ' '))
    & $DotNetExe @publishArgs
    Assert-LastExitCode 'dotnet publish'
}

function Test-FileContainsText([string]$Path, [string]$Name) {
    $ascii = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($Path))
    if ($ascii.Contains($Name)) { return $true }
    $utf16 = [System.Text.Encoding]::Unicode.GetString([System.IO.File]::ReadAllBytes($Path))
    return $utf16.Contains($Name)
}

function Assert-NoMissingLibraries([string]$PublishDir, [string]$LauncherExe) {
    foreach ($name in @('WebView2Loader.dll', 'e_sqlite3.dll')) {
        $loose = Join-Path $PublishDir $name
        $inBundle = Test-FileContainsText $LauncherExe $name
        $onDisk = Test-Path -LiteralPath $loose -PathType Leaf
        if (-not $inBundle -and -not $onDisk) {
            Throw-Single "漏库: 单文件和发布目录都没有 $name。拒绝为了体积丢掉原生库。"
        }
        Write-Step ("库 {0}: bundle={1} loose={2}" -f $name, $inBundle, $onDisk)
    }
    $bytes = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($LauncherExe))
    if ($bytes.Contains('includedFrameworks')) {
        Throw-Single '启动器 exe 含 includedFrameworks，拒绝把 .NET 运行时打进去。'
    }
}

function Copy-PublishPayload([string]$PublishDir, [string]$Stage) {
    New-Item -ItemType Directory -Force -Path $Stage | Out-Null
    $copied = 0
    foreach ($file in @(Get-ChildItem -LiteralPath $PublishDir -File)) {
        if ($file.Extension -in '.pdb', '.xml') { continue }
        if ($file.Name -match '(?i)^(windowsdesktop-runtime|dotnet-runtime).*\.(zip|exe)$') {
            Throw-Single "发布目录出现 .NET 运行时包: $($file.Name)"
        }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Stage $file.Name) -Force
        $copied++
    }
    foreach ($dirName in @('runtimes')) {
        $src = Join-Path $PublishDir $dirName
        if (Test-Path -LiteralPath $src -PathType Container) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $Stage $dirName) -Recurse -Force
        }
    }
    foreach ($pair in @(
            @{ Src = Join-Path $script:RepoRoot 'LICENSE'; Name = 'LICENSE' }
            @{ Src = Join-Path $script:ProjectDir 'LICENCE'; Name = 'LICENCE' }
        )) {
        if (-not (Test-Path -LiteralPath $pair.Src -PathType Leaf)) {
            Throw-Single "缺少许可证: $($pair.Src)"
        }
        Copy-Item -LiteralPath $pair.Src -Destination (Join-Path $Stage $pair.Name) -Force
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Stage 'Plain Craft Launcher 2.exe') -PathType Leaf)) {
        Throw-Single '发布目录没有 Plain Craft Launcher 2.exe'
    }
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $manifest = (@{ architecture = $Architecture; channel = '10.0'; selfContained = $false; trimmed = $false } | ConvertTo-Json -Compress)
    [System.IO.File]::WriteAllText((Join-Path $Stage 'runtime.json'), $manifest, $utf8)
    Write-Step "已暂存 $copied 个发布文件，未丢弃 dll/exe/json。"
}

function Copy-FixedRuntime([string]$Stage) {
    if ([string]::IsNullOrWhiteSpace($FixedRuntimeDirectory)) {
        Write-Step '未嵌入 WebView2 Fixed Version。WebView2Loader 仍必须在包内；浏览器运行时未打进 exe，以减小体积。'
        return
    }
    $root = [System.IO.Path]::GetFullPath($FixedRuntimeDirectory)
    $exe = Join-Path $root 'msedgewebview2.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        Throw-Single "FixedRuntimeDirectory 缺少 msedgewebview2.exe: $root"
    }
    $machine = Get-PeMachine $exe
    $expected = Get-ExpectedPeMachine $Architecture
    if ($machine -ne $expected) {
        Throw-Single ("WebView2 PE machine=0x{0:X4} 与 {1} 不匹配" -f $machine, $Architecture)
    }
    $dest = Join-Path $Stage 'WebView2Runtime'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $root '*') -Destination $dest -Recurse -Force
    Write-Step "已嵌入 WebView2Runtime（会明显增大单 exe）"
}

function New-PayloadZip([string]$Stage, [string]$ZipPath) {
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Stored, not deflated. A second compression pass looks like a packer and barely shrinks the already-packed launcher.
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $Stage,
        $ZipPath,
        [System.IO.Compression.CompressionLevel]::NoCompression,
        $false)
}

function Get-FileSha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Write-BuildInfo([string]$Path, [string]$Arch, [string]$Hash, [bool]$WebViewBundled) {
    if ($Hash -notmatch '^[0-9a-f]{64}$') { Throw-Single '启动文件哈希无效' }
    if ($Arch -ne 'x64' -and $Arch -ne 'arm64') { Throw-Single "架构无效: $Arch" }
    $flag = if ($WebViewBundled) { 'true' } else { 'false' }
    $text = @"
internal static class BuildInfo
{
    internal const string Arch = "$Arch";
    internal const string PayloadSha256 = "$Hash";
    internal const bool WebViewBundled = $flag;
}
"@
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, $text.Trim() + "`n", $utf8)
}

function Find-RoslynCsc([string]$DotNetExe) {
    # Ask the host which SDK it selected (including global.json), rather than
    # recursively walking all SDK files and lexically sorting version strings.
    $version = ((& $DotNetExe --version) -join '').Trim()
    Assert-LastExitCode 'dotnet --version'
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        Throw-Single "无法确定 SDK 版本: $version"
    }
    $csc = Join-Path (Split-Path -Parent $DotNetExe) "sdk\$version\Roslyn\bincore\csc.dll"
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { Throw-Single "找不到所选 SDK 的 Roslyn: $csc" }
    return $csc
}

function Compile-Bootstrapper([string]$DotNetExe, [string]$OutExe, [string]$ZipPath, [string]$Hash, [bool]$WebViewBundled) {
    $csc = Find-RoslynCsc $DotNetExe
    $fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $refs = @(
        'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll',
        'System.Windows.Forms.dll', 'System.Net.Http.dll',
        'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll',
        'System.Web.Extensions.dll'
    )
    $refArgs = @()
    foreach ($name in $refs) {
        $path = Join-Path $fx $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Throw-Single "缺少 .NET Framework 引用: $path"
        }
        $refArgs += ('/r:' + $path)
    }
    $platform = if ($Architecture -eq 'arm64') { 'arm64' } else { 'x64' }
    $info = Join-Path $script:WorkRoot 'BuildInfo.cs'
    $res = Join-Path $script:WorkRoot 'app.res'
    Write-BuildInfo $info $Architecture $Hash $WebViewBundled
    Write-Win32Resource $res '2.15.4.0' 'PCL-MOON' (Join-Path $script:ProjectDir 'Images\icon.ico') (Join-Path $script:StubDir 'app.manifest')
    $obj = Join-Path $script:WorkRoot 'app.obj'
    $cvtres = Join-Path $fx 'cvtres.exe'
    if (-not (Test-Path -LiteralPath $cvtres -PathType Leaf)) { Throw-Single "缺少 cvtres.exe: $cvtres" }
    $machine = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'X64' }
    & $cvtres /nologo ('/machine:' + $machine) ('/out:' + $obj) $res
    Assert-LastExitCode 'cvtres'
    $cscArgs = @(
        $csc,
        '/nologo', '/noconfig', '/nostdlib+', '/optimize+', '/deterministic+',
        '/target:winexe',
        ('/platform:' + $platform),
        '/langversion:7.3',
        '/codepage:65001',
        '/utf8output',
        ('/win32res:' + $obj),
        ('/resource:' + $ZipPath + ',PCL-MOON.App'),
        ('/out:' + $OutExe)
    ) + $refArgs
    $cscArgs += (Join-Path $script:StubDir 'Bootstrapper.cs')
    $cscArgs += $info
    Write-Step '编译 .NET Framework 启动器（资源内嵌，不在文件尾附加数据，不链接 .NET 10）'
    & $DotNetExe exec @cscArgs
    Assert-LastExitCode 'csc'
    if (-not (Test-Path -LiteralPath $OutExe -PathType Leaf)) {
        Throw-Single "启动器没有生成: $OutExe"
    }
    Set-PeChecksum $OutExe
    $overlay = Assert-NoPeOverlay $OutExe
    Write-Step "PE overlay=$overlay（必须接近 0，启动文件在资源里）"
}

function Invoke-SingleExeSelfCheck {
    if ([string]::IsNullOrWhiteSpace($script:Architecture)) { $script:Architecture = 'x64' }
    $dotnet = Resolve-DotNetExe
    $stage = Join-Path $script:WorkRoot 'stage'
    $zip = Join-Path $script:WorkRoot 'payload.zip'
    $stub = Join-Path $script:WorkRoot 'stub.exe'
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $stage 'Plain Craft Launcher 2.exe'), 'pcl-moon-self-check')
    New-PayloadZip $stage $zip
    Compile-Bootstrapper $dotnet $stub $zip (Get-FileSha256 $zip) $false
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($stub)
    if ($info.ProductName -ne 'PCL-MOON' -or $info.FileDescription -ne 'PCL-MOON') {
        Throw-Single "版本信息资源未写入: Product=$($info.ProductName) Description=$($info.FileDescription)"
    }
    $ascii = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($stub))
    if ($ascii.Contains('MOONCLPKGv1')) { Throw-Single '产物仍含旧的尾部标记' }
    $bad = Join-Path $script:WorkRoot 'overlay.exe'
    Copy-Item -LiteralPath $stub -Destination $bad -Force
    $extra = [System.IO.File]::Open($bad, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
    try {
        $pad = New-Object byte[] 4096
        $extra.Write($pad, 0, $pad.Length)
    }
    finally { $extra.Dispose() }
    $rejected = $false
    try { Assert-NoPeOverlay $bad } catch { $rejected = $true }
    if (-not $rejected) { Throw-Single '尾部载荷守卫没有拒绝 4 KiB overlay' }
    $proc = Start-Process -FilePath $stub -ArgumentList '--self-check' -Wait -PassThru -WindowStyle Hidden
    if ($proc.ExitCode -ne 0) { Throw-Single "嵌入资源自检失败，exit=$($proc.ExitCode)" }
    Write-Host 'SELF-CHECK OK'
}

function Initialize-Secrets {
    # dot-source 本地密钥文件；生成器跳过空值，留空等于不设置
    $secretsLocal = Join-Path $PSScriptRoot 'secrets.local.ps1'
    if (Test-Path -LiteralPath $secretsLocal -PathType Leaf) {
        . $secretsLocal
        $env:PCL_WRITE_SECRET = '1'
        $names = @(Get-ChildItem env: | Where-Object { $_.Name.StartsWith('PCL_') -and $_.Name -ne 'PCL_WRITE_SECRET' } |
            Sort-Object Name | ForEach-Object {
                '{0}={1}' -f $_.Name, $(if ([string]::IsNullOrWhiteSpace($_.Value)) { '空' } else { '已设置' })
            })
        Write-Step ('已加载 secrets.local.ps1：' + ($names -join '，'))
    }

    # 密钥戳：内容随密钥集合变化，用于触发增量构建重新编译
    $stampDir = [System.IO.Path]::GetFullPath((Join-Path (Get-BuildOutputDirectory) '..'))
    New-Item -ItemType Directory -Force -Path $stampDir | Out-Null
    $script:SecretsStampFile = Join-Path $stampDir 'secrets.stamp'
    if ([string]::IsNullOrWhiteSpace($env:PCL_WRITE_SECRET)) {
        $stampContent = 'none'
    }
    else {
        $pairs = @(Get-ChildItem env: | Where-Object {
                $_.Name.StartsWith('PCL_') -and $_.Name -ne 'PCL_WRITE_SECRET' -and -not [string]::IsNullOrWhiteSpace($_.Value)
            } | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" })
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $stampContent = ([System.BitConverter]::ToString(
                $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($pairs -join "`n")))).Replace('-', '').ToLowerInvariant()
        }
        finally { $sha.Dispose() }
    }
    # 只在内容变化时才写，保留 mtime 避免不必要的重新编译
    if (-not (Test-Path -LiteralPath $script:SecretsStampFile -PathType Leaf) -or
        [System.IO.File]::ReadAllText($script:SecretsStampFile) -ne $stampContent) {
        [System.IO.File]::WriteAllText($script:SecretsStampFile, $stampContent)
    }
}

function Main {
    $totalWatch = [System.Diagnostics.Stopwatch]::StartNew()
    if ($Architecture -ne 'x64' -and $Architecture -ne 'arm64') {
        Throw-Single '必须指定 -Architecture x64 或 arm64'
    }
    if (-not (Test-Path -LiteralPath $script:ProjectFile -PathType Leaf)) {
        Throw-Single "找不到工程: $script:ProjectFile"
    }
    $dotnet = Resolve-DotNetExe
    Initialize-ExternalCaches
    $publishDir = Join-Path $script:WorkRoot 'publish'
    $stage = Join-Path $script:WorkRoot 'stage'
    $zip = Join-Path $script:WorkRoot 'payload.zip'
    $stub = Join-Path $script:WorkRoot 'stub.exe'
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    Write-Step "dotnet=$dotnet"
    Write-Step '框架依赖单文件：不自包含、不修剪、原生库打进启动器。外层不再压缩，也不把数据接到 exe 尾部。'
    Write-Step ("构建输出: {0}" -f (Get-BuildOutputDirectory))
    Initialize-Secrets
    Invoke-TimedStep 'dotnet publish' { Invoke-DotNetPublish $dotnet $publishDir }
    $launcher = Join-Path $publishDir 'Plain Craft Launcher 2.exe'
    Invoke-TimedStep '校验原生库' { Assert-NoMissingLibraries $publishDir $launcher }
    Invoke-TimedStep '暂存与 ZIP' {
        Copy-PublishPayload $publishDir $stage
        Copy-FixedRuntime $stage
        New-PayloadZip $stage $zip
    }
    $webview = Test-Path -LiteralPath (Join-Path $stage 'WebView2Runtime\msedgewebview2.exe') -PathType Leaf
    Invoke-TimedStep '编译外层 exe 与 PE 校验' { Compile-Bootstrapper $dotnet $stub $zip (Get-FileSha256 $zip) ([bool]$webview) }
    $outFile = Resolve-OutputFile
    if (Test-Path -LiteralPath $outFile) { Remove-Item -LiteralPath $outFile -Force }
    Copy-Item -LiteralPath $stub -Destination $outFile -Force
    $overlay = Assert-NoPeOverlay $outFile
    $size = (Get-Item -LiteralPath $outFile).Length
    Write-Host ''
    Write-Host 'Single exe OK'
    Write-Host "  output : $outFile"
    Write-Host ("  size   : {0:N1} MiB" -f ($size / 1MB))
    Write-Host "  layout : embedded resource, PE overlay=$overlay"
    Write-Host '  runtime: not bundled; missing libraries are listed before launch'
    Write-Host '  consent: download only after 同意并下载'
    Write-Host '  trim   : false; native libraries required'
    Write-Host ("  time   : {0:N2} 秒" -f $totalWatch.Elapsed.TotalSeconds)
}

function Clear-SingleExeWork {
    if ($script:WorkRoot -and (Test-Path -LiteralPath $script:WorkRoot)) {
        try { Remove-Item -LiteralPath $script:WorkRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
}

$script:ScratchRoot = Get-ScratchRoot
$archFolder = if ([string]::IsNullOrWhiteSpace($Architecture)) { 'self-check' } else { $Architecture }
$script:WorkRoot = Join-Path $script:ScratchRoot ("pcl-cy-single-exe\{0}-{1}" -f $archFolder, $PID)
$script:DotSource = $MyInvocation.InvocationName -eq '.' -or $MyInvocation.Line -match '^\s*\.\s+'
if ($SelfCheck) {
    try { Invoke-SingleExeSelfCheck }
    finally { Clear-SingleExeWork }
    return
}
if (-not $script:DotSource) {
    try { Main }
    finally { Clear-SingleExeWork }
}
