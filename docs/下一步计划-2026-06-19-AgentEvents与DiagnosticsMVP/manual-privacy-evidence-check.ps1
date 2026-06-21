param(
    [string]$AgentRoot,
    [int]$LookbackMinutes = 15
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($AgentRoot)) {
    if (Test-Path -LiteralPath "D:\WUJI\WindowsAgent") {
        $AgentRoot = "D:\WUJI\WindowsAgent"
    }
    else {
        $AgentRoot = Join-Path $env:LOCALAPPDATA "WUJI\WindowsAgent"
    }
}

$databasePath = Join-Path $AgentRoot "data\quantified_self_windows.db"
if (-not (Test-Path -LiteralPath $databasePath)) {
    throw "Database not found: $databasePath"
}

$sinceUtc = (Get-Date).ToUniversalTime().AddMinutes(-1 * $LookbackMinutes).ToString("O")
$sql = @"
.headers on
.mode column

SELECT COUNT(*) AS recent_notepad_samples
FROM foreground_samples
WHERE sample_time_utc >= '$sinceUtc'
  AND lower(process_name) IN ('notepad', 'notepad.exe');

SELECT id, event_time_utc, event_type, event_level, error_code, process_name, payload_json
FROM agent_events
WHERE event_time_utc >= '$sinceUtc'
  AND event_type = 'PrivacyFiltered'
ORDER BY id DESC
LIMIT 20;
"@

$sqliteCandidates = @(
    "sqlite3",
    "sqlite3.exe",
    (Join-Path $AgentRoot "sqlite3.exe")
)

$sqlite = $sqliteCandidates | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if ($null -eq $sqlite) {
    Write-Host "sqlite3 was not found on PATH."
    Write-Host "Database path:"
    Write-Host $databasePath
    Write-Host ""
    Write-Host "Run these queries in DB Browser for SQLite or another SQLite client:"
    Write-Host $sql
    exit 0
}

Write-Host "Database:"
Write-Host $databasePath
Write-Host ""
Write-Host "Lookback since UTC:"
Write-Host $sinceUtc
Write-Host ""

$sql | & $sqlite $databasePath
