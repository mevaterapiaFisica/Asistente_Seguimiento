@echo off
REM ============================================================
REM  Meva RT - Actualizacion completa (Win10 standalone)
REM
REM  Secuencia:
REM    1. Scraping SitraMed (sin ARIA) → genera snapshot + pacientes.json
REM    2. Consultar ARIA (impersonando ECL-FISICA2\varian)
REM    3. Importar resultados ARIA al servidor
REM    4. Refrescar dashboard final (merge SitraMed + ARIA)
REM    5. Scrapear agenda equipos proximos 15 dias habiles
REM    6. Scrapear agenda tomografos proximos 15 dias habiles
REM
REM  CONFIGURACION REQUERIDA (editar esta seccion):
REM ============================================================

set MEVA_URL=http://localhost:5062
set DATA_DIR=C:\MevaRT\data
set RUNNER_EXE=C:\Pablo\Meva.Rt\Meva.Rt.AriaRunner\bin\Release\net9.0-windows\AriaRunner.exe

REM Contrasena del usuario varian en ECL-FISICA2 (cuenta Windows con acceso a ARIAMEVADB-SVR)
set ARIA_VARIAN_PASSWORD=1e$civres

REM ============================================================

echo [%date% %time%] === Iniciando actualizacion Meva RT ===

REM ── 1. Scraping SitraMed sin ARIA → snapshot + pacientes.json ─
echo [%date% %time%] 1/6 Scrapeando SitraMed (sin ARIA)...
curl -s --max-time 1800 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/home/refresh-no-aria"

REM ── 2. Consultar ARIA ────────────────────────────────────────
echo [%date% %time%] 2/6 Consultando ARIA (ECL-FISICA2\varian @ ARIAMEVADB-SVR)...
"%RUNNER_EXE%" --input="%DATA_DIR%\pacientes.json" --output-dir="%DATA_DIR%"
if %ERRORLEVEL% NEQ 0 (
    echo [%date% %time%] ERROR en paso 2: AriaRunner termino con codigo %ERRORLEVEL%
    goto :error
)

REM ── 3. Importar resultados ARIA ──────────────────────────────
echo [%date% %time%] 3/6 Importando resultados ARIA...
curl -s --max-time 120 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/aria/import-results"

REM ── 4. Refrescar dashboard final (SitraMed + ARIA) ───────────
echo [%date% %time%] 4/6 Refrescando dashboard final...
curl -s --max-time 1800 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/home/refresh"

REM ── 5. Agenda equipos ────────────────────────────────────────
echo [%date% %time%] 5/6 Scrapeando agenda equipos...
curl -s --max-time 1800 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/agenda/scrape-upcoming?days=15"

REM ── 6. Agenda tomografos ─────────────────────────────────────
echo [%date% %time%] 6/6 Scrapeando agenda tomografos...
curl -s --max-time 1800 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/tomograph-agenda/scrape-upcoming?days=15"

echo [%date% %time%] === Actualizacion completada ===
exit /b 0

:error
echo [%date% %time%] === Actualizacion FALLIDA ===
exit /b 1
