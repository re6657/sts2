# TokenSpire2 LAN Multiplayer Launcher (2-4 players)
# Uses the game's built-in --fastmp mode for ENet transport (127.0.0.1:33771)
# No Steam lobby/matchmaking needed — direct ENet connection on localhost.
#
# Window 1: Host (human player, auto-battle OFF)
# Windows 2-4: Clients (bots, auto-battle ON)
#
# Each instance has its OWN config file and signal file — no race conditions.
#
# Usage: .\launch_lan.ps1 [-Character IRONCLAD] [-Seed SEED] [-BotCount 2]
#   BotCount: 1-3 (default: 1, total windows = BotCount + 1)

param(
    [string]$Character = "IRONCLAD",
    [string]$Seed = $null,
    [ValidateRange(1, 3)]
    [int]$BotCount = 1
)

$ErrorActionPreference = "Stop"

# Paths
$GameDir = "E:\SteamLibrary\steamapps\common\Slay the Spire 2"
$GameExe = Join-Path $GameDir "SlayTheSpire2.exe"
$ModDir = Join-Path $GameDir "mods\TokenSpire2"

if (-not (Test-Path $GameExe)) {
    Write-Error "Game executable not found: $GameExe"
    exit 1
}

$totalWindows = $BotCount + 1
Write-Host "============================================================"
Write-Host " TokenSpire2 LAN Multiplayer Launcher"
Write-Host " Players: 1 Human + $BotCount Bot(s) = $totalWindows windows"
Write-Host " Character: $Character"
if ($Seed) { Write-Host " Seed: $Seed" }
Write-Host "============================================================"
$seedJson = if ($Seed) { "`"$Seed`"" } else { "null" }

# ── Cleanup ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[Cleanup] Removing old signal files and configs..."

Remove-Item -Path (Join-Path $ModDir "config_read.signal") -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $ModDir "config_read_host.signal") -Force -ErrorAction SilentlyContinue
for ($i = 1; $i -le 3; $i++) {
    Remove-Item -Path (Join-Path $ModDir "config_read_bot$i.signal") -Force -ErrorAction SilentlyContinue
    Remove-Item -Path (Join-Path $GameDir "token_spire_bot$i.json") -Force -ErrorAction SilentlyContinue
}
Remove-Item -Path (Join-Path $ModDir "batch_config.json") -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $GameDir "token_spire_host.json") -Force -ErrorAction SilentlyContinue

# ── Write per-instance config files ───────────────────────────────────
Write-Host ""
Write-Host "[Config] Writing per-instance config files..."

# Host config
$hostConfigPath = Join-Path $GameDir "token_spire_host.json"
$hostSignalFile = "config_read_host.signal"
$hostConfig = @"
{"Seed":$seedJson,"Character":"$Character","MultiplayerMode":true,"IsMultiplayerHost":true,"SteamPersonaName":"Player","AutoBattleEnabled":false,"SignalFile":"$hostSignalFile"}
"@
$hostConfig | Set-Content -Path $hostConfigPath -Encoding UTF8
Write-Host "  Host: $hostConfigPath"

# Bot configs
$botConfigs = @()
for ($i = 1; $i -le $BotCount; $i++) {
    $botConfigPath = Join-Path $GameDir "token_spire_bot$i.json"
    $botSignalFile = "config_read_bot$i.signal"
    $botName = "Bot$i"
    $botConfig = @"
{"Seed":$seedJson,"Character":"$Character","MultiplayerMode":true,"IsMultiplayerHost":false,"SteamPersonaName":"$botName","AutoBattleEnabled":true,"SignalFile":"$botSignalFile"}
"@
    $botConfig | Set-Content -Path $botConfigPath -Encoding UTF8
    Write-Host "  $botName : $botConfigPath"
    $botConfigs += @{ Path = $botConfigPath; Signal = (Join-Path $ModDir $botSignalFile); Name = $botName; SignalName = $botSignalFile }
}

# ── Launch Host ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "[Host] Starting Window 1 (Human, auto-battle OFF)..."

$hostConfigArg = "--config `"$hostConfigPath`""
$hostArgs = "--fastmp host_standard $hostConfigArg"
Write-Host "[Host] Args: $hostArgs"
Start-Process -FilePath $GameExe -ArgumentList $hostArgs -WorkingDirectory $GameDir
Write-Host "[Host] Launched at $(Get-Date -Format 'HH:mm:ss')"

Write-Host "[Host] Waiting for signal: $hostSignalFile ..."
$timeout = 180
$waited = 0
$hostSignalPath = Join-Path $ModDir $hostSignalFile
while (-not (Test-Path $hostSignalPath) -and $waited -lt $timeout) {
    Start-Sleep -Seconds 3
    $waited += 3
    if ($waited % 15 -eq 0) { Write-Host "  ... waited ${waited}s" }
}
if (Test-Path $hostSignalPath) {
    Write-Host "[Host] Signal received after ${waited}s"
} else {
    Write-Host "[WARN] Host signal timeout after ${timeout}s"
}

# ── Launch Bots (sequential, but without waiting for each signal) ────
Write-Host ""
foreach ($bot in $botConfigs) {
    Write-Host "[$($bot.Name)] Starting bot window..."

    $botConfigArg = "--config `"$($bot.Path)`""
    $botArgs = "--fastmp join $botConfigArg"
    Write-Host "[$($bot.Name)] Args: $botArgs"
    Start-Process -FilePath $GameExe -ArgumentList $botArgs -WorkingDirectory $GameDir
    Write-Host "[$($bot.Name)] Launched at $(Get-Date -Format 'HH:mm:ss')"

    # Small delay between bot launches to avoid ENet port conflicts
    Start-Sleep -Seconds 2
}

# ── Wait for all bot signals ─────────────────────────────────────────
Write-Host ""
Write-Host "[Wait] Waiting for all bot signals..."
$allReady = $false
$waited2 = 0
while (-not $allReady -and $waited2 -lt $timeout) {
    Start-Sleep -Seconds 3
    $waited2 += 3
    $allReady = $true
    foreach ($bot in $botConfigs) {
        if (-not (Test-Path $bot.Signal)) {
            $allReady = $false
        }
    }
    if ($waited2 % 15 -eq 0) {
        $readyCount = ($botConfigs | Where-Object { Test-Path $_.Signal }).Count
        Write-Host "  ... ${waited2}s — $readyCount/$($botConfigs.Count) bot signals"
    }
}

Write-Host ""
$readyCount = ($botConfigs | Where-Object { Test-Path $_.Signal }).Count
if ($readyCount -eq $botConfigs.Count) {
    Write-Host "[OK] All $($botConfigs.Count) bot(s) ready after ${waited2}s"
} else {
    Write-Host "[WARN] Timeout: $readyCount/$($botConfigs.Count) bots ready"
}

# ── Summary ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "============================================================"
Write-Host " Launch Complete — 1 Human + $BotCount Bot(s)"
Write-Host "============================================================"
Write-Host ""
Write-Host "  Window 1 (Host/Human): ENet server on 127.0.0.1:33771"
Write-Host "                         TokenSpire2 auto-navigates to lobby"
for ($i = 1; $i -le $BotCount; $i++) {
    Write-Host "  Window $($i+1) (Bot$i/Bot):   Auto-joins via ENet, auto-ready, auto-battle"
}
Write-Host ""
Write-Host "  Host controls (F1-F3): F1=Nav F2=Battle F3=Event"
Write-Host "  Bot auto-battle scope: Full (navigate + combat)"
Write-Host ""
Write-Host "  Config files: $GameDir\token_spire_*.json"
Write-Host "  Signal files: $ModDir\config_read_*.signal"
Write-Host ""
