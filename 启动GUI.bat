@echo off
chcp 65001 >nul
REM TokenSpire2 GUI 启动器 — 自动检测 .NET 版本并运行
pushd "%~dp0tools\Launcher"

REM 检测可用运行时，优先级: 8.0(LTS) > 10.0 > 9.0 > 6.0
set TFM=
dotnet --list-runtimes 2>nul | findstr "Microsoft.NETCore.App" > "%TEMP%\dotnet_rt.txt"
findstr "8.0." "%TEMP%\dotnet_rt.txt" >nul 2>nul && set TFM=net8.0-windows
if "%TFM%"=="" (findstr "10.0." "%TEMP%\dotnet_rt.txt" >nul 2>nul && set TFM=net10.0-windows)
if "%TFM%"=="" (findstr "9.0." "%TEMP%\dotnet_rt.txt" >nul 2>nul && set TFM=net9.0-windows)
if "%TFM%"=="" (findstr "6.0." "%TEMP%\dotnet_rt.txt" >nul 2>nul && set TFM=net6.0-windows)
if "%TFM%"=="" (
    echo [错误] 未找到 .NET 运行时 (需要 6.0+)
    echo 安装 .NET 8.0: https://dotnet.microsoft.com/download
    pause & exit /b 1
)

echo [TokenSpire2 GUI Launcher] .NET = %TFM%

REM 编译 (通过 -p 覆盖 TFM，不修改 csproj)
dotnet build -c Release -p:TargetFramework=%TFM% --nologo -v q 2>&1
if %ERRORLEVEL% neq 0 (
    echo [错误] 编译失败！
    pause & exit /b 1
)

REM 找到输出的 exe 并启动
for /d %%d in (bin\Release\*) do if exist "%%d\TokenSpire2Launcher.exe" start "" "%%d\TokenSpire2Launcher.exe" & goto :done

echo [错误] 找不到编译输出
pause & exit /b 1

:done
popd
exit /b 0
