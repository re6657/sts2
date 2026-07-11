# TokenSpire2 LAN Multiplayer Launcher
# Uses the game's built-in --fastmp mode for ENet transport (127.0.0.1:33771)
# No Steam lobby/matchmaking needed — direct ENet connection on localhost.
#
# Window 1: Host (human player, auto-battle OFF)
# Window 2: Client (bot, auto-battle ON)
#
# Flow:
#   1. Launch host — TokenSpire2 reads token_spire_host.json via --config flag
#      Game creates ENet server on 127.0.0.1:33771 (--fastmp host_standard)
#   2. Launch client — TokenSpire2 reads token_spire_client.json via --config flag
#      Game auto-connects to 127.0.0.1:33771 via ENet (--fastmp join)
#
# Each instance has its OWN config file — NO shared batch_config.json race condition.
# Each instance writes a role-specific signal file (config_read_host.signal / config_read_client.signal).
#
# Usage: .\launch_lan.ps1 [Character] [Seed]
#   Character: IRONCLAD, SILENT, DEFECT, REGENT, NECROBINDER (default: IRONCLAD)
#   Seed: optional run seed (default: random)

param(
    [string]$Character = "IRONCLAD",
    [string]$Seed = $null
)

$ErrorActionPreference = "Stop"

# Paths
$GameDir = "E:\SteamLibrary\steamapps\common\Slay the Spire 2"
$GameExe = Join-Path $GameDir "SlayTheSpire2.exe"
$ModDir = Join-Path $GameDir "mods\TokenSpire2"

# Per-instance config files — in game root, NOT in mods directory
# (game scans mods/*.json as mod manifests; putting configs there causes noise)
$HostConfigFile = Join-Path $GameDir "token_spire_host.json"
$ClientConfigFile = Join-Path $GameDir "token_spire_client.json"

# Per-instance signal files — ModDir is fine, only AppConfig writes these
$HostSignalFile = Join-Path $ModDir "config_read_host.signal"
$ClientSignalFile = Join-Path $ModDir "config_read_client.signal"

# Validate
if (-not (Test-Path $GameExe)) {
    Write-Error "Game executable not found: $GameExe"
    exit 1
}

Write-Host "=== TokenSpire2 LAN Multiplayer Launcher ==="
Write-Host "Character: $Character"
if ($Seed) { Write-Host "Seed: $Seed" }
$seedJson = if ($Seed) { "`"$Seed`"" } else { "null" }

# ──── Clean up old files ──────────────────────────────────────────
Write-Host ""
Write-Host "[Cleanup] Removing old signal files and shared config..."

Remove-Item -Path $HostSignalFile -Force -ErrorAction SilentlyContinue
Remove-Item -Path $ClientSignalFile -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $ModDir "config_read.signal") -Force -ErrorAction SilentlyContinue

# Remove old per-instance configs from mods dir (causes mod-manifest scan noise)
Remove-Item -Path (Join-Path $ModDir "batch_config.json") -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $ModDir "batch_config_host.json") -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $ModDir "batch_config_client.json") -Force -ErrorAction SilentlyContinue

# ──── Write per-instance config files (in game root, NOT mods dir) ─
$hostConfig = @"
{"Seed":$seedJson,"Character":"$Character","MultiplayerMode":true,"IsMultiplayerHost":true,"SteamPersonaName":"Player","AutoBattleEnabled":false}
"@

$clientConfig = @"
{"Seed":$seedJson,"Character":"$Character","MultiplayerMode":true,"IsMultiplayerHost":false,"SteamPersonaName":"Bot","AutoBattleEnabled":true}
"@

$hostConfig | Set-Content -Path $HostConfigFile -Encoding UTF8
$clientConfig | Set-Content -Path $ClientConfigFile -Encoding UTF8

Write-Host "[Cleanup] Host config written to: $HostConfigFile"
Write-Host "[Cleanup] Client config written to: $ClientConfigFile"

# ──── Launch Host (Window 1) ──────────────────────────────────────
Write-Host ""
Write-Host "============================================================"
Write-Host "[Host] Starting Window 1 (Player, auto-battle OFF)..."
Write-Host "[Host] Config: $hostConfig"

# --config flag tells AppConfig which file to read (absolute path)
# --fastmp host_standard tells the game to auto-host ENet server
$hostConfigArg = "--config `"$HostConfigFile`""
$hostArgs = "--fastmp host_standard $hostConfigArg"
Write-Host "[Host] Launch args: $hostArgs"
Start-Process -FilePath $GameExe -ArgumentList $hostArgs -WorkingDirectory $GameDir
Write-Host "[Host] Launched at $(Get-Date -Format 'HH:mm:ss')"

# Poll for host to read config
Write-Host "[Host] Waiting for config_read_host.signal..."
$timeout = 180  # seconds — cold start can take 60-90s
$waited = 0
while (-not (Test-Path $HostSignalFile) -and $waited -lt $timeout) {
    Start-Sleep -Seconds 3
    $waited += 3
    if ($waited % 15 -eq 0) { Write-Host "  ... waited ${waited}s" }
}
if (Test-Path $HostSignalFile) {
    $signalContent = Get-Content $HostSignalFile -Raw
    Write-Host "[Host] Signal received after ${waited}s:"
    Write-Host $signalContent
} else {
    Write-Host "[WARNING] Host signal timeout after ${timeout}s."
    Write-Host "[WARNING] Check if host window is visible. Launching client anyway."
}

# ──── Launch Client (Window 2) ────────────────────────────────────
Write-Host ""
Write-Host "============================================================"
Write-Host "[Client] Starting Window 2 (Bot, auto-battle ON)..."
Write-Host "[Client] Config: $clientConfig"

$clientConfigArg = "--config `"$ClientConfigFile`""
$clientArgs = "--fastmp join $clientConfigArg"
Write-Host "[Client] Launch args: $clientArgs"
Start-Process -FilePath $GameExe -ArgumentList $clientArgs -WorkingDirectory $GameDir
Write-Host "[Client] Launched at $(Get-Date -Format 'HH:mm:ss')"

# Wait for client to read config
Write-Host "[Client] Waiting for config_read_client.signal..."
$waited2 = 0
while (-not (Test-Path $ClientSignalFile) -and $waited2 -lt $timeout) {
    Start-Sleep -Seconds 3
    $waited2 += 3
    if ($waited2 % 15 -eq 0) { Write-Host "  ... waited ${waited2}s" }
}
if (Test-Path $ClientSignalFile) {
    $signalContent = Get-Content $ClientSignalFile -Raw
    Write-Host "[Client] Signal received after ${waited2}s:"
    Write-Host $signalContent
} else {
    Write-Host "[WARNING] Client signal timeout after ${timeout}s."
}

Write-Host ""
Write-Host "=== Both instances launched ==="
Write-Host ""
Write-Host "Expected flow:"
Write-Host "  Window 1 (Host/Human): Game auto-hosts Standard run via --fastmp host_standard"
Write-Host "                         ENet server on 127.0.0.1:33771"
Write-Host "                         TokenSpire2 auto-navigates to lobby"
Write-Host ""
Write-Host "  Window 2 (Bot/Client):  Game auto-joins 127.0.0.1:33771 via --fastmp join"
Write-Host "                         TokenSpire2 auto-navigates to lobby"
Write-Host "                         Bot reads character, clicks Ready"
Write-Host ""
Write-Host "Controls:"
Write-Host "  Host: F1=Nav OFF  F2=Battle OFF  F3=Event OFF (all OFF by default)"
Write-Host "  Client: F1=Nav ON  F2=Battle ON  F3=Event ON (all ON by default)"
Write-Host ""
Write-Host "Config files:"
Write-Host "  Host:  $HostConfigFile"
Write-Host "  Client: $ClientConfigFile"
