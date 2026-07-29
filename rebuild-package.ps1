$ErrorActionPreference = 'Stop'

function Get-RequiredCommand {
    param([Parameter(Mandatory = $true)][string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Required command was not found: $Name"
    }
    return $command.Source
}

function Assert-LastExitCode {
    param([Parameter(Mandatory = $true)][string]$Step)

    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE"
    }
}

$repoRoot = $PSScriptRoot
$desktopDir = Join-Path $repoRoot 'rebuild\apps\desktop'
$packageScript = Join-Path $repoRoot 'rebuild\scripts\build_dev_package.py'

if (-not (Test-Path -LiteralPath $packageScript)) {
    throw "Package script was not found: $packageScript"
}

$pnpm = Get-RequiredCommand 'pnpm.cmd'
$python = Get-RequiredCommand 'python.exe'

if (-not (Test-Path -LiteralPath (Join-Path $desktopDir 'node_modules'))) {
    Write-Host 'Installing frontend dependencies...'
    Push-Location $desktopDir
    try {
        & $pnpm install --frozen-lockfile
        Assert-LastExitCode 'pnpm install'
    }
    finally {
        Pop-Location
    }
}

Write-Host 'Building and validating the WUJI Rebuild NSIS package...'
Push-Location $repoRoot
try {
    & $python $packageScript
    Assert-LastExitCode 'Rebuild package validation'
}
finally {
    Pop-Location
}

$installerDir = Join-Path $repoRoot 'rebuild\target\release\bundle\nsis'
Write-Host "Package completed: $installerDir"
