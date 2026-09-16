@echo off
REM ── Iniciar minimizado (se re-lanza en ventana minimizada si es la primera ejecucion) ──
if not "%MEVA_MINIMIZED%"=="1" (
    set MEVA_MINIMIZED=1
    start "Meva RT Refresh" /min cmd /c ""%~f0""
    exit /b 0
)

REM ============================================================
REM  Meva RT - Actualizacion completa (Win10 standalone)
REM
REM  Secuencia:
REM    1. Scraping SitraMed (sin ARIA) → genera snapshot + pacientes.json
REM    2. Consultar ARIA (impersonando ECL-FISICA2\varian)
REM    3. Importar resultados ARIA al servidor
REM    4. Aplicar datos ARIA al snapshot (sin re-scrapear)
REM    5. Scrapear agenda equipos proximos 15 dias habiles
REM    6. Scrapear agenda tomografos proximos 15 dias habiles
REM    7. Importar mails de TBI (fecha tomo/inicio/equipo)
REM    8. Importar dosis TBI desde SitraMed (dosis diaria/total)
REM
REM  CONFIGURACION REQUERIDA (editar esta seccion):
REM ============================================================

set MEVA_URL=http://localhost:5062
set DATA_DIR=C:\MevaRT\data
set RUNNER_EXE=C:\MevaRT\AriaRunner\AriaRunner.exe

REM Dias habiles hacia adelante a scrapear (agenda equipos + tomografos). Debe coincidir con
REM "UpcomingScrapeDays" en rt_configuration.json — si no, quedan agendas de fechas lejanas
REM congeladas en el ultimo scrape que las cubrio (ver BUG_AGENDA_EQUIPOS_Y_ESTIMADOS.md).
set UPCOMING_DAYS=15

REM Contrasena del usuario varian en ECL-FISICA2 (cuenta Windows con acceso a ARIAMEVADB-SVR)
set ARIA_VARIAN_PASSWORD=1e$civres

REM ============================================================

echo [%date% %time%] === Iniciando actualizacion Meva RT ===

REM ── 1. Scraping SitraMed sin ARIA → snapshot + pacientes.json ─
echo [%date% %time%] 1/8 Scrapeando SitraMed (sin ARIA)...
curl -s --max-time 1800 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/home/refresh-no-aria"

REM ── 2. Consultar ARIA ────────────────────────────────────────
echo [%date% %time%] 2/8 Consultando ARIA (ECL-FISICA2\varian @ ARIAMEVADB-SVR)...
"%RUNNER_EXE%" --input="%DATA_DIR%\pacientes.json" --output-dir="%DATA_DIR%"
if %ERRORLEVEL% NEQ 0 (
    echo [%date% %time%] ERROR en paso 2: AriaRunner termino con codigo %ERRORLEVEL%
    goto :error
)

REM ── 3. Importar resultados ARIA ──────────────────────────────
echo [%date% %time%] 3/8 Importando resultados ARIA...
curl -s --max-time 120 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/aria/import-results"

REM ── 4. Aplicar datos ARIA al snapshot (sin re-scrapear) ─────
echo [%date% %time%] 4/8 Aplicando datos ARIA al snapshot...
curl -s --max-time 120 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/home/apply-aria"

REM ── 5. Agenda equipos ────────────────────────────────────────
echo [%date% %time%] 5/8 Scrapeando agenda equipos...
curl -s --max-time 1800 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/agenda/scrape-upcoming?days=%UPCOMING_DAYS%"

REM ── 6. Agenda tomografos ─────────────────────────────────────
echo [%date% %time%] 6/8 Scrapeando agenda tomografos...
curl -s --max-time 1800 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/tomograph-agenda/scrape-upcoming?days=%UPCOMING_DAYS%"

REM ── 7. Mails TBI ──────────────────────────────────────────────
echo [%date% %time%] 7/8 Importando mails de TBI...
curl -s --max-time 300 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/tbi-mail/refresh"

REM ── 8. Dosis TBI (SitraMed) ───────────────────────────────────
echo [%date% %time%] 8/8 Importando dosis TBI desde SitraMed...
curl -s --max-time 1800 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/tbi-dose/refresh"

echo [%date% %time%] === Actualizacion completada ===
exit /b 0

:error
echo [%date% %time%] === Actualizacion FALLIDA ===
exit /b 1
