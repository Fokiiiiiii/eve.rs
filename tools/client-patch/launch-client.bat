@echo off
rem Starts launch-client.ps1 with the same arguments, for example:
rem   launch-client.bat -ClientDir "C:\EVE\SharedCache\tq" -Port 26000
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-client.ps1" %*
if errorlevel 1 pause
