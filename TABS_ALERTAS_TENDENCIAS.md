# Meva.Rt — Tabs Alertas y Tendencias

> Doc funcional resumido, grupo nav **Análisis** (Tendencias) y **Pacientes** (Alertas). Fuente: PROYECTO.md.

## Tab Alertas

Grupo: Pacientes. Vista de problemas activos, cards + banners.

| Bloque | Qué muestra |
|---|---|
| A1 | Centros con etapas demoradas: grid tarjetas por centro, días reales vs. referencia |
| A2 | Tiempo estimado de planificación, dentro de cada tarjeta de centro, con `weeklyStats` por centro |
| B1 | Agenda de equipos agrupada por centro |
| B3 | Turnos superpuestos: cuenta pares de superposición (no equipos); ignora superposición si ambos turnos son mismo paciente |
| C2/C3 | Eventos recientes TechniqueChanged / StageRegressed (este último muestra `displayName` de etapa, no código) |
| Banner fin de año | Si quedan ≤10 días para 31/12 y `feriados.txt` no tiene año siguiente |

Datos: snapshot dashboard (`GET /api/home`) + `GET /api/patient-events` + `GET /api/alerts/feriados`.

## Tab Tendencias (ex-Resumen)

Grupo: Análisis. Estadísticas semanales de flujo de pacientes (`weekly_stats.json`).

- Requiere **≥4 semanas** de datos reales acumulados para mostrar stats calculadas.
- Antes de eso: valores de referencia (`expectedDays` por etapa) + leyenda explicativa de por qué son estimados.
- Mismos datos alimentan A2 (Alertas) y estimación de fecha de inicio en tab Física.

## Notas cruzadas

- `IsLongWait` (>40 días en etapa) excluido de promedios y transiciones semanales en ambos tabs.
- `PatientProcessEvents`: solo detecta TechniqueChanged y StageRegressed; desaparición de paciente = inició tratamiento (no se marca como suspensión).
- Auto-refresh cada 3 min (`GET /api/status`) recarga la página si cambia `appVersion` o `generatedAtUtc`, ambos tabs se refrescan.
