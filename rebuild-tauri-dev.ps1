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
$desktopDir = Join-Path $repoRoot 'apps\desktop'
$agentExe = Join-Path $repoRoot 'target\debug\wuji-rebuild-agent-v01.exe'

$pnpm = Get-RequiredCommand 'pnpm.cmd'

# Always isolate Tauri development from the legacy WUJI/WUJI-Dev databases.
$env:WUJI_REBUILD_CHANNEL = 'rebuild-v01-dev'

if (-not (Test-Path -LiteralPath $agentExe)) {
    Write-Warning 'The debug Agent does not exist. Run rebuild-agent.ps1 separately if the UI needs to start it.'
}

Push-Location $desktopDir
try {
    if (-not (Test-Path -LiteralPath (Join-Path $desktopDir 'node_modules'))) {
        Write-Host 'Installing frontend dependencies...'
        & $pnpm install --frozen-lockfile
        Assert-LastExitCode 'pnpm install'
    }

    Write-Host 'Starting Tauri development mode...'
    & $pnpm tauri dev
    Assert-LastExitCode 'Tauri development mode'
}
finally {
    Pop-Location
}
