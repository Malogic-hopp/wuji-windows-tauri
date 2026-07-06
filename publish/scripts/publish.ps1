#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publish WUJI App and Agent as self-contained win-x64, then assemble outputs.
.DESCRIPTION
    1. Publish QuantifiedSelf.Windows.App  -> <OutputPath>/App
    2. Publish QuantifiedSelf.Windows.Agent -> <OutputPath>/Agent
    3. Copy Agent publish outputs into App/Agent so dependencies stay isolated
    4. Verify both executables exist

    Both projects use FolderProfile.pubxml (self-contained, win-x64, Release).
    Output directory is always overridden by this script.
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER RuntimeIdentifier
    Target runtime identifier (default: win-x64).
.PARAMETER OutputPath
    Root output directory (default: publish/release).
    Script creates App/ and Agent/ subdirectories inside.
#>

param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputPath = "publish/release"
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$PublishRootRaw = Join-Path $RepoRoot $OutputPath
New-Item -ItemType Directory -Path $PublishRootRaw -Force | Out-Null
$PublishRoot = (Resolve-Path $PublishRootRaw).Path
$AppPublishDir = Join-Path $PublishRoot "App"
$AgentPublishDir = Join-Path $PublishRoot "Agent"

$AppProject = "$RepoRoot/src/QuantifiedSelf.Windows.App/QuantifiedSelf.Windows.App.csproj"
$AgentProject = "$RepoRoot/src/QuantifiedSelf.Windows.Agent/QuantifiedSelf.Windows.Agent.csproj"

$AppExeName = "QuantifiedSelf.Windows.App.exe"
$AgentExeName = "QuantifiedSelf.Windows.Agent.exe"
$AgentSubdirName = "Agent"

# Safe path display: show paths relative to $RepoRoot (string-based, no Resolve-Path)
function Write-SafePath($label, $fullPath) {
    $repoSep = $RepoRoot.TrimEnd('\', '/') + '\'
    if ($fullPath.StartsWith($repoSep, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $fullPath.Substring($repoSep.Length)
        Write-Host "  $label"  "<workspace>/$relative"
    } else {
        Write-Host "  $label"  "<workspace>/publish/release/..."
    }
}

Write-Host "=== WUJI Publish Script ==="
Write-Host "  Configuration:  $Configuration"
Write-Host "  Runtime:        $RuntimeIdentifier"
Write-SafePath "App publish:" $AppPublishDir
Write-SafePath "Agent publish:" $AgentPublishDir
Write-Host ""

# Ensure output directories are clean
if (Test-Path $AppPublishDir) { Remove-Item -Recurse -Force $AppPublishDir }
if (Test-Path $AgentPublishDir) { Remove-Item -Recurse -Force $AgentPublishDir }
New-Item -ItemType Directory -Path $AppPublishDir -Force | Out-Null
New-Item -ItemType Directory -Path $AgentPublishDir -Force | Out-Null

Write-Host "--- Step 1: Publishing App (self-contained $RuntimeIdentifier $Configuration) ---"
dotnet publish $AppProject `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $AppPublishDir `
    -p:PublishProfile=FolderProfile `
    -p:PublishDir=$AppPublishDir
if ($LASTEXITCODE -ne 0) { throw "App publish failed (exit code $LASTEXITCODE)." }
Write-Host "App publish succeeded."
Write-Host ""

Write-Host "--- Step 2: Publishing Agent (self-contained $RuntimeIdentifier $Configuration) ---"
dotnet publish $AgentProject `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $AgentPublishDir `
    -p:PublishProfile=FolderProfile `
    -p:PublishDir=$AgentPublishDir
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed (exit code $LASTEXITCODE)." }
Write-Host "Agent publish succeeded."
Write-Host ""

Write-Host "--- Step 3: Copy Agent publish directory into App/Agent ---"
# App and Agent are both self-contained executables. Their dependency sets can
# contain different versions of the same file name, so never flatten Agent files
# into the App root. Keep Agent in an isolated subdirectory and let the App
# launcher resolve AppContext.BaseDirectory\Agent\QuantifiedSelf.Windows.Agent.exe.
$RootAgentArtifacts = Join-Path $AppPublishDir "QuantifiedSelf.Windows.Agent.*"
Get-ChildItem -Path $RootAgentArtifacts -File -ErrorAction SilentlyContinue | Remove-Item -Force

$EmbeddedAgentDir = Join-Path $AppPublishDir $AgentSubdirName
if (Test-Path $EmbeddedAgentDir) { Remove-Item -Recurse -Force $EmbeddedAgentDir }
New-Item -ItemType Directory -Path $EmbeddedAgentDir -Force | Out-Null
Copy-Item -Path (Join-Path $AgentPublishDir "*") -Destination $EmbeddedAgentDir -Recurse -Force
$copiedCount = (Get-ChildItem -Path $EmbeddedAgentDir -Recurse -File).Count
Write-Host "Agent files: $copiedCount copied into App/$AgentSubdirName."
Write-Host ""

Write-Host "--- Step 4: Verify executables ---"
$AppExePath = Join-Path $AppPublishDir $AppExeName
$EmbeddedAgentDir = Join-Path $AppPublishDir $AgentSubdirName
$AgentExePath = Join-Path $EmbeddedAgentDir $AgentExeName

if (-not (Test-Path $AppExePath)) {
    throw "App executable not found in publish output."
}
Write-Host "  [OK] App executable present."

if (-not (Test-Path $AgentExePath)) {
    throw "Agent executable not found in publish output."
}
Write-Host "  [OK] Agent executable present."

# Verify self-contained: check for .deps.json and runtime files
$appDeps = Join-Path $AppPublishDir "QuantifiedSelf.Windows.App.deps.json"
$agentDeps = Join-Path $EmbeddedAgentDir "QuantifiedSelf.Windows.Agent.deps.json"
if (Test-Path $appDeps) { Write-Host "  [OK] App deps.json present (self-contained)." }
if (Test-Path $agentDeps) { Write-Host "  [OK] Agent deps.json present (self-contained)." }

$rootAgentExe = Join-Path $AppPublishDir $AgentExeName
if (Test-Path $rootAgentExe) {
    throw "Agent executable must be isolated under App/$AgentSubdirName, not App root."
}

# Check no output is under bin/Debug or obj
if ($AppPublishDir -match '(\\|/)bin(\\|/)Debug' -or $AppPublishDir -match '(\\|/)obj(\\|/)') {
    throw "Publish output directory appears to be a dev build path — aborting."
}

Write-Host ""
Write-Host "=== Publish complete ==="
Write-SafePath "App + Agent assembled in:" $AppPublishDir
Write-SafePath "Agent publish stage in:" $AgentPublishDir
Write-Host ""
Write-Host "Executable names preserved (not renamed):"
Write-Host "  $AppExeName"
Write-Host "  $AgentSubdirName/$AgentExeName"
