@echo off
REM ============================================================
REM  Meva RT - Actualizacion automatica via Task Scheduler
REM  Configura en Task Scheduler apuntando a este .bat
REM  Requiere que Meva.Rt.Web este corriendo en el puerto indicado
REM ============================================================

set MEVA_URL=http://localhost:5000

echo [%date% %time%] Iniciando actualizacion Meva RT...

REM 1) Actualizar seguimiento + agenda de hoy + ARIA
curl -s -o NUL -w "  /api/home/refresh: %%{http_code}\n" -X POST "%MEVA_URL%/api/home/refresh"

REM 2) Scrapear agenda de los proximos 15 dias habiles
curl -s -o NUL -w "  /api/agenda/scrape-upcoming: %%{http_code}\n" -X POST "%MEVA_URL%/api/agenda/scrape-upcoming?days=15"

echo [%date% %time%] Actualizacion completada.
