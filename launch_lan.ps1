# TokenSpire2 LAN Multiplayer Launcher (2-4 players)
# Uses the game's built-in --fastmp mode for ENet transport (127.0.0.1:33771)
# No Steam lobby/matchmaking needed — direct ENet connection on localhost.
#
# Window 1: Host (human player, auto-battle OFF)
# Windows 2-4: Bots (auto-battle ON)
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
$GameDir = Resolve-Path "$PSScriptRoot\..\.."
$GameExe = Join-Path $GameDir "SlayTheSpire2.exe"
$ModDir = Join-Path $GameDir "mods\TokenSpire2"

if (-not (Test-Path $GameExe)) {
    Write-Error "Game executable not found: $GameExe"
    exit 1
}

Write-Host "============================================================"
Write-Host " TokenSpire2 LAN Multiplayer Launcher"
Write-Host " Players: 1 Human + $BotCount Bot(s) = $($BotCount + 1) windows"
Write-Host " Character: $Character"
if ($Seed) { Write-Host " Seed: $Seed" }
Write-Host "============================================================"
$seedJson = if ($Seed) { "`"$Seed`"" } else { "null" }

# ── Cleanup ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[Cleanup] Removing old signal files and configs..."

Remove-Item -Path (Join-Path $ModDir "config_read.signal") -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $ModDir "config_read_host.signal") -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $ModDir "batch_config.json") -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $GameDir "token_spire_host.json") -Force -ErrorAction SilentlyContinue

# Clean up bot signals (1-3)
for ($i = 1; $i -le 3; $i++) {
    Remove-Item -Path (Join-Path $ModDir "config_read_bot$i.signal") -Force -ErrorAction SilentlyContinue
    Remove-Item -Path (Join-Path $GameDir "token_spire_bot$i.json") -Force -ErrorAction SilentlyContinue
}

# ── Write per-instance config files ───────────────────────────────────
$HostConfigPath = Join-Path $GameDir "token_spire_host.json"
$HostSignalFile = "config_read_host.signal"
$hostConfig = @"
{"Seed":$seedJson,"Character":"$Character","MultiplayerMode":true,"IsMultiplayerHost":true,"SteamPersonaName":"Player","AutoBattleEnabled":false,"SignalFile":"$HostSignalFile"}
"@
$hostConfig | Set-Content -Path $HostConfigPath -Encoding UTF8
Write-Host "[Config] Host: $HostConfigPath"

# Build bot configs (same pattern, one per bot)
$BotConfigs = @()
for ($i = 1; $i -le $BotCount; $i++) {
    $botPath = Join-Path $GameDir "token_spire_bot$i.json"
    $botSignal = "config_read_bot$i.signal"
    $botName = "Bot$i"
    $botJson = @"
{"Seed":$seedJson,"Character":"$Character","MultiplayerMode":true,"IsMultiplayerHost":false,"SteamPersonaName":"$botName","AutoBattleEnabled":true,"SignalFile":"$botSignal"}
"@
    $botJson | Set-Content -Path $botPath -Encoding UTF8
    Write-Host "[Config] $botName : $botPath"
    $BotConfigs += @{ Path = $botPath; SignalFile = $botSignal; Name = $botName }
}

# ── Launch Host (Window 1) ───────────────────────────────────────────
Write-Host ""
Write-Host "============================================================"
Write-Host "[Host] Starting Window 1 (Human, auto-battle OFF)..."

$hostConfigArg = "--config `"$HostConfigPath`""
$hostArgs = "--fastmp host_standard $hostConfigArg"
Write-Host "[Host] Args: $hostArgs"
Start-Process -FilePath $GameExe -ArgumentList $hostArgs -WorkingDirectory $GameDir
Write-Host "[Host] Launched at $(Get-Date -Format 'HH:mm:ss')"

# Wait for host signal
$HostSignalPath = Join-Path $ModDir $HostSignalFile
Write-Host "[Host] Waiting for $HostSignalFile ..."
$timeout = 180
$waited = 0
while (-not (Test-Path $HostSignalPath) -and $waited -lt $timeout) {
    Start-Sleep -Seconds 3
    $waited += 3
    if ($waited % 15 -eq 0) { Write-Host "  ... waited ${waited}s" }
}
if (Test-Path $HostSignalPath) {
    Write-Host "[Host] Signal received after ${waited}s"
} else {
    Write-Host "[WARN] Host signal timeout after ${timeout}s"
}

# ── Launch each Bot (same pattern as original 2-player) ──────────────
foreach ($bot in $BotConfigs) {
    Write-Host ""
    Write-Host "============================================================"
    Write-Host "[$($bot.Name)] Starting bot window..."

    $botConfigArg = "--config `"$($bot.Path)`""
    $botArgs = "--fastmp join $botConfigArg"
    Write-Host "[$($bot.Name)] Args: $botArgs"
    Start-Process -FilePath $GameExe -ArgumentList $botArgs -WorkingDirectory $GameDir
    Write-Host "[$($bot.Name)] Launched at $(Get-Date -Format 'HH:mm:ss')"

    # Wait for this bot's signal
    $botSignalPath = Join-Path $ModDir $bot.SignalFile
    Write-Host "[$($bot.Name)] Waiting for $($bot.SignalFile) ..."
    $waitedBot = 0
    while (-not (Test-Path $botSignalPath) -and $waitedBot -lt $timeout) {
        Start-Sleep -Seconds 3
        $waitedBot += 3
        if ($waitedBot % 15 -eq 0) { Write-Host "  ... waited ${waitedBot}s" }
    }
    if (Test-Path $botSignalPath) {
        Write-Host "[$($bot.Name)] Signal received after ${waitedBot}s"
    } else {
        Write-Host "[WARN] $($bot.Name) signal timeout after ${timeout}s"
    }
}

# ── Summary ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "============================================================"
Write-Host " Launch Complete — 1 Human + $BotCount Bot(s)"
Write-Host "============================================================"
Write-Host ""
Write-Host "  Window 1 (Host/Human): ENet server on 127.0.0.1:33771"
for ($i = 1; $i -le $BotCount; $i++) {
    Write-Host "  Window $($i+1) (Bot$i):      Auto-joins via ENet, auto-battle ON"
}
Write-Host ""
Write-Host "  Config files: $GameDir\token_spire_*.json"
Write-Host "  Signal files: $ModDir\config_read_*.signal"
Write-Host ""
