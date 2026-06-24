# Stage 6.6 Manual Verification Script
# Verifies privacy rule editing + ReloadConfig effectiveness
#
# Usage: Run this script while the WUJI App and Agent are running.
#   .\06-手动验收-隐私规则生效.ps1
#   .\06-手动验收-隐私规则生效.ps1 -AgentRoot "D:\WUJI\WindowsAgent"
#
# This script modifies windows-agent.json (creates a backup first).
# To restore: .\06-手动验收-隐私规则生效-restore.ps1

param(
    [string]$AgentRoot
)

$ErrorActionPreference = "Stop"

function Get-AgentRoot {
    param([string]$Value)

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    $envRoot = $env:QUANTIFIEDSELF_WINDOWS_AGENT_ROOT
    if ($envRoot -and (Test-Path -LiteralPath $envRoot)) {
        return $envRoot
    }

    if (Test-Path -LiteralPath "D:\WUJI\WindowsAgent") {
        return "D:\WUJI\WindowsAgent"
    }

    return Join-Path $env:LOCALAPPDATA "WUJI\WindowsAgent"
}

$Root = Get-AgentRoot $AgentRoot
$ConfigDir = Join-Path $Root "config"
$DataDir = Join-Path $Root "data"
$AgentOptionsPath = Join-Path $ConfigDir "windows-agent.json"
$DatabasePath = Join-Path $DataDir "quantified_self_windows.db"
$BackupPath = "$AgentOptionsPath.manual-test-backup.json"

Write-Host "=== Stage 6.6 Privacy Rule Verification ===" -ForegroundColor Cyan
Write-Host "Agent root : $Root"
Write-Host "Config     : $AgentOptionsPath"
Write-Host "DB         : $DatabasePath"
Write-Host ""

# Step 0: pre-flight checks
if (-not (Test-Path $AgentOptionsPath)) {
    Write-Host "[ERROR] $AgentOptionsPath not found." -ForegroundColor Red
    Write-Host "Make sure the Agent has run at least once to generate the config file."
    exit 1
}

if (-not (Test-Path $DatabasePath)) {
    Write-Host "[ERROR] $DatabasePath not found." -ForegroundColor Red
    Write-Host "Make sure the Agent has run at least once to create the database."
    exit 1
}

# Step 1: backup existing config
Copy-Item $AgentOptionsPath $BackupPath -Force
Write-Host "[OK] Backup created: $BackupPath" -ForegroundColor Green

# Write restore script
$restoreScriptDir = if ($PSCommandPath) { Split-Path $PSCommandPath -Parent } else { Get-Location }
$restoreScript = Join-Path $restoreScriptDir "06-手动验收-隐私规则生效-restore.ps1"
@"
# Restore script for stage 6.6 manual verification
param(
    [string]`$AgentRoot = "$Root"
)

`$Root = `$AgentRoot
if (-not (Test-Path -LiteralPath "`$Root")) {
    `$Root = Join-Path `$env:LOCALAPPDATA "WUJI\WindowsAgent"
}

`$ConfigDir = Join-Path `$Root "config"
`$AgentOptionsPath = Join-Path `$ConfigDir "windows-agent.json"
`$BackupPath = "`$AgentOptionsPath.manual-test-backup.json"

Copy-Item "`$BackupPath" "`$AgentOptionsPath" -Force
Remove-Item "`$BackupPath" -Force -ErrorAction SilentlyContinue
Write-Host "Restored windows-agent.json from manual test backup."
Write-Host "Please click 'Reload Agent Config' in WUJI App to apply the restored config."
"@ | Set-Content $restoreScript -Encoding UTF8
Write-Host "[OK] Restore script: $restoreScript" -ForegroundColor Green

# Step 2: Read current config and modify excludedProcesses to include Notepad
Write-Host ""
Write-Host "--- Step 2: Modifying config to exclude Notepad ---" -ForegroundColor Cyan

$config = Get-Content $AgentOptionsPath -Raw | ConvertFrom-Json

# Preserve all other settings
$currentExcluded = $config.excludedProcesses
if (-not $currentExcluded) { $currentExcluded = @() }

Write-Host "Current excludedProcesses: $($currentExcluded -join ', ')"
$config.excludedProcesses = @($currentExcluded + "notepad.exe") | Select-Object -Unique
Write-Host "Updated excludedProcesses: $($config.excludedProcesses -join ', ')"

$config | ConvertTo-Json -Depth 10 | Set-Content $AgentOptionsPath -Encoding UTF8
Write-Host "[OK] Config written. Notepad will be excluded after ReloadConfig." -ForegroundColor Green

# Step 3: Instructions for manual verification
Write-Host ""
Write-Host "=== Manual Verification Steps ===" -ForegroundColor Yellow
Write-Host ""
Write-Host "1. In WUJI App, go to Settings page."
Write-Host "2. Click 'Reload Agent Config' to apply the updated privacy rule."
Write-Host "3. Open Notepad (Win+R -> notepad -> Enter)."
Write-Host "4. Wait ~5 seconds for sampling to run."
Write-Host "5. Go to Diagnostics page - you should see a 'PrivacyFiltered' event"
Write-Host "   for process 'Notepad' with reason containing 'process privacy rule'."
Write-Host "6. Go to Samples page - no new Notepad samples should appear."
Write-Host "7. Go to Sessions page - no Notepad session should appear."
Write-Host ""
Write-Host "=== Verification SQL (run in any SQLite browser) ===" -ForegroundColor Yellow
Write-Host ""
Write-Host "-- Check recent PrivacyFiltered events"
Write-Host "SELECT event_time_utc, message, payload_json"
Write-Host "FROM agent_events"
Write-Host "WHERE event_type = 'PrivacyFiltered'"
Write-Host "ORDER BY id DESC LIMIT 5;"
Write-Host ""
Write-Host "-- Verify no Notepad samples"
Write-Host "SELECT sample_time_utc, process_name"
Write-Host "FROM foreground_samples"
Write-Host "WHERE process_name = 'Notepad'"
Write-Host "ORDER BY id DESC LIMIT 5;"
Write-Host "  (expect 0 rows after ReloadConfig)"
Write-Host ""
Write-Host "=== Cleanup ===" -ForegroundColor Yellow
Write-Host ""
Write-Host "To restore original config, run:"
Write-Host "  .\06-手动验收-隐私规则生效-restore.ps1"
Write-Host ""
Write-Host "Or manually:"
Write-Host "  copy '$BackupPath' '$AgentOptionsPath'"
Write-Host "Then click 'Reload Agent Config' in WUJI App."
