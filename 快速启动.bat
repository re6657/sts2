@echo off
chcp 65001 >nul
REM TokenSpire2 快速启动 — 跳过编译，信号文件轮询

set CHARACTER=IRONCLAD
set BOTCOUNT=2
set SEED=

pushd %~dp0..\..
set GAME_DIR=%CD%
popd
set MOD_DIR=%GAME_DIR%\mods\TokenSpire2

echo ============================================================
echo  TokenSpire2 快速启动
echo ============================================================

REM 清理旧文件
del /q "%MOD_DIR%\config_read*.signal" 2>nul
del /q "%GAME_DIR%\token_spire_*.json" 2>nul

REM 种子
if "%SEED%"=="" (set SEED_JSON=null) else (set SEED_JSON="%SEED%")

REM Host 配置
echo {"Seed":%SEED_JSON%,"Character":"%CHARACTER%","MultiplayerMode":true,"IsMultiplayerHost":true,"SteamPersonaName":"Player","AutoBattleEnabled":false,"SignalFile":"config_read_host.signal"} > "%GAME_DIR%\token_spire_host.json"

REM Bot 配置
for /L %%i in (1,1,%BOTCOUNT%) do (
    echo {"Seed":%SEED_JSON%,"Character":"%CHARACTER%","MultiplayerMode":true,"IsMultiplayerHost":false,"SteamPersonaName":"Bot%%i","AutoBattleEnabled":true,"SignalFile":"config_read_bot%%i.signal"} > "%GAME_DIR%\token_spire_bot%%i.json"
)

REM 启动 Host
echo [1/3] 启动 Host...
start "TokenSpire2 Host" "%GAME_DIR%\SlayTheSpire2.exe" --fastmp host_standard --config "%GAME_DIR%\token_spire_host.json"

REM 等待 Host 信号文件 (config 加载完毕)
echo 等待 Host 就绪...
:wait_host
timeout /t 2 /nobreak >nul
if not exist "%MOD_DIR%\config_read_host.signal" goto wait_host
echo Host 就绪!

REM 启动 Bot1
echo [2/3] 启动 Bot1...
start "TokenSpire2 Bot1" "%GAME_DIR%\SlayTheSpire2.exe" --fastmp join --config "%GAME_DIR%\token_spire_bot1.json"
timeout /t 5 /nobreak >nul

REM 启动 Bot2 (如果有)
if %BOTCOUNT% GEQ 2 (
    echo [3/3] 启动 Bot2...
    start "TokenSpire2 Bot2" "%GAME_DIR%\SlayTheSpire2.exe" --fastmp join --config "%GAME_DIR%\token_spire_bot2.json"
)

echo.
echo ============================================================
echo  启动完成! %BOTCOUNT% Bot + 1 Host
echo  窗口1 (Host): ENet server 127.0.0.1:33771
echo  Host 手动操作，Bot 自动加入
echo ============================================================
pause
