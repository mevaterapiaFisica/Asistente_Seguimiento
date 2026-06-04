@echo off
REM ============================================================
REM  Meva RT - Actualizacion RAPIDA para debug
REM
REM  Diferencias con refresh.bat completo:
REM    - NO re-scrapea seguimiento SitraMed (usa snapshot existente)
REM    - NO re-corre AriaRunner.exe            (usa mock ARIA existente)
REM    - Agenda equipos: solo hoy + 1 dia habil (en vez de 7)
REM    - NO scrapea agenda tomografos
REM
REM  Tiempo estimado: < 2 min (vs 20-30 min del refresh completo)
REM
REM  Cuando usarlo:
REM    - Probar cambios de UI o logica sin esperar el ciclo completo
REM    - Verificar badges, labels, propagacion de datos ARIA
REM    - Debugging general donde los datos de fondo no cambiaron
REM
REM  CONFIGURACION:
REM ============================================================

set MEVA_URL=http://localhost:5062

REM ── 1. Aplicar datos ARIA existentes al snapshot ─────────────────────────
REM    (re-lee aria_plans_mock.json y recalcula TreatmentLabel sin re-scrapear)
echo [%date% %time%] 1/2 Aplicando ARIA al snapshot existente...
curl -s --max-time 60 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/home/apply-aria"

REM ── 2. Agenda equipos: hoy + 1 dia habil ──────────────────────────────────
REM    (cada dia extra suma ~30 seg de scraping Playwright)
echo [%date% %time%] 2/2 Scrapeando agenda equipos (2 dias)...
curl -s --max-time 300 -o NUL -w "  HTTP %%{http_code}\n" -X POST "%MEVA_URL%/api/agenda/scrape-upcoming?days=2"

echo [%date% %time%] === Debug refresh completado ===
