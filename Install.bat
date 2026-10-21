@echo off
rem Shogun 2 Shared Turns: close the game first. With the 8-player co-op mod, install that one first.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
