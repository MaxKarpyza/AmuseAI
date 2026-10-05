<#
.SYNOPSIS
    Builds Amuse and deploys a portable copy into an existing installation folder
    WITHOUT wiping it.

.DESCRIPTION
    `dotnet publish` only writes the files it produces, so the heavy runtime folders
    (RuntimeHuggingFace, RuntimeStableDiffusionCpp, Plugins) and the user profile
    (Settings.json) are left alone. This script adds the rest of the safety:

      * refuses to run while Amuse is running (files would be locked);
      * snapshots the user-editable JSON files and puts them back afterwards, so
        Templates.json / Settings.backup.json are not reset by the deploy;
      * measures the data folders before and after and fails loudly if anything
        shrank, so a bad run can never silently eat downloaded models.

    Icons need no paid assets: the build bundles Font Awesome Free (CC BY 4.0).
    Pass -UseFontAwesomePro to use licensed Font Awesome Pro fonts instead.

.EXAMPLE
    pwsh -File Scripts/publish-portable.ps1 -OutputDir C:\ai\Amuse

.EXAMPLE
    pwsh -File Scripts/publish-portable.ps1 -OutputDir C:\ai\Amuse -Configuration Release
#>
[CmdletBinding()]
param(
    [string] $OutputDir    = 'C:\ai\Amuse',
    [string] $Configuration = 'Release',
    [string] $Runtime       = 'win-x64',
    [switch] $SelfContained,
    [switch] $UseFontAwesomePro,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot

# Projects that make up the portable distribution. They are published into ONE
# folder on purpose: the csproj sets ErrorOnDuplicatePublishOutputFiles=false,
# so the shared native dependencies are written once and all executables sit
# side by side next to Settings.json.
$projects = @(
    'Amuse.App\Amuse.App.csproj'
    'Amuse.Host.HuggingFace\Amuse.Host.HuggingFace.csproj'
    'Amuse.Host.OnnxRuntime\Amuse.Host.OnnxRuntime.csproj'
    'Amuse.Host.StableDiffusionCpp\Amuse.Host.StableDiffusionCpp.csproj'
)

# User data that must survive the deploy. Templates.json is re-emitted by the
# build, so it is snapshotted and restored; Settings.json is written by the app
# at runtime but is listed here as a guard.
$preservedFiles = @('Settings.json', 'Settings.backup.json', 'Templates.json')

# Directories holding downloaded models / pipelines. These are never written by
# the build; their sizes are checked before and after to prove it.
$dataDirs = @('RuntimeHuggingFace', 'RuntimeStableDiffusionCpp', 'Plugins', 'History', 'Logs', 'Temp')

function Write-Step($message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Get-DirSize([string] $path) {
    if (-not (Test-Path -LiteralPath $path)) { return 0L }
    # A folder can exist and be empty (History/Temp/Logs on a fresh install), and
    # Measure-Object emits no object at all for empty input - which would make .Sum
    # throw under StrictMode. Materialise the list first.
    $items = @(Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue)
    if ($items.Count -eq 0) { return 0L }
    $sum = ($items | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0L }
    return [int64] $sum
}

function Get-Snapshot($dir) {
    $result = @{}
    foreach ($name in $dataDirs) {
        $result[$name] = Get-DirSize (Join-Path $dir $name)
    }
    return $result
}

# --- preflight -------------------------------------------------------------

Write-Step "Resolving output directory: $OutputDir"
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    Write-Host "    created a new folder" -ForegroundColor Yellow
}
$resolved = (Resolve-Path -LiteralPath $OutputDir).Path
if (-not $resolved.StartsWith('C:\ai', [System.StringComparison]::OrdinalIgnoreCase) -and
    -not $resolved.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Warning "Refusing to deploy into $resolved - it is neither under C:\ai nor inside the repo."
    exit 2
}

$running = Get-Process -Name 'Amuse', 'AmuseHost.*' -ErrorAction SilentlyContinue |
    Where-Object {
        # only a process actually living in the target folder would block the deploy
        $path = $null
        try { $path = $_.Path } catch { $path = $null }
        $path -and $path.StartsWith($resolved, [System.StringComparison]::OrdinalIgnoreCase)
    }
if ($running) {
    Write-Host "Amuse is running from $resolved. Close it, then re-run." -ForegroundColor Red
    $running | Select-Object Id, ProcessName, Path | Format-Table -AutoSize
    exit 3
}

Write-Step 'Recording data-folder sizes (proof they survive)'
$before = Get-Snapshot $OutputDir
foreach ($name in $dataDirs) {
    if ($before[$name] -gt 0) {
        Write-Host ("    {0,-28} {1,10:N1} MB" -f $name, ($before[$name] / 1MB))
    }
}

$backupDir = Join-Path ([System.IO.Path]::GetTempPath()) ("amuse-publish-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
$backedUp = @{}
foreach ($name in $preservedFiles) {
    $path = Join-Path $OutputDir $name
    if (Test-Path -LiteralPath $path) {
        Copy-Item -LiteralPath $path -Destination (Join-Path $backupDir $name) -Force
        $backedUp[$name] = $path
        Write-Host "    saved $name ($((Get-Item $path).Length) bytes)"
    }
}

# --- build + publish -------------------------------------------------------

# The build is staged in a throwaway folder and then copied over the target.
#
# This is not belt-and-braces, it is required. `dotnet publish` treats its output
# folder as build state: on an incremental (no-op) build the SDK cleans files it
# believes are no longer produced. Pointing it straight at an existing install
# therefore ERASES payloads - observed: the whole publish dir reduced to 6 files,
# and the 61 MB Plugins\Preview\*.onnx went missing. Staging keeps the target
# folder under our control: we only ever add or overwrite the files the build
# produced, and never delete anything.
$stageDir = Join-Path ([System.IO.Path]::GetTempPath()) ("amuse-stage-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

$publishArgs = @(
    'publish'
    '-c', $Configuration
    '-r', $Runtime
    '-o', $stageDir
    '-p:PublishSingleFile=true'
    '-p:ErrorOnDuplicatePublishOutputFiles=false'
    # CopyUnchangedFiles compares TIMESTAMPS. A leftover file from an earlier build
    # is normally older and gets replaced, but one that happens to be newer-dated
    # (a different .NET servicing band, a restored backup) is skipped even when its
    # contents differ - which leaves e.g. wpfgfx_cor3.dll from .NET 10.0.x next to
    # 10.0.12 managed WPF and crashes at startup with EntryPointNotFoundException.
    '-p:SkipCopyUnchangedFiles=false'
    '-p:DebugType=none'
    '-p:DebugSymbols=false'
    '-m:1'
    '--nologo'
    '-v', 'minimal'
)
if ($SelfContained) { $publishArgs += '-p:SelfContained=true' }
if ($UseFontAwesomePro) { $publishArgs += '-p:UseFontAwesomePro=true' }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

try {
    if (-not $SkipBuild) {
        # Published one project at a time on purpose. Passing --output to the .sln
        # is rejected by the SDK (NETSDK1194) and a solution-level build also races
        # the host projects, which share Amuse.App\bin through BaseOutputPath.
        Write-Step "Publishing $Configuration ($Runtime) into a staging folder"
        foreach ($project in $projects) {
            $path = Join-Path $repoRoot $project
            Write-Host "    dotnet publish $project" -ForegroundColor DarkGray
            & dotnet @publishArgs $path
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet publish failed for $project with exit code $LASTEXITCODE"
            }
        }
    }

    # --- stage the build over the target ------------------------------------
    Write-Step "Copying the build into $OutputDir (existing files overwritten, nothing deleted)"
    $staged = @(Get-ChildItem -LiteralPath $stageDir -Recurse -File)
    Write-Host "    $($staged.Count) file(s), $([math]::Round((($staged | Measure-Object Length -Sum).Sum) / 1MB, 1)) MB"
    foreach ($file in $staged) {
        $relative = $file.FullName.Substring($stageDir.Length).TrimStart('\', '/')
        $dest = Join-Path $OutputDir $relative
        $destDir = Split-Path -Parent $dest
        if (-not (Test-Path -LiteralPath $destDir)) {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $dest -Force
    }
}
finally {
    # --- restore user data --------------------------------------------------
    if ($backedUp.Count -gt 0) {
        Write-Step 'Restoring user data'
        foreach ($name in $backedUp.Keys) {
            Copy-Item -LiteralPath (Join-Path $backupDir $name) -Destination $backedUp[$name] -Force
            Write-Host "    restored $name"
        }
    }
    Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stageDir  -Recurse -Force -ErrorAction SilentlyContinue
}

# --- verify ----------------------------------------------------------------

# Build outputs the app cannot start without. Verified by name so a deploy that
# silently skips a file fails here instead of crashing on first launch.
$requiredFiles = @(
    'Amuse.exe'
    'AmuseHost.HuggingFace.exe'
    'AmuseHost.OnnxRuntime.exe'
    'AmuseHost.StableDiffusionCpp.exe'
    'CSnakes.Runtime.dll'
    'ffmpeg.exe'
    'ffprobe.exe'
    'Interpolation.onnx'
    'Settings.default.json'
    'Templates.json'
    'Plugins\Preview\Flux1.onnx'
    'Plugins\Preview\Qwen.onnx'
    'Plugins\Preview\StableDiffusionXL.onnx'
)

Write-Step 'Verifying the deploy'
$after = Get-Snapshot $OutputDir
$failed = $false
foreach ($name in $dataDirs) {
    if ($before[$name] -eq 0) { continue }
    $delta = $after[$name] - $before[$name]
    if ($delta -lt 0) {
        Write-Host ("    {0,-28} LOST {1,10:N1} MB" -f $name, ($delta / 1MB)) -ForegroundColor Red
        $failed = $true
    }
    else {
        Write-Host ("    {0,-28} ok   {1,10:N1} MB" -f $name, ($after[$name] / 1MB)) -ForegroundColor Green
    }
}

$exe = Join-Path $OutputDir 'Amuse.exe'
if (Test-Path -LiteralPath $exe) {
    Write-Host ("    {0,-28} {1,10:N1} MB" -f 'Amuse.exe', ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
}
else {
    Write-Host '    Amuse.exe is MISSING' -ForegroundColor Red
    $failed = $true
}

foreach ($name in $requiredFiles) {
    $path = Join-Path $OutputDir $name
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host ("    {0,-28} MISSING" -f $name) -ForegroundColor Red
        $failed = $true
    }
}
if (-not $failed) {
    Write-Host ("    {0,-28} all {1} required files present" -f 'payload', $requiredFiles.Count) -ForegroundColor Green
}

# WPF loads its native graphics stack from the app folder when it is present. A
# file left behind by an earlier build then disagrees with the managed WPF in the
# fresh bundle and the process dies during Splashscreen InitializeComponent with
# EntryPointNotFoundException. Prove every native is byte-identical to the runtime
# this build was compiled against.
Write-Step 'Verifying WPF native binaries match the build runtime'
# Sort by parsed version, not by name: a string sort puts "8.0.30" above "10.0.12"
# and would compare the build against the wrong runtime.
function Get-NewestFramework([string] $sharedRoot) {
    if (-not $sharedRoot -or -not (Test-Path -LiteralPath $sharedRoot)) { return $null }
    Get-ChildItem -LiteralPath $sharedRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
        Sort-Object { [version] $_.Name } -Descending |
        Select-Object -First 1
}

$wpfDir = $null
if ($env:DOTNET_ROOT) {
    $wpfDir = Get-NewestFramework (Join-Path $env:DOTNET_ROOT 'shared\Microsoft.WindowsDesktop.App')
}
if (-not $wpfDir) {
    # Walk up from dotnet.exe until a 'shared' directory turns up. The muxer sits
    # at <root>\dotnet.exe, so a fixed number of Split-Path calls gets it wrong.
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if ($dotnet) {
        $dir = Split-Path -Parent $dotnet
        for ($i = 0; $i -lt 5 -and $dir; $i++) {
            $wpfDir = Get-NewestFramework (Join-Path $dir 'shared\Microsoft.WindowsDesktop.App')
            if ($wpfDir) { break }
            $parent = Split-Path -Parent $dir
            if ($parent -eq $dir) { break }
            $dir = $parent
        }
    }
}

$nativeFiles = @('wpfgfx_cor3.dll', 'PresentationNative_cor3.dll', 'D3DCompiler_47_cor3.dll',
                  'PenImc_cor3.dll', 'vcruntime140_cor3.dll')
if ($wpfDir) {
    Write-Host "    runtime: $($wpfDir.Name)"
    foreach ($name in $nativeFiles) {
        $src = Join-Path $wpfDir.FullName $name
        $dst = Join-Path $OutputDir $name
        if (-not (Test-Path -LiteralPath $src)) { continue }
        if (-not (Test-Path -LiteralPath $dst)) {
            Write-Host "    $name is MISSING" -ForegroundColor Red
            $failed = $true
            continue
        }
        $a = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash
        $b = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
        if ($a -eq $b) {
            Write-Host ("    {0,-28} ok" -f $name) -ForegroundColor Green
        }
        else {
            Write-Host ("    {0,-28} STALE - differs from {1}" -f $name, $wpfDir.Name) -ForegroundColor Red
            $failed = $true
        }
    }
}
else {
    Write-Warning 'Could not locate Microsoft.WindowsDesktop.App to verify against; skipped.'
}

if ($failed) {
    Write-Host "`nDeploy verification FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "`nPortable build is ready: $OutputDir\Amuse.exe" -ForegroundColor Green
exit 0
