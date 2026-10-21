@echo off
rem Shogun 2 Shared Turns: puts back the DLL it patched (the original, or the 8-player co-op mod). Close the game first.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall %*
echo.
pause
