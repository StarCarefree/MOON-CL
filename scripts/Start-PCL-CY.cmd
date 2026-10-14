@echo off
setlocal
REM Do not download .NET until the user clicks Yes in Ensure-DotNet10.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Ensure-DotNet10.ps1" %*
exit /b %ERRORLEVEL%
