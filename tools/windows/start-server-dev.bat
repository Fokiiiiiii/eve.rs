@echo off
rem Starts the eve.rs server for development: honours EVE_TRACE_RPC and EVE_STRICT_RPC
rem (set them before running, or pass -Trace and -Strict). Extra arguments go to start-server.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-server.ps1" -Dev %*
exit /b %ERRORLEVEL%
