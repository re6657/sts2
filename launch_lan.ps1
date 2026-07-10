# TokenSpire2 LAN Multiplayer Launcher
# Launches two STS2 instances with different Steam persona names
# Window 1: Host (human player, auto-battle OFF)
# Window 2: Client (bot, auto-battle ON)
#
# Each instance gets its own config file via TOKENSPIRE2_CONFIG env var
# to avoid the race condition where the second write overwrites the first
# before the first instance finishes reading it.
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
# We write to the shared batch_config.json sequentially. Each instance reads
# the shared file ONCE during AppConfig.Initialize() at startup. After reading,
# the mod writes a "config_read.signal" file. The launcher polls for this
# signal before overwriting the config for the next instance.
#
# Godot's .NET runtime does NOT pass arbitrary CLI args through to
# Environment.GetCommandLineArgs(), so --config doesn't work. The env var
# approach also fails with Start-Process in PowerShell.

$SharedConfigFile = Join-Path $ModDir "batch_config.json"
# Per-role signal files — host writes config_read_host.signal,
# client writes config_read_client.signal. This prevents overwrite.
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
Start-Process -FilePath $GameExe -WorkingDirectory $GameDir
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
Start-Process -FilePath $GameExe -WorkingDirectory $GameDir
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
Write-Host "Window 1 (Host/Human): Create room via Multiplayer -> Host"
Write-Host "Window 2 (Bot/Client): Bot auto-navigates to Multiplayer -> Join"
Write-Host "                      Human clicks Join on bot window to connect"
Write-Host ""
Write-Host "Config files:"
Write-Host "  Host:  $HostConfigFile"
Write-Host "  Client: $ClientConfigFile"
