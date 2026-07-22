# Meva.Rt — flujo de archivos de datos (dev vs prod)

Todo dato persistente (pedidos.json, qa_especifico.json, agenda_*.json, dashboard_bootstrap.json,
rt_configuration.json, guid_hc_map.json, aria_plans_mock.json, aria_results_*.json, etc.) vive bajo
**una sola variable**: `MEVA_DATA_DIR`. Sin ella, cae a `ContentRootPath/data` (`Program.cs:16-17`).

## Quién setea `MEVA_DATA_DIR`

| Entorno | Mecanismo | Valor |
|---|---|---|
| Dev (`dotnet run` / VS) | `Meva.Rt.Web/Properties/launchSettings.json` | `C:\MevaRT\data` |
| Prod (servicio Windows `MevaRT`) | Registro `HKLM:\SYSTEM\CurrentControlSet\Services\MevaRT\Environment`, seteado por `scripts/install-service.ps1` | `C:\MevaRT\data` |
| `scripts/refresh.bat` (Task Scheduler, 2x/día) | Hardcodeado (`set DATA_DIR=C:\MevaRT\data`), independiente del registro | `C:\MevaRT\data` |

**Coinciden hoy los 3, pero por sitios distintos** — no hay una única fuente de verdad. Si algún día
se cambia `-PublishPath` en `install-service.ps1`, hay que actualizar `refresh.bat` a mano también.

`launchSettings.json` **solo** aplica a `dotnet run`/debug — un exe publicado corrido a mano
(sin servicio, sin esas env vars) cae al fallback `ContentRootPath/data`, que hoy coincide con
`C:\MevaRT\data` solo porque `PublishPath` de `install-service.ps1` es `C:\MevaRT` (mismo árbol).

## Deploy real

- `install-service.ps1`: publica Web+AriaRunner a `C:\MevaRT`, crea el servicio (`sc.exe create`),
  y ahí sí escribe `MEVA_DATA_DIR`/credenciales SitraMed/etc en el registro del servicio.
- `publish.ps1`: redeploy del día a día (para/republica/reinicia el servicio). **No toca env vars**.
- `Meva.Rt.Web.csproj`: excluye la carpeta `data\` local del publish a propósito (comentario
  `ponytail:` en el csproj) — evita pisar `C:\MevaRT\data` real con fixtures de dev.

## Inconsistencia conocida (no rompe nada hoy)

AriaRunner invocado a mano (sin `--output-dir`) escribe en su propia carpeta de exe, no en
`MEVA_DATA_DIR` — el import del Web app no vería nada, sin error visible. Invocado desde el Web
app (`/api/aria/run-query`) o desde `refresh.bat` siempre pasa `--output-dir` explícito, así que
el camino automático está cubierto.

(Ya corregido: `AgendaHtmlCaptureDirectory`/`DiagnosticsDirectory` ahora usan `snapshotsDirectory`
en vez de `ContentRootPath` — `Program.cs:28,30`.)

## Regla al tocar código de paths

Cualquier archivo nuevo de persistencia va bajo `snapshotsDirectory`/`MEVA_DATA_DIR` (mismo patrón
que `PedidoStore`/`QaEspecificoStore` en `Meva.Rt.Infrastructure.Storage`) — nunca hardcodear
`ContentRootPath` ni una ruta nueva suelta.
