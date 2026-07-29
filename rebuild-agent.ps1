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
$agentExe = Join-Path $repoRoot 'target\debug\wuji-rebuild-agent-v01.exe'
$cargo = Get-RequiredCommand 'cargo.exe'

Write-Host 'Building the debug Agent...'
Push-Location $repoRoot
try {
    & $cargo build -p wuji-rebuild-agent
    Assert-LastExitCode 'Agent debug build'
}
finally {
    Pop-Location
}

Write-Host "Agent build completed: $agentExe"
