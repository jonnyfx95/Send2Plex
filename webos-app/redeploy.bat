@echo off
title NimbusFX WebOS - Pacchetta e installa
cd /d "%~dp0"

set DEVICE=send2plex-tv
set APPID=com.nimbusfx.tv

echo ===============================================
echo   Impacchetto l'app...
echo ===============================================
del /q *.ipk >nul 2>&1
rem "call" e' necessario: ares-package/-install/-launch sono shim .cmd (npm), senza "call" il
rem controllo non torna mai a questo script dopo la prima chiamata (bug reale osservato: lo script
rem si fermava silenziosamente subito dopo il packaging, saltando disinstallazione/installazione/
rem avvio senza alcun errore visibile).
call ares-package . -o .
if errorlevel 1 goto :error

set IPK=
for %%f in (*.ipk) do set IPK=%%f
if "%IPK%"=="" (
    echo *** ERRORE: nessun file .ipk generato. ***
    goto :error
)

echo.
echo ===============================================
echo   Disinstallo la versione precedente (se presente)...
echo ===============================================
call ares-install --device %DEVICE% -r %APPID%
rem Fallisce normalmente al primo deploy (l'app non e' ancora installata): non e' un errore fatale.

echo.
echo ===============================================
echo   Installo %IPK%...
echo ===============================================
call ares-install --device %DEVICE% "%IPK%"
if errorlevel 1 goto :error

echo.
echo ===============================================
echo   Avvio l'app sulla TV...
echo ===============================================
call ares-launch --device %DEVICE% %APPID%
if errorlevel 1 goto :error

echo.
echo Fatto: %APPID% installato e avviato su %DEVICE%.
pause
exit /b 0

:error
echo.
echo *** ERRORE durante il redeploy: controlla i messaggi sopra. ***
echo *** Se la chiave/passphrase di pairing e' scaduta, rigenerala dall'app ***
echo *** "Developer Mode" sulla TV e rilancia: ***
echo ***   ares-novacom --device %DEVICE% --getkey ***
pause
exit /b 1
