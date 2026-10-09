@echo off
rem Stops eve.rs processes recorded by start-server (forceful; see tools/windows/README.md).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0stop-server.ps1" %*
exit /b %ERRORLEVEL%
