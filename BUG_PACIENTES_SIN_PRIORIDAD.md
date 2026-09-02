# Bug: pacientes sin prioridad desaparecían del dashboard

**Fecha:** 2026-07-23
**Estado:** fix aplicado en `PlaywrightSitraMedClient.cs`, pendiente de verificar en próximo refresh real.

## El problema

`ParseFollowUpRowsAsync` (`Meva.Rt.Infrastructure.SitraMed/PlaywrightSitraMedClient.cs:1659`) leía la
primera celda de cada fila del seguimiento SitraMed como prioridad y **descartaba la fila entera** si no
parseaba a `int`:

```csharp
var priority = (await cells.Nth(0).InnerTextAsync()).Trim();
if (!int.TryParse(priority, out _)) continue;   // <- bug
```

SitraMed renderiza `-` en esa celda cuando el paciente no tiene prioridad asignada (P1/P2/P3). Resultado:
el paciente no entraba nunca al snapshot (`dashboard_bootstrap.json`) — invisible en Seguimiento, Buscar,
Alertas, Física, Técnicas Especiales, todo. No era un problema de UI ni de filtro, sino de scraping: el
paciente simplemente nunca llegaba a los datos.

Caso detectado: paciente `1-119286-0` (CETRO, etapa F6F/QA) — fila real en SitraMed confirmada vía HTML
exportado, primera celda `-`.

**Fix:** parsear prioridad como nullable, sin descartar la fila:

```csharp
var priority = (await cells.Nth(0).InnerTextAsync()).Trim();
var hasPriority = int.TryParse(priority, out var priorityValue);
...
Priority = hasPriority ? priorityValue : (int?)null,
```

## Impacto en Tendencias (y en A2/Física, que comparten los mismos datos)

`weekly_stats.json` y `StageSummary` se calculan sobre `ProcessPatientSnapshot` — cualquier paciente
ausente del snapshot no cuenta en nada de esto. Consecuencias probables, ya presentes en los datos
históricos (el bug lleva quién sabe cuánto tiempo activo):

1. **Subconteo sistemático de etapas.** `StageSummary` (conteo, promedio de días, demorados, long-wait
   por etapa/centro) excluye a todo paciente sin prioridad en el momento del scrape. Sesgo, no solo ruido:
   los promedios reflejan solo pacientes que ya tienen P1/P2/P3 asignado.

2. **Transiciones semanales rotas para estos pacientes.** La detección de transición
   (`Contracts.cs:394`, `BootstrapService`) exige que el paciente exista en el snapshot **anterior**:
   ```csharp
   if (!previousByPatient.TryGetValue(current.PatientId, out var previous)) continue;
   ```
   Si el paciente entró a una etapa sin prioridad asignada y la consiguió recién unos días después, el
   tramo transitado "invisible" nunca genera `StageTransitionEvent` → `weekly_stats.json` subcuenta
   duración real en esa etapa. Esto sesga las medias hacia abajo (subestima tiempos reales), afectando:
   - Tab Tendencias directamente.
   - A2 (Alertas) — usa los mismos `weeklyStats`.
   - Estimación de fecha de inicio en tab Física.

3. **Eventos de proceso también rotos para estos pacientes.** Mismo guard se usa para
   `TechniqueChanged`/`StageRegressed` (`Contracts.cs:436,457`) — si el paciente falta en el snapshot
   previo, cualquier cambio de técnica o retroceso de etapa durante ese hueco no se detecta.

4. **Corrupción histórica ya acumulada.** `weekly_stats.json` es append-only y nunca se recalcula desde
   cero — los datos ya guardados (necesarios para destrabar Tendencias a las 4 semanas) están sesgados
   bajo por este bug. El fix corrige el flujo hacia adelante, pero no reescribe lo ya acumulado.

## Preguntas abiertas / a verificar

- ¿Qué tan frecuente es que un paciente quede sin prioridad asignada por varios días? Si es raro, el sesgo
  es menor; si es común (ej. todos los pacientes nuevos arrancan sin prioridad hasta que un médico la
  asigna), el sesgo en Tendencias puede ser significativo.
- Si el sesgo resulta relevante, evaluar si vale la pena resetear `weekly_stats.json` /
  `stage_transitions.json` para que las próximas 4 semanas post-fix generen una base limpia (perderíamos
  histórico acumulado, a cambio de no arrastrar el sesgo).
