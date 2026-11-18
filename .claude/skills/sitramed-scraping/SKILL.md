---
name: sitramed-scraping
description: >
  Guía para tocar, debuggear o extender el scraping Playwright de SitraMed en Meva.Rt
  (Meva.Rt.Infrastructure.SitraMed). Usar SIEMPRE que la tarea toque
  PlaywrightSitraMedClient.cs, SitraMedExtractors.cs, FollowUpDateParser.cs,
  SitraMedPatientHcFetcher.cs, SitraMedPatientPhoneFetcher.cs, cualquier endpoint
  /api/scraping/*, /api/agenda*, /api/tomograph-agenda*, /api/derivation/*, o cuando el
  usuario reporte que un paciente/turno "no aparece", "aparece en fecha equivocada",
  "aparece duplicado", "sale con técnica mal clasificada", o pida agregar un campo nuevo
  extraído de SitraMed (agenda, seguimiento, tomógrafos, GUID→HC, teléfono, dosis TBI,
  etc). También usar antes de escribir cualquier código Playwright nuevo contra un form
  de SitraMed, aunque parezca trivial — SitraMed tiene quirks de Phoenix LiveView que
  rompen scrapers "obvios" de forma silenciosa (sin error, con datos de otro día).
---

# SitraMed scraping (Meva.Rt)

SitraMed (`sitramed.mevaterapia.com.ar`) no tiene API. Todo es Playwright headless
contra `Meva.Rt.Infrastructure.SitraMed`. Doc de arquitectura completa (leer primero
si vas a tocar algo no cubierto abajo): `SITRAMED_SCRAPING.md` (raíz del repo).
Historial de bugs reales encontrados en este scraper y la metodología que los destapó:
`BUG_AGENDA_EQUIPOS_Y_ESTIMADOS.md` (raíz del repo) — léelo antes de asumir que un
síntoma raro es nuevo, es muy probable que ya haya pasado.

## La regla que más bugs causó: Phoenix LiveView y el orden de los campos

SitraMed usa Phoenix LiveView en varios forms (agenda de equipos, tomógrafos). Un campo
con `phx-debounce="blur"` sincroniza su valor al servidor **solo cuando dispara blur**,
no cuando Playwright lo llena. Si seleccionás otro campo dependiente (ej. equipo/tomógrafo)
antes de ese blur, el `phx-change` de ese segundo campo viaja al servidor con el valor
viejo del primero — normalmente la fecha vuelve a "hoy" sin importar qué muestre el DOM.
**Sin error visible.** El síntoma es: la agenda de una fecha futura devuelve el roster de
hoy, repetido, día tras día.

Esto pasó una vez en tomógrafos (arreglado hace tiempo) y **volvió a pasar meses después
en agenda de equipos** porque nadie replicó el fix ahí — ver bug #4 en
`BUG_AGENDA_EQUIPOS_Y_ESTIMADOS.md`. Antes de escribir o tocar cualquier flujo Playwright
nuevo contra un form de SitraMed:

1. Capturá el HTML del form y gerpeá `phx-debounce` / `phx-change`. Si aparece en un
   campo, el orden de interacción importa.
2. Orden obligatorio cuando hay LiveView: **seteá el campo con `phx-debounce="blur"`
   primero → disparar blur explícito (`focus()` + `blur()` vía JS) → esperar
   `WaitForTimeoutAsync` + `NetworkIdle` → recién ahora tocar el campo dependiente**
   (equipo/tomógrafo) → confirmar.
3. **No confíes en un wait fijo para saber si LiveView terminó.** LiveView empuja por
   WebSocket, que `NetworkIdle` de Playwright no trackea. Guardá un snapshot del HTML
   antes de la acción final (Enter/submit) y hacé poll (ej. 300ms × hasta 3s) esperando
   que cambie de verdad.
4. **Post-condición obligatoria, no opcional:** después del round-trip completo, releé
   el valor real del campo crítico (ej. `#search_date`) y comparalo contra lo pedido. Si
   no coincide, reintentá el bloque completo una vez; si sigue sin coincidir, lanzá
   excepción. El bug de agenda de equipos vivió meses porque nada verificaba esto —
   fallaba en silencio, con datos de otro día servidos como si fueran correctos.

Un form **sin** `phx-debounce`/`phx-change` por campo (ej. `follow_up_search` — todo el
form viaja junto en un solo `phx-submit` al click del botón) no tiene este riesgo:
confirmado en vivo, no repitas la paranoia ahí, pero sí verificalo antes de asumirlo en
un form nuevo.

## Dónde vive cada cosa

| Qué scrapea | Extractor | Cliente Playwright (método) |
|---|---|---|
| Seguimiento de pacientes | `SitraMedFollowUpExtractor` | `DownloadFollowUpPagesAsync` |
| Agenda de equipos | `SitraMedAgendaExtractor` | `DownloadAgendaHtmlAsync` / `DownloadAgendaPagesForDatesAsync` |
| Agenda de tomógrafos | `SitraMedTomographExtractor` | `DownloadTomographAgendaHtmlAsync` |
| Pacientes atendidos (Derivación) | `SitraMedAttendedPatientsExtractor` | reusa `DownloadAgendaPageHtmlForMachineAsync` → `DownloadAgendaHtmlAsync` |
| GUID → HC | `SitraMedPatientHcFetcher` | navega `/medical_histories/{guid}/overview` |
| Teléfono de paciente | `SitraMedPatientPhoneFetcher` | `FetchPhonesForGuidsAsync` |
| Dosis TBI | (directo en `PlaywrightSitraMedClient`) | `FetchTbiDosesForGuidsAsync` → `/medical_histories/{guid}/planifications` |

Todos los extractores de alto nivel reciben HTML crudo + `DomRows` ya parseados por
Playwright (`FollowUpHtmlSnapshot` / `AgendaHtmlSnapshot` / `TomographAgendaHtmlSnapshot`)
y **prefieren DOM sobre regex** cuando `DomSnapshots`/`DomRows` no está vacío — el regex
es fallback de compatibilidad, no la ruta principal. Si agregás un campo nuevo, extraelo
en el parseo DOM primero.

Detalle de selectores, columnas, offsets y regex exactos: `SITRAMED_SCRAPING.md`. No los
dupliques de memoria acá, ese doc es la fuente de verdad y se actualiza con cada fix.

## Antes de tocar código: reproducí con el endpoint de test, no a mano

Comparar manualmente en browser contra lo que devuelve el scraper da falsos
positivos/negativos — reusar una pestaña entre pruebas deja estado corrupto (foco,
popups de calendario, fecha revertida). Cada prueba nueva necesita navegación fresca.

En cambio, estos endpoints corren el código real de producción, aislado, sin
concurrencia, con login fresco — son la forma confiable de confirmar un bug o un fix:

```
POST /api/scraping/test?center=...&stage=...              (seguimiento)
POST /api/scraping/test-agenda?machine=<DisplayName>&date=<yyyy-MM-dd>
POST /api/scraping/test-tomograph?tomograph=<DisplayName>&date=<yyyy-MM-dd>
POST /api/scraping/test-followup-full
```

Con `MEVA_SITRAMED_SAVE_AGENDA_HTML=true` seteado en el proceso, `test-agenda` también
guarda el HTML completo devuelto (`capturedFilePath`, bajo `data/agenda-captures/`) —
imprescindible para comparar una fila rota contra una normal byte a byte.

**Regla de higiene de estos endpoints de test:** si agregás uno nuevo, hacé que reuse
la función de descarga real (`DownloadXxxHtmlAsync`) en vez de reimplementar la
lógica de selects/blur/waits inline. Ya pasó (`RunTomographTestAsync`, ~150 líneas
duplicadas) que el endpoint de test corría un camino separado que **nunca** llamaba al
código de producción — un bug arreglado en el scraper real podía seguir roto ahí sin que
nadie lo notara.

## Iterar sin tocar el servicio de producción (Windows Service `MevaRT`)

El servicio real sirve usuarios reales; no lo reinicies para probar un cambio. Publicá
a una carpeta separada y corré en un puerto alterno reusando los datos reales en modo
lectura:

```powershell
dotnet publish Meva.Rt.Web/Meva.Rt.Web.csproj -c Release -o C:\MevaRT_test --self-contained false
$env:MEVA_SITRAMED_USER = "..."          # ya suele estar seteada en el entorno
$env:MEVA_SITRAMED_PASSWORD = "..."
$env:MEVA_DATA_DIR = "C:\MevaRT\data"    # datos reales, no los pises
$env:MEVA_SITRAMED_SAVE_AGENDA_HTML = "true"
$env:ASPNETCORE_URLS = "http://localhost:5099"
Start-Process dotnet -ArgumentList "C:\MevaRT_test\Meva.Rt.Web.dll" -WorkingDirectory "C:\MevaRT_test" -PassThru
curl -s -X POST "http://localhost:5099/api/scraping/test-agenda?machine=QUILMES%20-%20Equipo%202&date=2026-09-16"
# al terminar: matar el proceso y borrar C:\MevaRT_test
```

Para inspección visual puntual (ver qué hay realmente en una fecha/máquina, contar
columnas de una fila con `querySelectorAll('td')`, explorar cómo reacciona un widget
tipo `vanillajs-datepicker` o `flatpickr`) usá el skill `agent-browser` contra
`https://sitramed.mevaterapia.com.ar` (login en `/session/new`, credenciales en
`MEVA_SITRAMED_USER`/`MEVA_SITRAMED_PASSWORD`, ya suelen estar seteadas en el entorno).
Pero para confirmar si el **scraper** tiene un bug, preferí siempre el endpoint de test:
el browser a mano introduce condiciones de carrera propias que confunden el diagnóstico.

## Trampas ya conocidas al parsear filas (no las reintroduzcas)

- **Offsets de columna por posición son frágiles.** La agenda de equipos ancló el offset
  "saltear celdas en blanco iniciales" asumiendo que la columna `signs` siempre está
  vacía — un turno marcado urgente ("URG") la puebla y corre todo el mapeo una columna
  (nombre = hora, técnica = texto genérico). Fix real: anclar en la primera celda con
  formato `H:MM`/`HH:MM` (la celda "inicio" siempre tiene ese formato), con el
  skip-de-blancos como fallback. **Regla general: cuando el offset dependa de "primera
  celda vacía" o similar, buscá un ancla por contenido/formato en vez de por posición.**
- **Celdas con modales HTML embebidos contaminan el fallback por keywords.** La celda
  "Comunicaciones Internas" trae el HTML completo de un modal con notas históricas que
  pueden mencionar técnicas viejas del paciente — sin excluir `.modal`/`button.modal-button`,
  un VMAT actual se clasifica mal por una nota de hace meses.
- **`OrdinalIgnoreCase` no normaliza diacríticos.** `"Radiocirugía"` (í con tilde) no
  matchea `"radiocirugia"` bajo `OrdinalIgnoreCase`. Cualquier regex/keyword nuevo sobre
  texto de SitraMed necesita la variante sin tilde a mano, o normalizar con
  `Normalize()`/`NormalizationForm.FormD` primero (mismo patrón que
  `SelectFirstByLabelAsync` usa para labels de dropdown).
- **Extraer un campo de un `<div>` por longitud de string/slice es fragil.** La
  indentación real del HTML mete espacios en medio del texto. Cloná el nodo, remové el
  `<strong>` del label, y leé el resto — no asumas offsets de caracteres.
- **Filas grises/finalizadas se mezclan con las del día pedido.** Filtrá por
  `data-type` conteniendo "Finalizado" y por fecha-fin anterior a la fecha pedida (y en
  el fallback regex, por color de fondo gris — hex/rgb con `max-min < 25`).
- **Dedup por `PatientId` sin mirar técnica/zona puede borrar un tratamiento real.** Un
  paciente puede tener 2+ cursos de tratamiento concurrentes genuinos (ej. BQT +
  IMRT en sitios distintos) — dedupear a ciegas por `PatientId` quedándose con la etapa
  más avanzada descarta el estimado del curso menos avanzado. Antes de dedupear, mirá si
  `TreatmentZone`/técnica difiere entre las filas del mismo paciente — eso sugiere
  flujos reales distintos, no una entrada duplicada. Caso sin resolver documentado en
  `BUG_AGENDA_EQUIPOS_Y_ESTIMADOS.md` (sección "Bug pendiente").

## Configuración y entorno

- Credenciales: `MEVA_SITRAMED_USER` / `MEVA_SITRAMED_PASSWORD`.
- `MEVA_SITRAMED_TIMEOUT_SECONDS` (default 30) — timeout Playwright por página.
- `MEVA_SITRAMED_HEADFUL=false` por default (headless); poné `true` (nombre invertido,
  ojo) para ver el browser.
- `MEVA_SITRAMED_DIAGNOSTICS=true` guarda screenshots antes/después de cada búsqueda de
  follow-up (`data/diagnostics/`). En prod se activa vía registro
  `HKLM:\SYSTEM\CurrentControlSet\Services\MevaRT\Environment` + reinicio del servicio.
- `MEVA_SITRAMED_SAVE_AGENDA_HTML=true` guarda HTML crudo de cada `test-agenda`.
- `CanUseRemoteScraping()` retorna `false` sin usuario/password → todo lo que dependa de
  Playwright devuelve vacío en lugar de fallar (no asumas que un resultado vacío es un
  bug de parseo antes de chequear que las credenciales estén seteadas).
- Todo path de persistencia nuevo (mapas, cachés, capturas) va bajo `MEVA_DATA_DIR` —
  ver `CLAUDE.md` (raíz del repo) para el flujo completo dev/prod de esa variable. Nunca
  hardcodees `ContentRootPath` ni una carpeta nueva suelta.
- Logs van a `Console.Error` con prefijo `[SitraMed]` — no hay logger inyectado. Visibles
  en el log del Windows Service en producción.

## Checklist rápido al terminar un cambio

1. ¿Toca un form con `phx-debounce`? → confirmá orden fecha-antes-que-dependiente +
   blur explícito + poll de cambio real + post-condición de verificación (sección arriba).
2. ¿Agrega un campo nuevo? → extraelo primero en el parseo DOM (no solo en el regex
   fallback), y actualizá `SITRAMED_SCRAPING.md` con la fuente exacta (selector/offset).
3. ¿Toca un endpoint `/api/scraping/test-*`? → confirmá que sigue llamando a la función
   real de descarga, no una copia inline.
4. ¿Corriste `POST /api/scraping/test-*` contra el cambio antes de darlo por bueno? Un
   bug de este scraper casi nunca tira excepción — se manifiesta como datos de otro
   día/fila corrida, así que "no rompió nada" no es suficiente evidencia.
5. ¿El bug/fix es un patrón nuevo (no cubierto en este SKILL ni en
   `BUG_AGENDA_EQUIPOS_Y_ESTIMADOS.md`)? Documentalo ahí para la próxima persona (o
   instancia) que toque este código.
