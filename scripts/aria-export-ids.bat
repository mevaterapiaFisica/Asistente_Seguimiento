@echo off
REM ============================================================
REM  Meva RT - Exportar IDs de pacientes para consulta en ARIA
REM  Llamar desde Task Scheduler en Win10, ANTES de la tarea
REM  de Win7. Ver INSTRUCCIONES_WIN7_AUTOMATICO.txt.
REM
REM  Requiere que el servicio MevaRT este corriendo en %MEVA_URL%
REM  Requiere curl.exe (incluido en Windows 10 1803+)
REM ============================================================

set MEVA_URL=http://localhost:5062
set DATA_DIR=C:\Pablo\WebScrapSitra\Meva.Rt\Meva.Rt.Web\data

echo [%date% %time%] Exportando IDs de pacientes para ARIA...

curl -s --max-time 600 -X GET "%MEVA_URL%/api/aria/export-patient-ids" -o "%DATA_DIR%\pacientes.json"

if %ERRORLEVEL% NEQ 0 (
    echo [%date% %time%] ERROR: No se pudo exportar los IDs. Codigo: %ERRORLEVEL%
    exit /b 1
)

echo [%date% %time%] Exportacion completada. Archivo: %DATA_DIR%\pacientes.json
