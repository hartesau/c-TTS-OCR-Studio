@echo off
color 0A
echo ===================================================
echo   TTS STUDIO - MEISTER EDITION - KOMPILER
echo ===================================================
echo.
echo Raeume alte Dateien auf und starte Build...
echo.

dotnet build -c Release

echo.
echo ===================================================
echo Fertig! Druecke eine beliebige Taste zum Schliessen.
pause >nul