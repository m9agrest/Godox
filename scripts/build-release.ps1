[CmdletBinding()]
param([switch]$SkipInstaller)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location -LiteralPath $root
$cache = Join-Path $root '.build-cache'
$dist = Join-Path $root 'dist'
$publish = Join-Path $dist 'Godox-win-x64'
$pythonVersion = '3.12.10'
$pythonHash = '4ACBED6DD1C744B0376E3B1CF57CE906F9DC9E95E68824584C8099A63025A3C3'
$archiveName = "python-$pythonVersion-embed-amd64.zip"
$archive = Join-Path $cache $archiveName
$project = Join-Path $root 'src/Godox.Desktop/Godox.Desktop.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version

function Run([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe failed with exit code $LASTEXITCODE" }
}

function Hash-File([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Clear-PublishDirectory {
    $resolved = [IO.Path]::GetFullPath($publish)
    $expected = [IO.Path]::GetFullPath((Join-Path $root 'dist/Godox-win-x64'))
    if ($resolved -ne $expected -or -not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clear a directory outside this project.'
    }
    foreach ($entry in @($dist, $publish)) {
        if ((Test-Path -LiteralPath $entry) -and ((Get-Item -LiteralPath $entry).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Build directory must not be a link: $entry"
        }
    }
    if (Test-Path -LiteralPath $resolved) {
        if (Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) {
            throw 'Publish directory contains a link; refusing recursive cleanup.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
}

try {
    Get-Command dotnet, git -ErrorAction Stop | Out-Null
    $python = if (Test-Path 'test/.venv/Scripts/python.exe') {
        (Resolve-Path 'test/.venv/Scripts/python.exe').Path
    } else { (Get-Command python -ErrorAction Stop).Source }
    Run $python @('-c', "import sys,struct; assert sys.version_info[:2] == (3,12) and struct.calcsize('P') == 8, 'Build requires Python 3.12 x64'")
    $iscc = $null
    if (-not $SkipInstaller) {
        $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($compiler) { $iscc = $compiler.Source }
        else {
            $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
            if (-not (Test-Path -LiteralPath $iscc)) { throw 'Install Inno Setup 6, add ISCC.exe to PATH, or pass -SkipInstaller.' }
        }
    }
    New-Item -ItemType Directory -Path $cache, $dist -Force | Out-Null
    if (-not (Test-Path -LiteralPath $archive)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $partial = $archive + '.download'
        Invoke-WebRequest -UseBasicParsing -Uri "https://www.python.org/ftp/python/$pythonVersion/$archiveName" -OutFile $partial
        if ((Hash-File $partial) -ne $pythonHash) { throw 'Python download checksum mismatch.' }
        Move-Item -LiteralPath $partial -Destination $archive -Force
    }
    if ((Hash-File $archive) -ne $pythonHash) { throw 'Cached Python checksum mismatch.' }

    $requirements = Join-Path $root 'packaging/requirements-win-x64.txt'
    $stamp = (Hash-File $requirements).Substring(0, 16)
    $wheels = Join-Path $cache "wheels-$stamp"
    New-Item -ItemType Directory -Path $wheels -Force | Out-Null
    Run $python @('-m', 'pip', 'wheel', '--only-binary=:all:', '--wheel-dir', $wheels, '-r', $requirements)

    Clear-PublishDirectory
    Run 'dotnet' @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $publish, '--nologo')
    $packJson = & dotnet msbuild $project -t:ResolveFrameworkReferences -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -getItem:ResolvedRuntimePack -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve .NET runtime license locations.' }
    $runtimePacks = ($packJson -join "`n" | ConvertFrom-Json).Items.ResolvedRuntimePack
    if (-not $runtimePacks) { throw 'No .NET runtime packs resolved.' }

    $runtime = Join-Path $publish 'runtime/python'
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $runtime)
    $packages = Join-Path $runtime 'Lib/site-packages'
    $runtimeRequirements = Join-Path $cache 'runtime-requirements.txt'
    # Installation uses the wheels just built, not Git or pip on the target PC.
    (Get-Content -LiteralPath $requirements) -replace '^godox-mesh-bt @ .+$', 'godox-mesh-bt==1.0.0' |
        Set-Content -LiteralPath $runtimeRequirements -Encoding ASCII
    Run $python @('-m', 'pip', 'install', '--no-index', '--find-links', $wheels, '--no-compile',
        '--target', $packages, '-r', $runtimeRequirements)
    @('python312.zip', '.', 'Lib/site-packages', '../../backend') |
        Set-Content -LiteralPath (Join-Path $runtime 'python312._pth') -Encoding ASCII
    # Pip's installation provenance can contain local builder paths; it is not required at runtime.
    Get-ChildItem -LiteralPath $packages -Filter direct_url.json -Recurse -File |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    # The worker imports modules; pip's optional CLI launchers embed the build interpreter path.
    $cliDirectory = Join-Path $packages 'bin'
    if (Test-Path -LiteralPath $cliDirectory) {
        Get-ChildItem -LiteralPath $cliDirectory -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
        Remove-Item -LiteralPath $cliDirectory
    }
    New-Item -ItemType Directory -Path (Join-Path $publish 'backend') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'backend/worker.py') -Destination (Join-Path $publish 'backend/worker.py')
    foreach ($name in @('LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $publish $name)
    }
    Copy-Item -LiteralPath (Join-Path $root 'docs/licenses') -Destination (Join-Path $publish 'licenses') -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination (Join-Path $publish 'docs') -Recurse
    New-Item -ItemType Directory -Path (Join-Path $publish 'assets') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'assets/NOTICE.md') -Destination (Join-Path $publish 'assets/NOTICE.md')
    Copy-Item -LiteralPath (Join-Path $root 'assets/NOTICE.md') -Destination (Join-Path $publish 'licenses/Godox-icon-NOTICE.md')
    foreach ($pack in $runtimePacks) {
        $destination = Join-Path $publish ("licenses/dotnet/{0}/{1}" -f $pack.Identity, $pack.NuGetPackageVersion)
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $notices = @(Get-ChildItem -LiteralPath $pack.PackageDirectory -File | Where-Object Name -Match 'license|notice')
        if (-not $notices) { throw "Missing license files for $($pack.Identity)." }
        $notices | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination }
    }
    @{ version = $version; runtime = 'win-x64'; selfContained = $true; python = $pythonVersion; pythonArchiveSha256 = $pythonHash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'build-info.json') -Encoding UTF8
    Run (Join-Path $runtime 'python.exe') @('-B', '-c', "import worker,bleak,cryptography; print('Bundled Python and Bluetooth dependencies: OK')")
    $portable = Join-Path $dist "Godox-Desktop-$version-win-x64.zip"
    if (Test-Path -LiteralPath $portable) { Remove-Item -LiteralPath $portable }
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $portable, [IO.Compression.CompressionLevel]::Optimal, $false)
    if (-not $SkipInstaller) { Run $iscc @('/Qp', "/DMyAppVersion=$version", (Join-Path $root 'innoSetup.iss')) }
    Write-Host "Portable: $portable"
    if (-not $SkipInstaller) { Write-Host "Installer: $(Join-Path $dist "installer/Godox-Desktop-$version-Setup-x64.exe")" }
} catch {
    Write-Error $_
    exit 1
}
