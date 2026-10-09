@echo off
rem Starts the eve.rs server (see tools/windows/README.md). Extra arguments go to start-server.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-server.ps1" %*
exit /b %ERRORLEVEL%
