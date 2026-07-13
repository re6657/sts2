# TokenSpire2 Unified Launcher — all modes
#
# Usage:
#   .\launch.ps1 -Mode solo_bot                     单角色自动战斗
#   .\launch.ps1 -Mode solo_player                   单角色正常游戏
#   .\launch.ps1 -Mode coop_1bot                     1玩家 + 1人机 (2窗口)
#   .\launch.ps1 -Mode coop_2bot                     1玩家 + 2人机 (3窗口)
#   .\launch.ps1 -Mode coop_3bot                     1玩家 + 3人机 (4窗口)
#
#   .\launch.ps1 -Mode coop_1bot -Character SILENT   指定角色
#   .\launch.ps1 -Mode solo_bot -Seed ABC123         指定种子

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("solo_bot", "solo_player", "coop_1bot", "coop_2bot", "coop_3bot")]
    [string]$Mode,

    [ValidateSet("IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER")]
    [string]$Character = "IRONCLAD",

    [string]$Seed = $null
)

$ErrorActionPreference = "Stop"

# ── Paths ────────────────────────────────────────────────────────────
$GameDir  = Resolve-Path "$PSScriptRoot\..\.."
$GameExe  = Join-Path $GameDir "SlayTheSpire2.exe"
$ModDir   = Join-Path $GameDir "mods\TokenSpire2"
$seedJson = if ($Seed) { "`"$Seed`"" } else { "null" }

if (-not (Test-Path $GameExe)) {
    Write-Error "Game not found: $GameExe"
    exit 1
}

# ── Mode info ─────────────────────────────────────────────────────────
$modeInfo = @{
    "solo_bot"    = @{ Windows=1; Desc="单角色自动战斗"; IsMultiplayer=$false }
    "solo_player" = @{ Windows=1; Desc="单角色正常游戏"; IsMultiplayer=$false }
    "coop_1bot"   = @{ Windows=2; Desc="1玩家 + 1人机"; IsMultiplayer=$true; BotCount=1 }
    "coop_2bot"   = @{ Windows=3; Desc="1玩家 + 2人机"; IsMultiplayer=$true; BotCount=2 }
    "coop_3bot"   = @{ Windows=4; Desc="1玩家 + 3人机"; IsMultiplayer=$true; BotCount=3 }
}
$info = $modeInfo[$Mode]
Write-Host ""
Write-Host "============================================================"
Write-Host " TokenSpire2 Launcher"
Write-Host " Mode:      $Mode ($($info.Desc))"
Write-Host " Windows:   $($info.Windows)"
Write-Host " Character: $Character"
if ($Seed) { Write-Host " Seed:      $Seed" }
Write-Host "============================================================"
Write-Host ""

# ── Cleanup ──────────────────────────────────────────────────────────
Write-Host "[Cleanup] Removing old files..."
@(
    "$ModDir\config_read.signal",
    "$ModDir\config_read_host.signal"
) | ForEach-Object { Remove-Item $_ -Force -ErrorAction SilentlyContinue }
1..4 | ForEach-Object {
    Remove-Item "$ModDir\config_read_bot$_.signal" -Force -ErrorAction SilentlyContinue
    Remove-Item "$GameDir\token_spire_bot$_.json"   -Force -ErrorAction SilentlyContinue
}
Remove-Item "$ModDir\batch_config.json" -Force -ErrorAction SilentlyContinue
Remove-Item "$GameDir\token_spire_host.json" -Force -ErrorAction SilentlyContinue
Remove-Item "$GameDir\token_spire_solo.json" -Force -ErrorAction SilentlyContinue

# ── Build instance list ──────────────────────────────────────────────
$instances = @()

switch ($Mode) {
    "solo_bot" {
        $instances += @{
            Label      = "SoloBot"
            ConfigPath = Join-Path $GameDir "token_spire_solo.json"
            Signal     = "config_read.signal"
            FastMp     = $null
            Config     = @{ Seed=$seedJson; Character=$Character; MultiplayerMode=$false; IsMultiplayerHost=$false; AutoBattleEnabled=$true; SteamPersonaName="" }
        }
    }
    "solo_player" {
        $instances += @{
            Label      = "SoloPlayer"
            ConfigPath = Join-Path $GameDir "token_spire_solo.json"
            Signal     = "config_read.signal"
            FastMp     = $null
            Config     = @{ Seed=$seedJson; Character=$Character; MultiplayerMode=$false; IsMultiplayerHost=$false; AutoBattleEnabled=$false; SteamPersonaName="" }
        }
    }
    default {
        # coop_1bot / coop_2bot / coop_3bot
        $botCount = $info.BotCount

        # Host (human, auto-battle OFF)
        $instances += @{
            Label      = "Host"
            ConfigPath = Join-Path $GameDir "token_spire_host.json"
            Signal     = "config_read_host.signal"
            FastMp     = "host_standard"
            Config     = @{ Seed=$seedJson; Character=$Character; MultiplayerMode=$true; IsMultiplayerHost=$true; AutoBattleEnabled=$false; SteamPersonaName="Player" }
        }

        # Bots (auto-battle ON)
        for ($i = 1; $i -le $botCount; $i++) {
            $instances += @{
                Label      = "Bot$i"
                ConfigPath = Join-Path $GameDir "token_spire_bot$i.json"
                Signal     = "config_read_bot$i.signal"
                FastMp     = "join"
                Config     = @{ Seed=$seedJson; Character=$Character; MultiplayerMode=$true; IsMultiplayerHost=$false; AutoBattleEnabled=$true; SteamPersonaName="Bot$i" }
            }
        }
    }
}

# ── Write config files ───────────────────────────────────────────────
foreach ($inst in $instances) {
    $json = @"
{"Seed":$($inst.Config.Seed),"Character":"$($inst.Config.Character)","MultiplayerMode":$($inst.Config.MultiplayerMode.ToString().ToLower()),"IsMultiplayerHost":$($inst.Config.IsMultiplayerHost.ToString().ToLower()),"SteamPersonaName":"$($inst.Config.SteamPersonaName)","AutoBattleEnabled":$($inst.Config.AutoBattleEnabled.ToString().ToLower()),"SignalFile":"$($inst.Signal)"}
"@
    $json | Set-Content -Path $inst.ConfigPath -Encoding UTF8
    Write-Host "[Config] $($inst.Label): $($inst.ConfigPath)"
}

# ── Launch ────────────────────────────────────────────────────────────
# Solo modes: launch directly (no multiplayer dependencies)
# Coop modes: sequential launch — host first, then bots (same proven pattern as launch_lan.ps1)

if (-not $info.IsMultiplayer) {
    # ── Solo launch (simultaneous, only 1 window anyway) ──────────────
    Write-Host ""
    Write-Host "[Launch] Starting solo window..."
    $inst = $instances[0]
    $argsString = "--config `"$($inst.ConfigPath)`""
    Write-Host "  Args: $argsString"
    Start-Process -FilePath $GameExe -ArgumentList $argsString -WorkingDirectory $GameDir
    Write-Host "[Launch] Started at $(Get-Date -Format 'HH:mm:ss')"

    # Wait for signal
    $signalPath = Join-Path $ModDir $inst.Signal
    $timeout = 180; $waited = 0
    while (-not (Test-Path $signalPath) -and $waited -lt $timeout) {
        Start-Sleep -Seconds 3; $waited += 3
    }
    if (Test-Path $signalPath) {
        Write-Host "[OK] Ready after ${waited}s"
    } else {
        Write-Host "[WARN] Timeout after ${timeout}s"
    }
}
else {
    # ── Coop launch (sequential: host → bots) ────────────────────────
    $hostInst = $instances[0]
    $botInsts = $instances[1..($instances.Count - 1)]
    $timeout = 180

    # Launch host first
    Write-Host ""
    Write-Host "============================================================"
    Write-Host "[Host] Starting Window 1 (Human, auto-battle OFF)..."
    $hostConfigArg = "--config `"$($hostInst.ConfigPath)`""
    $hostArgs = "--fastmp host_standard $hostConfigArg"
    Write-Host "[Host] Args: $hostArgs"
    Start-Process -FilePath $GameExe -ArgumentList $hostArgs -WorkingDirectory $GameDir
    Write-Host "[Host] Launched at $(Get-Date -Format 'HH:mm:ss')"

    # Wait for host signal
    $hostSignalPath = Join-Path $ModDir $hostInst.Signal
    Write-Host "[Host] Waiting for $($hostInst.Signal) ..."
    $waited = 0
    while (-not (Test-Path $hostSignalPath) -and $waited -lt $timeout) {
        Start-Sleep -Seconds 3; $waited += 3
        if ($waited % 15 -eq 0) { Write-Host "  ... waited ${waited}s" }
    }
    if (Test-Path $hostSignalPath) {
        Write-Host "[Host] Signal received after ${waited}s"
    } else {
        Write-Host "[WARN] Host signal timeout after ${timeout}s"
    }

    # Launch each bot sequentially
    foreach ($bot in $botInsts) {
        Write-Host ""
        Write-Host "============================================================"
        Write-Host "[$($bot.Label)] Starting bot window..."

        $botConfigArg = "--config `"$($bot.ConfigPath)`""
        $botArgs = "--fastmp join $botConfigArg"
        Write-Host "[$($bot.Label)] Args: $botArgs"
        Start-Process -FilePath $GameExe -ArgumentList $botArgs -WorkingDirectory $GameDir
        Write-Host "[$($bot.Label)] Launched at $(Get-Date -Format 'HH:mm:ss')"

        # Wait for this bot's signal
        $botSignalPath = Join-Path $ModDir $bot.Signal
        Write-Host "[$($bot.Label)] Waiting for $($bot.Signal) ..."
        $waitedBot = 0
        while (-not (Test-Path $botSignalPath) -and $waitedBot -lt $timeout) {
            Start-Sleep -Seconds 3; $waitedBot += 3
            if ($waitedBot % 15 -eq 0) { Write-Host "  ... waited ${waitedBot}s" }
        }
        if (Test-Path $botSignalPath) {
            Write-Host "[$($bot.Label)] Signal received after ${waitedBot}s"
        } else {
            Write-Host "[WARN] $($bot.Label) signal timeout after ${timeout}s"
        }
    }
}

# ── Summary ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "============================================================"
Write-Host " Launch Complete — $($info.Desc)"
Write-Host "============================================================"
Write-Host ""

if ($Mode -eq "solo_bot") {
    Write-Host "  Window: Auto-battle running. Press T to toggle."
}
elseif ($Mode -eq "solo_player") {
    Write-Host "  Window: Normal game. Press T to enable auto-battle."
}
else {
    Write-Host "  Window 1 (Host/Human): ENet server on 127.0.0.1:33771"
    Write-Host "                         F1=Nav F2=Battle F3=Event"
    for ($i = 1; $i -le $info.BotCount; $i++) {
        Write-Host "  Window $($i+1) (Bot$i):      Auto-joins via ENet, auto-battle ON"
    }
}
Write-Host ""
Write-Host "  Config files in: $GameDir"
Write-Host "  Signals in:      $ModDir"
Write-Host ""
