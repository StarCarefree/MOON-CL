@echo off
setlocal
cd /d "%~dp0"

set "PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not exist "%PWSH%" set "PWSH=pwsh"

if "%~1"=="" (
  "%PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-SingleExe.ps1" -Architecture x64 -Configuration Release
) else (
  "%PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-SingleExe.ps1" %*
)
set ERR=%ERRORLEVEL%
echo.
echo exit=%ERR%
if not "%NO_PAUSE%"=="1" pause
exit /b %ERR%
