@echo off
title Send2Plex
cd /d "%~dp0"

echo ===============================================
echo   Send2Plex - avvio server
echo ===============================================
echo.

dotnet run --project "Send2Plex.csproj"

echo.
echo Il server si e' fermato.
pause
