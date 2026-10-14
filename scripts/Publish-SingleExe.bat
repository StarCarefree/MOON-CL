@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
title Publish MOONCL single exe
chcp 65001 >nul

set "PS1=%~dp0Publish-SingleExe.ps1"
if not exist "%PS1%" (
  echo 找不到 Publish-SingleExe.ps1
  pause
  exit /b 1
)

set "PWSH="
where pwsh >nul 2>&1 && set "PWSH=pwsh"
if not defined PWSH if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined PWSH if exist "%LocalAppData%\Microsoft\WindowsApps\pwsh.exe" set "PWSH=%LocalAppData%\Microsoft\WindowsApps\pwsh.exe"
if not defined PWSH (
  echo 需要 PowerShell 7。请先安装 pwsh，再用本 bat 打开打包脚本。
  pause
  exit /b 1
)

set "ARGS=%*"
echo !ARGS! | findstr /I /C:"-Architecture" >nul
if errorlevel 1 set "ARGS=-Architecture x64 !ARGS!"
echo !ARGS! | findstr /I /C:"-Configuration" >nul
if errorlevel 1 set "ARGS=-Configuration Release !ARGS!"

echo 使用: "%PWSH%" -File "%PS1%" !ARGS!
echo 其它编译还在跑时不要继续。
"%PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%PS1%" !ARGS!
set "ERR=!ERRORLEVEL!"
echo.
if not "!ERR!"=="0" echo 打包失败，退出码 !ERR!
pause
exit /b !ERR!
