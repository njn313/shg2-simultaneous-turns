@echo off
rem Packs the player files into Shogun2-SharedTurns_<hash>.zip in this folder (no game files).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make_zip.ps1"
echo.
pause
