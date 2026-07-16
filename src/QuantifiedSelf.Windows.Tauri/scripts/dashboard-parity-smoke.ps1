[CmdletBinding()]
param(
    [string]$ReportDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'WUJI.Smoke')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$tauriRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$bridgePath = Join-Path $tauriRoot 'src-tauri\sidecars\bridge\QuantifiedSelf.Windows.Client.Bridge.exe'
$toolProject = Join-Path $repoRoot 'tools\QuantifiedSelf.Windows.DashboardParity\QuantifiedSelf.Windows.DashboardParity.csproj'
$wpfTests = Join-Path $repoRoot 'tests\QuantifiedSelf.Windows.App.Tests\QuantifiedSelf.Windows.App.Tests.csproj'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportPath = Join-Path $ReportDirectory "dashboard-parity-$runId.md"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string]$Label,
        [Parameter(Mandatory)] [scriptblock]$Action
    )

    Write-Host "`n=== $Label ===" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed, exit code=$LASTEXITCODE"
    }
}

New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null

Push-Location $tauriRoot
try {
    Invoke-Checked '1/7 Prepare the fixed dev Bridge' { pnpm.cmd bridge:prepare }
    Invoke-Checked '2/7 Build React route chunks' { pnpm.cmd build }

    Write-Host "`n=== 3/7 Check route chunks and bundle budgets ===" -ForegroundColor Cyan
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $bundleOutput = @(& pnpm.cmd bundle:check 2>&1 | ForEach-Object { $_.ToString() })
    $bundleExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    $bundleOutput | ForEach-Object { Write-Host $_ }
    if ($bundleExitCode -ne 0) {
        throw "Bundle budget failed, exit code=$bundleExitCode"
    }

    Invoke-Checked '4/7 Verify React states and formatting' { pnpm.cmd test }
}
finally {
    Pop-Location
}

Invoke-Checked '5/7 Verify WPF Dashboard Empty/Error states' {
    dotnet test $wpfTests --no-restore --filter 'FullyQualifiedName~TodayPageTests'
}

Invoke-Checked '6/7 Compare the same dev data' {
    dotnet run --project $toolProject -- --bridge $bridgePath --report $reportPath
}

$reportLines = @(
    ''
    '## UI states and bundle acceptance'
    ''
    '- React Dashboard states, retry, and locale-aware formatting: PASS (Vitest)'
    '- WPF Dashboard Empty/Error: PASS (TodayPageTests)'
    '- Route-level chunks and bundle budgets: PASS'
    ''
    '```text'
) + $bundleOutput + @('```')
Add-Content -LiteralPath $reportPath -Encoding UTF8 -Value $reportLines

Write-Host "`n=== 7/7 Final result ===" -ForegroundColor Cyan
Write-Host 'Stage 4D Dashboard parity acceptance: PASS' -ForegroundColor Green
Write-Host "Report: $reportPath"
