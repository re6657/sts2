@echo off
chcp 65001 >nul
REM TokenSpire2 LAN 多人对战 — 双击启动
REM
REM 修改下面的参数即可自定义:
REM   CHARACTER: IRONCLAD / SILENT / DEFECT / REGENT / NECROBINDER
REM   BOTCOUNT:  1-3 (Bot数量)
REM   SEED:      留空=随机种子, 填入=固定种子

set CHARACTER=IRONCLAD
set BOTCOUNT=2
set SEED=

REM Derive game dir relative to this script: TokenSpire2 → mods → Slay the Spire 2
pushd %~dp0..\..
set GAME_DIR=%CD%
popd
set MOD_DIR=%GAME_DIR%\mods\TokenSpire2

echo ============================================================
echo  TokenSpire2 多人对战启动器
echo  角色: %CHARACTER%  Bot: %BOTCOUNT%  种子: %SEED%
echo ============================================================

echo [清理] 删除旧文件...
del /q "%MOD_DIR%\config_read*.signal" 2>nul
del /q "%GAME_DIR%\token_spire_*.json" 2>nul

REM 种子JSON
if "%SEED%"=="" (set SEED_JSON=null) else (set SEED_JSON="%SEED%")

REM Host 配置
echo {"Seed":%SEED_JSON%,"Character":"%CHARACTER%","MultiplayerMode":true,"IsMultiplayerHost":true,"SteamPersonaName":"Player","AutoBattleEnabled":false,"SignalFile":"config_read_host.signal"} > "%GAME_DIR%\token_spire_host.json"
echo [配置] Host: token_spire_host.json

REM Bot 配置
for /L %%i in (1,1,%BOTCOUNT%) do (
    echo {"Seed":%SEED_JSON%,"Character":"%CHARACTER%","MultiplayerMode":true,"IsMultiplayerHost":false,"SteamPersonaName":"Bot%%i","AutoBattleEnabled":true,"SignalFile":"config_read_bot%%i.signal"} > "%GAME_DIR%\token_spire_bot%%i.json"
    echo [配置] Bot%%i: token_spire_bot%%i.json
)

REM 启动 Host
echo.
echo [启动] Host (窗口1)...
start "TokenSpire2 Host" "%GAME_DIR%\SlayTheSpire2.exe" --fastmp host_standard --config "%GAME_DIR%\token_spire_host.json"
echo 等待 Host 就绪 (60s)...
timeout /t 60 /nobreak >nul

REM 启动 Bot
for /L %%i in (1,1,%BOTCOUNT%) do (
    echo [启动] Bot%%i...
    start "TokenSpire2 Bot%%i" "%GAME_DIR%\SlayTheSpire2.exe" --fastmp join --config "%GAME_DIR%\token_spire_bot%%i.json"
    timeout /t 15 /nobreak >nul
)

echo.
echo ============================================================
echo  启动完成! %BOTCOUNT% Bot + 1 Host = %BOTCOUNT%+1 窗口
echo  窗口1 (Host): ENet server 127.0.0.1:33771
echo ============================================================
pause
