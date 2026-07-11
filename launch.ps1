# TokenSpire2 Unified Launcher — all modes, simultaneous window launch
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
$GameDir  = "E:\SteamLibrary\steamapps\common\Slay the Spire 2"
$GameExe  = Join-Path $GameDir "SlayTheSpire2.exe"
$ModDir   = Join-Path $GameDir "mods\TokenSpire2"
$seedJson = if ($Seed) { "`"$Seed`"" } else { "null" }

if (-not (Test-Path $GameExe)) {
    Write-Error "Game not found: $GameExe"
    exit 1
}

# ── Mode description ─────────────────────────────────────────────────
$modeInfo = @{
    "solo_bot"    = @{ Windows=1; Desc="单角色自动战斗" }
    "solo_player" = @{ Windows=1; Desc="单角色正常游戏" }
    "coop_1bot"   = @{ Windows=2; Desc="1玩家 + 1人机" }
    "coop_2bot"   = @{ Windows=3; Desc="1玩家 + 2人机" }
    "coop_3bot"   = @{ Windows=4; Desc="1玩家 + 3人机" }
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

# ── Cleanup old signals/configs ──────────────────────────────────────
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
Remove-Item "$GameDir\token_spire_host.json"  -Force -ErrorAction SilentlyContinue
Remove-Item "$GameDir\token_spire_client.json" -Force -ErrorAction SilentlyContinue

# ── Build instance list ──────────────────────────────────────────────
$instances = @()

switch ($Mode) {
    "solo_bot" {
        # 1 window: single-player auto-battle
        $instances += @{
            Label     = "SoloBot"
            Config    = @{ Seed=$seedJson; Character=$Character; MultiplayerMode=$false; IsMultiplayerHost=$false; AutoBattleEnabled=$true; SteamPersonaName="" }
            FastMp    = $null
            ConfigPath = Join-Path $GameDir "token_spire_solo.json"
            Signal    = "config_read.signal"
        }
    }
    "solo_player" {
        # 1 window: single-player normal game (no auto-battle)
        $instances += @{
            Label     = "SoloPlayer"
            Config    = @{ Seed=$seedJson; Character=$Character; MultiplayerMode=$false; IsMultiplayerHost=$false; AutoBattleEnabled=$false; SteamPersonaName="" }
            FastMp    = $null
            ConfigPath = Join-Path $GameDir "token_spire_solo.json"
            Signal    = "config_read.signal"
        }
    }
    default {
        # coop_1bot / coop_2bot / coop_3bot
        $botCount = [int]$Mode.Substring(5, 1)  # "coop_Xbot" → X
        $totalWindows = $botCount + 1

        # Host: human player
        $instances += @{
            Label     = "Host"
            Config    = @{ Seed=$seedJson; Character=$Character; MultiplayerMode=$true; IsMultiplayerHost=$true; AutoBattleEnabled=$false; SteamPersonaName="Player" }
            FastMp    = "host_standard"
            ConfigPath = Join-Path $GameDir "token_spire_host.json"
            Signal    = "config_read_host.signal"
        }

        # Bots: auto-battle clients
        for ($i = 1; $i -le $botCount; $i++) {
            $instances += @{
                Label     = "Bot$i"
                Config    = @{ Seed=$seedJson; Character=$Character; MultiplayerMode=$true; IsMultiplayerHost=$false; AutoBattleEnabled=$true; SteamPersonaName="Bot$i" }
                FastMp    = "join"
                ConfigPath = Join-Path $GameDir "token_spire_bot$i.json"
                Signal    = "config_read_bot$i.signal"
            }
        }
    }
}

# ── Write config files & build launch args ───────────────────────────
$launchJobs = @()

foreach ($inst in $instances) {
    $signalFile = [System.IO.Path]::GetFileName($inst.Signal)
    $json = @"
{"Seed":$($inst.Config.Seed),"Character":"$($inst.Config.Character)","MultiplayerMode":$($inst.Config.MultiplayerMode.ToString().ToLower()),"IsMultiplayerHost":$($inst.Config.IsMultiplayerHost.ToString().ToLower()),"SteamPersonaName":"$($inst.Config.SteamPersonaName)","AutoBattleEnabled":$($inst.Config.AutoBattleEnabled.ToString().ToLower()),"SignalFile":"$signalFile"}
"@
    $json | Set-Content -Path $inst.ConfigPath -Encoding UTF8
    Write-Host "[Config] $($inst.Label): $($inst.ConfigPath)"

    # Build args as a single string (same format as launch_lan.ps1)
    $fastmpArg = if ($inst.FastMp) { "--fastmp $($inst.FastMp) " } else { "" }
    $argsString = "${fastmpArg}--config `"$($inst.ConfigPath)`""

    $launchJobs += @{
        Label      = $inst.Label
        Args       = $argsString
        Signal     = Join-Path $ModDir $inst.Signal
        ConfigPath = $inst.ConfigPath
    }
}

# ── Launch ALL windows simultaneously ────────────────────────────────
Write-Host ""
Write-Host "[Launch] Starting all $($launchJobs.Count) window(s) simultaneously..."
Write-Host ""

$launchTime = Get-Date
foreach ($job in $launchJobs) {
    Write-Host "  [$($job.Label)] Launching: $($job.Args)"
    Start-Process -FilePath $GameExe -ArgumentList $job.Args -WorkingDirectory $GameDir
}
Write-Host ""
Write-Host "[Launch] All $($launchJobs.Count) processes started at $($launchTime.ToString('HH:mm:ss'))"

# ── Wait for all signals (non-blocking for solo modes) ──────────────
Write-Host ""
Write-Host "[Wait] Waiting for config signals..."

$timeout = 180
$waited = 0
$allReady = $false

while (-not $allReady -and $waited -lt $timeout) {
    Start-Sleep -Seconds 3
    $waited += 3
    $allReady = $true
    foreach ($job in $launchJobs) {
        if (-not (Test-Path $job.Signal)) {
            $allReady = $false
        }
    }
    if ($waited % 30 -eq 0) {
        $readyCount = ($launchJobs | Where-Object { Test-Path $_.Signal }).Count
        Write-Host "  ... ${waited}s — $readyCount/$($launchJobs.Count) signals received"
    }
}

Write-Host ""
if ($allReady) {
    Write-Host "[OK] All $($launchJobs.Count) instance(s) ready after ${waited}s"
} else {
    $missing = ($launchJobs | Where-Object { -not (Test-Path $_.Signal) })
    Write-Host "[WARN] Timeout after ${timeout}s. Missing signals:"
    $missing | ForEach-Object { Write-Host "  - $($_.Label): $($_.Signal)" }
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
    Write-Host "  Host window:  Human player (auto-battle OFF)"
    Write-Host "                F1=Nav F2=Battle F3=Event"
    Write-Host ""
    for ($i = 1; $i -le $botCount; $i++) {
        Write-Host "  Bot$i window:   Auto-battle ON"
    }
    Write-Host ""
    Write-Host "  Flow: Host navigates → bots follow → combat auto-plays"
}
Write-Host ""
Write-Host "  Config files in: $GameDir"
Write-Host "  Signals in:      $ModDir"
