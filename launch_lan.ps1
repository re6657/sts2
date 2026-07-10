# ============================================================================
# TokenSpire2 Dual-Instance LAN Multiplayer Launcher (Broker Mode)
# ============================================================================
# Architecture:
#   - BrokerServer.exe relays TCP messages between game instances
#   - Harmony patches intercept Steam networking → redirect to TCP broker
#   - HOST instance (human): creates room, selects character
#   - CLIENT instance (bot): auto-joins, auto-plays
# ============================================================================

$ErrorActionPreference = "Stop"

$STS2_DIR   = "E:\SteamLibrary\steamapps\common\Slay the Spire 2"
$MOD_DIR    = "$STS2_DIR\mods\TokenSpire2"
$BROKER_DIR = "$MOD_DIR\BrokerServer\bin\Release\net8.0"
$SESSION_ID = "coop-session"
$BROKER_PORT = 9999
$ENDPOINT   = "127.0.0.1:$BROKER_PORT"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " TokenSpire2 LAN Multiplayer Launcher" -ForegroundColor Cyan
Write-Host " Mode: TCP Broker" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Session: $SESSION_ID" -ForegroundColor White
Write-Host " Broker:  $ENDPOINT" -ForegroundColor White
Write-Host ""

# ── Step 0: Write per-instance marker files ───────────────────────────
Write-Host "[0/4] Writing broker marker files..." -ForegroundColor Yellow

$hostMarker = @"
role=host
clientIndex=0
endpoint=$ENDPOINT
sessionId=$SESSION_ID
"@
$hostMarker | Out-File -FilePath "$MOD_DIR\enable-local-broker-host.txt" -Encoding ASCII -NoNewline
Write-Host "  Host marker: enable-local-broker-host.txt" -ForegroundColor Green

$clientMarker = @"
role=client
clientIndex=1
endpoint=$ENDPOINT
sessionId=$SESSION_ID
"@
$clientMarker | Out-File -FilePath "$MOD_DIR\enable-local-broker-client.txt" -Encoding ASCII -NoNewline
Write-Host "  Client marker: enable-local-broker-client.txt" -ForegroundColor Green

# Remove shared marker if it exists (would cause ambiguity)
Remove-Item "$MOD_DIR\enable-local-broker.txt" -ErrorAction SilentlyContinue

# ── Step 1: Build BrokerServer if needed ───────────────────────────────
Write-Host "[1/4] Checking BrokerServer..." -ForegroundColor Yellow
$brokerExe = "$BROKER_DIR\BrokerServer.exe"
if (-not (Test-Path $brokerExe)) {
    Write-Host "  Building BrokerServer..." -ForegroundColor Yellow
    dotnet publish "$MOD_DIR\BrokerServer\BrokerServer.csproj" -c Release -f net8.0 -o "$BROKER_DIR" --no-self-contained 2>&1 | Out-Null
    if (-not (Test-Path $brokerExe)) {
        Write-Host "  ERROR: Failed to build BrokerServer" -ForegroundColor Red
        exit 1
    }
}
Write-Host "  BrokerServer: OK" -ForegroundColor Green

# ── Step 2: Start BrokerServer ─────────────────────────────────────────
Write-Host "[2/4] Starting TCP Broker Server..." -ForegroundColor Yellow

# Kill any stale broker from previous run
Get-Process BrokerServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

$brokerProc = Start-Process -FilePath $brokerExe -ArgumentList "--port $BROKER_PORT", "--session-id $SESSION_ID" -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 2

if ($brokerProc.HasExited) {
    Write-Host "  ERROR: BrokerServer exited immediately (code: $($brokerProc.ExitCode))" -ForegroundColor Red
    exit 1
}
Write-Host "  BrokerServer PID: $($brokerProc.Id)" -ForegroundColor Green

# ── Step 3: Launch HOST instance (human player) ────────────────────────
Write-Host "[3/4] Launching HOST instance..." -ForegroundColor Yellow

$hostBat = @"
@echo off
set TOKENSPIRE2_ROLE=host
cd /D "$STS2_DIR"
start "STS2 Host" SlayTheSpire2.exe
"@
$hostBatPath = "$env:TEMP\sts2_host_launcher.bat"
$hostBat | Out-File -FilePath $hostBatPath -Encoding ASCII
$hostProc = Start-Process -FilePath $hostBatPath -PassThru
Start-Sleep -Seconds 5
Write-Host "  HOST launched (batch PID: $($hostProc.Id))" -ForegroundColor Green

# ── Step 4: Wait then launch CLIENT instance (bot) ────────────────────
Write-Host "[4/4] Waiting 25s for host to create lobby, then launching CLIENT..." -ForegroundColor Yellow
Start-Sleep -Seconds 25

$clientBat = @"
@echo off
set TOKENSPIRE2_ROLE=client
cd /D "$STS2_DIR"
start "STS2 Client" SlayTheSpire2.exe
"@
$clientBatPath = "$env:TEMP\sts2_client_launcher.bat"
$clientBat | Out-File -FilePath $clientBatPath -Encoding ASCII
$clientProc = Start-Process -FilePath $clientBatPath -PassThru
Write-Host "  CLIENT launched (batch PID: $($clientProc.Id))" -ForegroundColor Green

# Cleanup temp batch files
Start-Sleep -Seconds 3
Remove-Item $hostBatPath -ErrorAction SilentlyContinue
Remove-Item $clientBatPath -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Lanuch Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " HOST:   PID $($hostProc.Id) — create lobby, select character, Embark" -ForegroundColor White
Write-Host " CLIENT: PID $($clientProc.Id) — auto-join, auto-ready, auto-play" -ForegroundColor White
Write-Host " Broker: PID $($brokerProc.Id) — localhost:$BROKER_PORT" -ForegroundColor White
Write-Host " Press T to toggle auto-battle on HOST" -ForegroundColor White
Write-Host "========================================" -ForegroundColor Cyan
