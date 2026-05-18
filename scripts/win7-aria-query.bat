@echo off
REM ============================================================
REM  Meva RT - Consulta de pacientes en ARIA (Win7)
REM  Llamar desde Task Scheduler en la PC con ARIA.
REM  Ver INSTRUCCIONES_WIN7_AUTOMATICO.txt para configuracion.
REM
REM  CONFIGURACION REQUERIDA:
REM    WIN10_SHARE  → ruta UNC a la carpeta compartida del Win10
REM                   Ej: \\MEVA-WIN10\MevaRtData
REM    RUNNER_DIR   → carpeta local donde esta AriaRunner.exe
REM                   Ej: C:\AriaRunner
REM ============================================================

set WIN10_SHARE=\\WIN10-HOSTNAME\MevaRtData
set RUNNER_DIR=C:\AriaRunner

echo [%date% %time%] Iniciando consulta ARIA...
echo   Input:      %WIN10_SHARE%\pacientes.json
echo   Output dir: %WIN10_SHARE%

"%RUNNER_DIR%\AriaRunner.exe" --input="%WIN10_SHARE%\pacientes.json" --output-dir="%WIN10_SHARE%"

if %ERRORLEVEL% NEQ 0 (
    echo [%date% %time%] ERROR: AriaRunner termino con codigo %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)

echo [%date% %time%] Consulta ARIA completada.
