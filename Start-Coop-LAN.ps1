# =============================================================================
# TokenSpire2 LAN Co-op — Dual-Instance Launch Script (TCP Broker)
# =============================================================================
# Uses a local TCP broker server (BrokerServer.exe) to relay network messages
# between two game instances. No Steam networking required.
#
# Architecture:
#   BrokerServer.exe (127.0.0.1:9999)
#       ↑ TCP              ↑ TCP
#   Host (STS2)  ←→  Broker  ←→  Client (STS2)
#       bot plays P1        relay       human plays P2
#
# Usage: Right-click → "Run with PowerShell" or:
#   powershell -ExecutionPolicy Bypass -File "Start-Coop-LAN.ps1"
# =============================================================================

$ErrorActionPreference = "Stop"

# ── Paths ────────────────────────────────────────────────────────────────────
$GAME_DIR   = "E:\SteamLibrary\steamapps\common\Slay the Spire 2"
$GAME_EXE   = "$GAME_DIR\SlayTheSpire2.exe"
$MOD_DIR    = "$GAME_DIR\mods\TokenSpire2"
$BROKER_EXE = "$MOD_DIR\BrokerServer\bin\Release\net8.0\BrokerServer.exe"
$CONFIG     = "$MOD_DIR\coop_config.json"
$MARKER_HOST  = "$MOD_DIR\enable-local-broker-host.txt"
$MARKER_CLIENT = "$MOD_DIR\enable-local-broker-client.txt"
$MARKER_SHARED = "$MOD_DIR\enable-local-broker.txt"
$STEAM_APPID= "$GAME_DIR\steam_appid.txt"

# ── Broker settings ──────────────────────────────────────────────────────────
$BROKER_PORT = 9999
$SESSION_ID  = "coop-dual-" + (Get-Date -Format "HHmmss")

# ── Verify BrokerServer.exe exists ───────────────────────────────────────────
if (-not (Test-Path $BROKER_EXE)) {
    Write-Host "[ERROR] BrokerServer.exe not found: $BROKER_EXE" -ForegroundColor Red
    Write-Host "Please build the BrokerServer project first."
    pause
    exit 1
}

# ── Ensure steam_appid.txt exists ────────────────────────────────────────────
if (-not (Test-Path $STEAM_APPID)) {
    "2868840" | Out-File -FilePath $STEAM_APPID -Encoding ASCII -NoNewline
    Write-Host "[OK] Created steam_appid.txt"
}

# ── Clean up previous instances ─────────────────────────────────────────────
Write-Host "=== Cleaning up previous instances ===" -ForegroundColor DarkGray
try { Get-Process -Name "SlayTheSpire2" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction Stop } catch { Write-Host "[WARN] Could not stop SlayTheSpire2: $_" }
try { Get-Process -Name "BrokerServer" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction Stop } catch { Write-Host "[WARN] Could not stop BrokerServer: $_" }
Remove-Item $MARKER_HOST -ErrorAction SilentlyContinue
Remove-Item $MARKER_CLIENT -ErrorAction SilentlyContinue
Remove-Item $MARKER_SHARED -ErrorAction SilentlyContinue
Write-Host "[CLEAN] Previous instances cleaned."
Start-Sleep -Seconds 1

# ── Write shared coop config ─────────────────────────────────────────────────
@"
{
  "AutoBattleEnabled": true,
  "AutoBattlePaused": false,
  "AutoBattleScope": 1,
  "CoopMode": true,
  "BotPlayerSlot": 0,
  "AutoStartEnabled": true
}
"@ | Out-File -FilePath $CONFIG -Encoding UTF8
Write-Host "[OK] Config: CoopMode=true, AutoStart=true"
Write-Host "[OK] Session ID: $SESSION_ID"
Write-Host ""

# ═══════════════════════════════════════════════════════════════════════════════
# STEP 1: START TCP BROKER SERVER
# ═══════════════════════════════════════════════════════════════════════════════
Write-Host "=== Starting TCP Broker Server ===" -ForegroundColor Magenta
$brokerProc = Start-Process -FilePath $BROKER_EXE `
    -ArgumentList "--port", $BROKER_PORT, "--session-id", $SESSION_ID `
    -PassThru -WindowStyle Minimized
Write-Host "[BROKER] PID: $($brokerProc.Id)  Port: $BROKER_PORT  Session: $SESSION_ID"
Start-Sleep -Seconds 1
Write-Host "[BROKER] Ready."
Write-Host ""

# ═══════════════════════════════════════════════════════════════════════════════
# STEP 2: LAUNCH HOST INSTANCE (HUMAN PLAYER)
# ═══════════════════════════════════════════════════════════════════════════════
Write-Host "=== Instance 1: HUMAN (Host) ===" -ForegroundColor Green

# Write HOST per-instance marker file AND shared marker (backup)
$hostConfig = @"
role=host
clientIndex=0
endpoint=127.0.0.1:$BROKER_PORT
sessionId=$SESSION_ID
"@
$hostConfig | Out-File -FilePath $MARKER_HOST -Encoding ASCII
$hostConfig | Out-File -FilePath $MARKER_SHARED -Encoding ASCII
Write-Host "[HUMAN] Marker written: $MARKER_HOST (+ shared backup)"
Write-Host "[HUMAN]   role=host, clientIndex=0, endpoint=127.0.0.1:$BROKER_PORT"

# Set env var for this process (inherited by child via Start-Process)
$env:TOKENSPIRE2_ROLE = "host"
Write-Host "[HUMAN]   TOKENSPIRE2_ROLE=host"
Write-Host "[HUMAN] Starting SlayTheSpire2.exe..."
$hostProc = Start-Process -FilePath $GAME_EXE -PassThru -WorkingDirectory $GAME_DIR
Write-Host "[HUMAN] PID: $($hostProc.Id)"

# ── Wait for human to navigate to Host Game lobby ─────────────────────────
Write-Host "[WAIT] Human navigating to Host Game (25s)..."
Start-Sleep -Seconds 25

# ═══════════════════════════════════════════════════════════════════════════════
# STEP 3: LAUNCH CLIENT INSTANCE (BOT)
# ═══════════════════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "=== Instance 2: BOT (Client) ===" -ForegroundColor Cyan

# Write CLIENT per-instance marker file AND shared marker (backup)
$clientConfig = @"
role=client
clientIndex=1
endpoint=127.0.0.1:$BROKER_PORT
sessionId=$SESSION_ID
"@
$clientConfig | Out-File -FilePath $MARKER_CLIENT -Encoding ASCII
$clientConfig | Out-File -FilePath $MARKER_SHARED -Encoding ASCII
Write-Host "[BOT] Marker written: $MARKER_CLIENT (+ shared backup)"
Write-Host "[BOT]   role=client, clientIndex=1, endpoint=127.0.0.1:$BROKER_PORT"

$env:TOKENSPIRE2_ROLE = "client"
Write-Host "[BOT]   TOKENSPIRE2_ROLE=client"
Write-Host "[BOT] Starting SlayTheSpire2.exe..."
$clientProc = Start-Process -FilePath $GAME_EXE -PassThru -WorkingDirectory $GAME_DIR
Write-Host "[BOT] PID: $($clientProc.Id)"

# ═══════════════════════════════════════════════════════════════════════════════
# INSTRUCTIONS
# ═══════════════════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "====================================================" -ForegroundColor Yellow
Write-Host "  LAN CO-OP LAUNCHED! (2 Windows + Broker Server)" -ForegroundColor Yellow
Write-Host "====================================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Broker Server: PID $($brokerProc.Id), Port $BROKER_PORT"
Write-Host ""
Write-Host "  Window 1 (HOST) — YOUR WINDOW:"
Write-Host "    Auto-navigates: Multiplayer → Host Game → Lobby"
Write-Host "    → Pick your character → Ready → Wait for bot"
Write-Host ""
Write-Host "  Window 2 (CLIENT) — BOT:"
Write-Host "    Auto-navigates: Multiplayer → Join Friends"
Write-Host "    → Broker auto-connects and joins your room"
Write-Host "    → Auto-readies → Game starts when you embark!"
Write-Host ""
Write-Host "  Press T in YOUR window to toggle auto-battle"
Write-Host "====================================================" -ForegroundColor Yellow
Write-Host ""

Remove-Item Env:\TOKENSPIRE2_ROLE -ErrorAction SilentlyContinue

Write-Host "Press any key to close this window (games keep running)..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
