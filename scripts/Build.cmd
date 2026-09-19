@echo off
cd /d "%~dp0.."
echo Building local sources. This does not install tools, drivers, or services.
pwsh.exe -NoProfile -File "%~dp0Build.ps1" %*
set "BUILD_EXIT=%ERRORLEVEL%"
if not "%BUILD_EXIT%"=="0" echo Build failed. Read the message above and work/logs/build.log if present.
pause
exit /b %BUILD_EXIT%
