@echo off
cd /d "%~dp0"
echo Unblocking this folder so Windows will start the app.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%~dp0.' -Recurse -File | Unblock-File"
echo.
echo Starting the till. Leave this window open.
echo In the browser open http://127.0.0.1:5080
echo Use http, not https. This sample has no certificate.
echo.
start "" cmd /c "timeout /t 2 /nobreak >nul & start http://127.0.0.1:5080"
AmwalECR.WebExample.exe --urls http://127.0.0.1:5080
if errorlevel 1 (
  echo.
  echo The app did not start.
  echo If Windows said the publisher is unknown: close that message, right-click
  echo Run-Localhost.cmd, open Properties, tick Unblock, Apply, then run it again.
  echo.
  pause
)
