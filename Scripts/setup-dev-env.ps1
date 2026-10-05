<#
.SYNOPSIS
    Prepares the two-repository layout that Amuse's Debug configuration needs.

.DESCRIPTION
    Amuse's Debug configuration references the TensorStack projects as
    ProjectReference items with ..\..\ paths, so from Amuse.App\ they resolve to
    <root>\TensorStack.WPF\ ... - siblings of the Amuse folder. TensorStack is a
    single repository that contains all eight projects, so a plain clone puts them
    one level too deep.

    This script clones TensorStack once and exposes each project as a directory
    junction at the expected path, then writes a Directory.Build.props next to the
    junctions that imports TensorStack's own props file.

    That props shim is not optional. The TensorStack projects declare no
    TargetFramework themselves - it comes from TensorStack\Directory.Build.props,
    which MSBuild discovers by walking up from the project directory. Through a
    junction the walk never reaches TensorStack\, every project then reports
    "does not support the target platform" (NU1201) and the Debug build dies during
    restore.

    The shim lives outside both repositories on purpose so neither git tree sees
    it, which is also why this script exists: it is what recreates that wiring.

.EXAMPLE
    pwsh -File Scripts/setup-dev-env.ps1

.EXAMPLE
    pwsh -File Scripts/setup-dev-env.ps1 -Root D:\src
#>
[CmdletBinding()]
param(
    # Parent folder that must contain both the Amuse clone and the junctions.
    # Defaults to the parent of the Amuse repository.
    [string] $Root = '',
    [string] $TensorStackUrl = 'https://github.com/saddam213/TensorStack.git',
    [string] $Branch = 'master'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is not reliably populated inside a param default, so resolve the
# root here instead.
if (-not $Root) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    # <root>\Amuse\Scripts  ->  <root>
    $Root = Split-Path -Parent (Split-Path -Parent $here)
}

$projects = @(
    'TensorStack.Common'
    'TensorStack.HuggingFace'
    'TensorStack.Media'
    'TensorStack.OnnxRuntime'
    'TensorStack.OnnxRuntime.LLM'
    'TensorStack.OnnxRuntime.Vision'
    'TensorStack.StableDiffusionCpp'
    'TensorStack.WPF'
)

function Write-Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

if (-not (Test-Path -LiteralPath (Join-Path $Root 'Amuse\Amuse.sln'))) {
    Write-Error "No Amuse clone at $(Join-Path $Root 'Amuse'). Run this from inside the Amuse repository."
    exit 2
}

$clone = Join-Path $Root 'TensorStack'

# --- clone -----------------------------------------------------------------
if (Test-Path -LiteralPath (Join-Path $clone '.git')) {
    Write-Step "TensorStack already cloned at $clone"
}
else {
    Write-Step "Cloning $TensorStackUrl"
    & git clone --branch $Branch --progress $TensorStackUrl $clone
    if ($LASTEXITCODE -ne 0) { throw "git clone failed with exit code $LASTEXITCODE" }
}

# --- junctions -------------------------------------------------------------
Write-Step 'Exposing each TensorStack project where Amuse expects it'
foreach ($name in $projects) {
    $target = Join-Path $clone $name
    $link = Join-Path $Root $name
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Warning "missing from the clone: $name"
        continue
    }
    if (Test-Path -LiteralPath $link) {
        $item = Get-Item -LiteralPath $link -Force
        $isJunction = $item.Attributes -band [IO.FileAttributes]::ReparsePoint
        Write-Host ("    {0,-34} {1}" -f $name, $(if ($isJunction) { 'junction already present' } else { 'exists (left alone)' }))
        continue
    }
    New-Item -ItemType Junction -Path $link -Target $target | Out-Null
    Write-Host ("    {0,-34} junction created" -f $name)
}

# --- props shim ------------------------------------------------------------
$shim = Join-Path $Root 'Directory.Build.props'
if (Test-Path -LiteralPath $shim) {
    Write-Step "Directory.Build.props already present at $shim"
}
else {
    Write-Step 'Writing the Directory.Build.props shim'
    @'
<!--
  Written by Scripts/setup-dev-env.ps1. See that script for why this is needed.

  The TensorStack.* folders here are directory junctions into a single clone of
  TensorStack. Those projects take their TargetFramework from
  TensorStack\Directory.Build.props, which MSBuild finds by walking up from the
  project directory; through a junction the walk never reaches TensorStack\, so
  every project reports NU1201 "does not support the target platform".

  Importing the real props file fixes it. Amuse is unaffected: MSBuild stops at
  the nearest Directory.Build.props and Amuse has its own.

  Kept outside both repositories so neither git tree sees it.
-->
<Project>
  <Import Project="TensorStack\Directory.Build.props" />
</Project>
'@ | Set-Content -LiteralPath $shim -Encoding UTF8
    Write-Host "    $shim"
}

# --- verify ----------------------------------------------------------------
Write-Step 'Verifying'
$bad = 0
foreach ($name in $projects) {
    $proj = Join-Path (Join-Path $Root $name) "$name.csproj"
    if (-not (Test-Path -LiteralPath $proj)) {
        Write-Host ("    {0,-34} csproj NOT FOUND" -f $name) -ForegroundColor Red
        $bad++
    }
}
if ($bad -eq 0) {
    Write-Host "    all $($projects.Count) project paths resolve" -ForegroundColor Green
}

Write-Step 'Building both configurations'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
foreach ($cfg in @('Debug', 'Release')) {
    Write-Host "    dotnet build Amuse.sln -c $cfg" -ForegroundColor DarkGray
    & dotnet build (Join-Path $Root 'Amuse\Amuse.sln') -c $cfg -v m --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "    $cfg FAILED" -ForegroundColor Red
        $bad++
    }
}

if ($bad -gt 0) {
    Write-Host "`nSetup incomplete: $bad problem(s)." -ForegroundColor Red
    exit 1
}

Write-Host "`nDev environment ready. Debug uses TensorStack from source; Release uses the published NuGet packages." -ForegroundColor Green
exit 0
