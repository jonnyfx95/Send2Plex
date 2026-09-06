@echo off
setlocal

rem === Modifica questi due percorsi se vuoi pubblicare altrove ===
set "PROJECT_DIR=C:\Users\jonny\Source\repos\Send2Plex"
set "PUBLISH_DIR=C:\Send2Plex"
set "TASK_NAME=Send2Plex"

echo ===============================================
echo   Send2Plex - configurazione avvio automatico
echo ===============================================
echo.

echo [1/3] Pubblico l'app in "%PUBLISH_DIR%"...
dotnet publish "%PROJECT_DIR%\Send2Plex.csproj" -c Release -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo.
    echo ERRORE: la pubblicazione e' fallita. Controlla i messaggi sopra.
    pause
    exit /b 1
)

echo.
echo [2/3] Creo lo script di avvio "%PUBLISH_DIR%\avvia-send2plex.bat"...
(
    echo @echo off
    echo cd /d "%PUBLISH_DIR%"
    echo start "Send2Plex" /min "Send2Plex.exe"
) > "%PUBLISH_DIR%\avvia-send2plex.bat"

echo.
echo [3/3] Registro l'attivita pianificata "%TASK_NAME%" (avvio all'accesso utente)...
schtasks /create /tn "%TASK_NAME%" /tr "\"%PUBLISH_DIR%\avvia-send2plex.bat\"" /sc onlogon /f
if errorlevel 1 (
    echo.
    echo ERRORE: creazione dell'attivita pianificata fallita.
    pause
    exit /b 1
)

echo.
echo ===============================================
echo  Fatto! Send2Plex partira' automaticamente al
echo  prossimo accesso a Windows ^(finestra minimizzata^).
echo.
echo  Per avviarlo subito, senza riavviare Windows:
echo  doppio click su "%PUBLISH_DIR%\avvia-send2plex.bat"
echo.
echo  Per rimuovere l'avvio automatico in futuro, apri
echo  il prompt dei comandi e digita:
echo    schtasks /delete /tn "%TASK_NAME%" /f
echo ===============================================
pause
