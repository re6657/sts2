# TokenSpire2 LAN Multiplayer Launcher
# Uses the game's built-in --fastmp mode for ENet transport (127.0.0.1:33771)
# No Steam lobby/matchmaking needed — direct ENet connection on localhost.
#
# Window 1: Host (human player, auto-battle OFF)
# Window 2: Client (bot, auto-battle ON)
#
# Flow:
#   1. Launch host — human navigates Multiplayer → Host → Standard
#      Game creates ENet server on 127.0.0.1:33771
#   2. Launch client — bot navigates Multiplayer → Join
#      Game auto-connects to 127.0.0.1:33771 via ENet (FastMpJoin)
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
$HostConfigFile = Join-Path $ModDir "batch_config_host.json"
$ClientConfigFile = Join-Path $ModDir "batch_config_client.json"
$SteamFixIni = Join-Path $GameDir "SteamFix.ini"

# Validate
if (-not (Test-Path $GameExe)) {
    Write-Error "Game executable not found: $GameExe"
    exit 1
}

Write-Host "=== TokenSpire2 LAN Multiplayer Launcher ==="
Write-Host "Character: $Character"
if ($Seed) { Write-Host "Seed: $Seed" }

# Build seed JSON fragment
$seedJson = if ($Seed) { "`"$Seed`"" } else { "null" }

# ──── Write per-instance config files ──────────────────────────
$SharedConfigFile = Join-Path $ModDir "batch_config.json"
$HostSignalFile = Join-Path $ModDir "config_read_host.signal"
$ClientSignalFile = Join-Path $ModDir "config_read_client.signal"

$hostConfig = @"
{"Seed":$seedJson,"Character":"$Character","MultiplayerMode":true,"IsMultiplayerHost":true,"SteamPersonaName":"Player","AutoBattleEnabled":false}
"@

$clientConfig = @"
{"Seed":$seedJson,"Character":"$Character","MultiplayerMode":true,"IsMultiplayerHost":false,"SteamPersonaName":"Bot","AutoBattleEnabled":true}
"@

# Save per-instance configs for diagnostic reference
$hostConfig | Set-Content -Path $HostConfigFile -Encoding UTF8
$clientConfig | Set-Content -Path $ClientConfigFile -Encoding UTF8

# ──── Launch Host (Window 1) ──────────────────────────────────
Write-Host ""
Write-Host "[Host] Starting Window 1 (Player, auto-battle OFF)..."
Write-Host "[Host] Config: $hostConfig"

# Delete old signal files before starting
Remove-Item -Path $HostSignalFile -Force -ErrorAction SilentlyContinue
Remove-Item -Path $ClientSignalFile -Force -ErrorAction SilentlyContinue

$hostConfig | Set-Content -Path $SharedConfigFile -Encoding UTF8

# Launch with --fastmp host_standard for ENet host (bypasses Steam matchmaking)
$hostArgs = "--fastmp host_standard"
Write-Host "[Host] Launch args: $hostArgs"
Start-Process -FilePath $GameExe -ArgumentList $hostArgs -WorkingDirectory $GameDir
Write-Host "[Host] Launched at $(Get-Date -Format 'HH:mm:ss')"

# Poll for host to read config (role-specific signal file)
Write-Host "[Host] Waiting for config_read_host.signal..."
$timeout = 120  # seconds
$waited = 0
while (-not (Test-Path $HostSignalFile) -and $waited -lt $timeout) {
    Start-Sleep -Seconds 2
    $waited += 2
    if ($waited % 10 -eq 0) { Write-Host "  ... waited ${waited}s" }
}
if (Test-Path $HostSignalFile) {
    $signalContent = Get-Content $HostSignalFile -Raw
    Write-Host "[Host] Signal received after ${waited}s:"
    Write-Host $signalContent
} else {
    Write-Host "[WARNING] Host signal timeout after ${timeout}s. Launching client anyway."
}

# ──── Launch Client (Window 2) ────────────────────────────────
Write-Host ""
Write-Host "[Client] Starting Window 2 (Bot, auto-battle ON)..."
Write-Host "[Client] Config: $clientConfig"

$clientConfig | Set-Content -Path $SharedConfigFile -Encoding UTF8

# Launch with --fastmp join for ENet client (bypasses Steam matchmaking)
$clientArgs = "--fastmp join"
Write-Host "[Client] Launch args: $clientArgs"
Start-Process -FilePath $GameExe -ArgumentList $clientArgs -WorkingDirectory $GameDir
Write-Host "[Client] Launched at $(Get-Date -Format 'HH:mm:ss')"

# Wait for client to read config (optional, for diagnostics)
Write-Host "[Client] Waiting for config_read_client.signal..."
$waited2 = 0
while (-not (Test-Path $ClientSignalFile) -and $waited2 -lt $timeout) {
    Start-Sleep -Seconds 2
    $waited2 += 2
    if ($waited2 % 10 -eq 0) { Write-Host "  ... waited ${waited2}s" }
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
Write-Host "Window 1 (Host/Human): Navigate Multiplayer -> Host -> Standard"
Write-Host "Window 2 (Bot/Client): Bot auto-navigates Multiplayer -> Join"
Write-Host "                      Joins automatically via ENet (127.0.0.1:33771)"
Write-Host ""
Write-Host "Config files:"
Write-Host "  Host:  $HostConfigFile"
Write-Host "  Client: $ClientConfigFile"
