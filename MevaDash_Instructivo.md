# MevaDash — Instructivo de uso
**Mevaterapia · Sistema de seguimiento y gestión de radioterapia**
*Versión: Junio 2026*

---

## 1. ¿Qué es MevaDash?

MevaDash es el sistema centralizado de seguimiento de pacientes y gestión operativa de los centros de radioterapia de Mevaterapia. Integra en tiempo real la información de dos plataformas separadas:

- **SitraMed** — sistema de gestión clínica: seguimiento de pacientes por etapas, agenda de equipos y tomógrafos.
- **ARIA (Varian)** — sistema de planificación de radioterapia: planes de tratamiento, máquina asignada, técnica de irradiación y número de fracciones.

MevaDash permite ver en un solo lugar el estado del proceso oncológico de cada paciente, la ocupación de los equipos, la carga de trabajo del equipo de física y los indicadores generales de cada centro.

**MevaDash no modifica información en SitraMed ni en ARIA.** Toda acción operativa (cambiar etapa de un paciente, confirmar un turno, aprobar un plan) se realiza directamente en esos sistemas. MevaDash es una herramienta de visualización, consulta y organización interna.

---

## 2. ¿De dónde viene la información?

| Fuente | Qué aporta | Frecuencia de actualización |
|---|---|---|
| SitraMed | Etapa de cada paciente, días en etapa, agenda de equipos y tomógrafos, prioridad, médico responsable, físico asignado, técnica de tratamiento | Actualización manual o programada (botón Actualizar o Task Scheduler) |
| ARIA | Máquina planificada, técnica de irradiación (VMAT/IMRT/3DC), energía del haz, número de fracciones | Actualización batch via AriaRunner (requiere acceso a la red ARIA) |

Los datos se combinan automáticamente: MevaDash cruza la información de ambas fuentes por número de historia clínica (HC) para construir la vista unificada de cada paciente.

**Centros con ARIA:** MEVA-Central, CETRO, QUILMES, SAN JUSTO, RT MEDRANO.
**Sin ARIA:** MEVA-Viamonte (los pacientes de este centro aparecen sin datos de plan).

---

## 3. Navegación general

La aplicación se organiza en cuatro grupos de pestañas:

| Grupo | Pestañas incluidas |
|---|---|
| **Pacientes** | Seguimiento · Técnicas Especiales · Física · Buscar |
| **Agendas** | Equipos · Tomógrafos · Inicios · Derivación |
| **Análisis** | Alertas · Tendencias |
| **Admin** | Configuración *(requiere contraseña SysAdmin)* |

Al abrir la app se muestra **Seguimiento** por defecto.

---

## 4. Secciones por perfil de uso

---

### 4.1 Seguimiento de proceso
*Para: personal de seguimiento, coordinadores, jefes de centro*

#### Pestaña Seguimiento

Vista principal del estado de todos los pacientes activos en proceso. Muestra cuántos pacientes hay en cada etapa de cada centro y cuántos días llevan en promedio.

**Cómo usarla:**
- Seleccionar un **centro** en los filtros superiores para enfocar la vista.
- Seleccionar un **grupo de etapa** para ver solo las etapas relevantes (ej: "Planificación" muestra F6A y F6B).
- Hacer click en una etapa de un centro → ver la lista detallada de pacientes en esa etapa con días, técnica y físico asignado.
- Los pacientes con el punto rojo (●) tienen demoras respecto al tiempo esperado.
- Los pacientes en **gris** son "larga espera" (más de 40 días en proceso, configurable): se excluyen de los promedios para no distorsionar los indicadores.
- Los pacientes **P1** aparecen resaltados en rojo — son prioridad oncológica alta.

#### Pestaña Alertas *(sección Centros y Pacientes)*

Muestra automáticamente situaciones que requieren atención:
- Etapas con promedio actual más del doble del tiempo esperado.
- Pacientes P1 con más de 5 días en planificación (configurable).
- Pacientes con demora acumulada superior al doble de lo esperado.

#### Pestaña Tendencias

Evolución histórica de los tiempos promedio por etapa y por centro. Útil para detectar si un cuello de botella está mejorando o empeorando semana a semana.

> **Nota:** las estadísticas históricas se acumulan desde la puesta en marcha del sistema. Durante las primeras semanas los gráficos pueden tener pocos datos.

---

### 4.2 Oficina Técnica
*Para: personal de oficina técnica, coordinadores de agenda*

#### Pestaña Equipos (Agendas)

Muestra la agenda de los equipos de irradiación para la fecha seleccionada. Cada equipo muestra:
- Turnos reales scrapeados de SitraMed (con nombre, hora y técnica de tratamiento).
- Turnos estimados (en rosa) — pacientes próximos a iniciar calculados por el sistema.
- Turnos reservados (en violeta) — reservas registradas en MevaDash.
- Capacidad libre del día (verde/amarillo/rojo según ocupación).

#### Pestaña Inicios (Agendas)

Lista de pacientes que **inician tratamiento** en los próximos 3 días hábiles. Un paciente aparece aquí cuando su nombre aparece en la agenda de un equipo por primera vez (no estaba en los 2 días hábiles anteriores).

Útil para preparar la logística del inicio: verificar que el paciente tiene todo listo antes de su primer turno.

#### Pestaña Derivación (Agendas)

Herramienta para organizar la redistribución de pacientes cuando un equipo queda fuera de servicio.

**Cómo usarla:**
1. Seleccionar el equipo fuera de servicio y el rango de fechas.
2. Presionar **Calcular derivación** — el sistema scrapea la agenda del equipo para detectar qué pacientes ya fueron atendidos ese día (marcados automáticamente como "Ya atendido").
3. Para cada paciente afectado, ver los equipos compatibles según la técnica de tratamiento. Los equipos incompatibles aparecen tachados con el motivo.
4. Asignar cada paciente a un equipo destino haciendo click en el botón correspondiente, o marcarlo como **Suspendido** si no se puede atender.
5. Exportar el plan como HTML para compartir con el equipo.

> La derivación es solo organizativa. Los cambios reales se realizan en SitraMed.

#### Pestaña Buscar (Pacientes)

Ver sección 4.4.

#### Pestaña Técnicas Especiales (Pacientes)

Ver sección detallada en 4.3. Útil para oficina técnica para identificar pacientes SBRT/RC y su turno reservado.

#### Reserva de turnos

Desde la pestaña **Buscar**, al seleccionar un paciente, aparece el botón **Reservar Turno**. Permite registrar una fecha y hora tentativa de inicio en un equipo, antes de que el turno esté confirmado en SitraMed.

Requiere usuario y contraseña de Oficina Técnica. La reserva se elimina automáticamente 2 días hábiles después de la fecha registrada.

---

### 4.3 Física Médica
*Para: físicos médicos, jefe de física*

#### Pestaña Física (Pacientes)

Vista dedicada a las tareas de física. Muestra:
- **Tareas de Física**: pacientes en etapas F6A a F7C agrupadas por tipo de tarea (Asignación, QA, Aprobación, etc.) con cantidad y promedio de días.
- **Físicos asignados**: carga de trabajo por nombre de físico.
- **Recomendación de equipo** (tercera columna, al seleccionar un centro): para cada técnica habilitada en el centro, muestra los dos equipos con más disponibilidad en la fecha estimada de inicio. Al hacer click en un paciente de la lista, filtra automáticamente las tarjetas según su técnica.

La fecha estimada de inicio se calcula sumando los días promedio de todas las etapas desde F6B hasta F11, usando estadísticas reales cuando hay 4 o más semanas acumuladas, y valores de referencia mientras tanto.

#### Pestaña Técnicas Especiales (Pacientes)

Tabla dedicada a pacientes con tratamientos **SBRT** y **RC** en seguimiento activo. Muestra:
- HC, nombre, técnica, fecha de tomosimulación, etapa actual, días en etapa, médico responsable, físico asignado.
- Si el paciente tiene turno reservado: fecha y hora resaltadas en la columna correspondiente.

Permite filtrar por técnica (SBRT / RC) y por etapa.

#### Pestaña Alertas *(sección Equipos)*

Muestra problemas en la agenda de equipos:
- Equipos con más turnos que su capacidad declarada.
- Turnos con duración incorrecta para la técnica del paciente (ej: un turno SBRT de 10 minutos cuando debería ser 30).
- Turnos superpuestos.
- Equipos sin datos de agenda recientes.

#### Pestaña Inicios (Agendas)

Útil para anticipar el inicio de pacientes nuevos y verificar que los planes estén listos a tiempo.

#### Pestaña Tendencias (Análisis)

Evolución de tiempos promedio por etapa. Permite detectar si el flujo de física está más lento que semanas anteriores.

#### Configuración *(Admin — requiere contraseña SysAdmin)*

Permite ajustar:
- Días de referencia por etapa.
- Capacidad de equipos y tomógrafos.
- Capacidades técnicas de cada equipo (VMAT, SBRT, RC, electrones, alta energía, etc.).
- Tiempos mínimos de turno por técnica.
- Umbral de larga espera y umbral de alerta P1.

---

### 4.4 Buscar paciente
*Para: cualquier perfil — especialmente para consulta puntual sin acceso a SitraMed ni ARIA*

#### Pestaña Buscar (Pacientes)

Permite buscar un paciente por nombre o HC y ver su ficha completa. Útil para responder preguntas puntuales sin necesidad de acceder a los sistemas clínicos.

La ficha muestra:
- Nombre, HC y prioridad (P1/P2/P3).
- Centro y etapa actual.
- Días en la etapa y demora acumulada respecto a lo esperado ("Dentro de lo esperado" / "N días sobre lo esperado").
- Técnica de tratamiento y equipo planificado (o "Sin asignar" si todavía no tiene plan en ARIA).
- Físico y médico responsable.
- Número de fracciones del tratamiento.
- Fecha estimada de inicio en equipo.
- Primer día con turno disponible en el equipo planificado.
- Si el paciente ya inició tratamiento: fecha y horario de su próximo turno en agenda.
- Eventos recientes: cambios de técnica o retrocesos de etapa detectados en los últimos 90 días.

> Esta sección no requiere conocimiento técnico de física ni acceso a ARIA.

---

### 4.5 SysAdmin
*Para: físico médico responsable del sistema o administrador técnico*

#### Acceso y contraseñas

El acceso a **Configuración** y al botón de **Actualización manual** requiere contraseña SysAdmin. La contraseña se configura como variable de entorno del sistema Windows (ver `docs/CONTRASEÑAS.txt` para el procedimiento completo).

La contraseña de **Oficina Técnica** (para reservas de turno) se configura de forma separada con el mismo procedimiento.

#### Actualización de datos

El sistema se actualiza automáticamente mediante el Task Scheduler de Windows. También se puede actualizar manualmente desde el menú de timestamp (esquina superior derecha):
- **Actualizar SitraMed**: scrapea seguimiento y agenda. Demora 2-5 minutos.
- **Actualizar ARIA**: ejecuta AriaRunner y aplica los planes al snapshot actual.
- **Actualizar todo**: ambas operaciones en secuencia.

#### Flujo de actualización ARIA

ARIA corre en una red separada. El flujo es:
1. MevaDash extrae la lista de HCs de pacientes en planificación.
2. AriaRunner.exe (en `C:\MevaRT\AriaRunner\`) consulta la base de datos ARIA usando impersonación de usuario `ECL-FISICA2\varian`.
3. El resultado se importa automáticamente al dashboard.

Si la contraseña del usuario ARIA cambia, hay que actualizar la variable de entorno `ARIA_VARIAN_PASSWORD` y reiniciar el servicio.

#### Cómo se calculan las fechas estimadas de inicio

La fecha estimada de inicio en equipo se calcula sumando los días promedio de todas las etapas desde la etapa actual del paciente hasta F11 (Turno Equipo), en días hábiles (excluyendo sábados, domingos y feriados del archivo `data/feriados.txt`).

**Fuente de tiempos:** el sistema usa automáticamente las estadísticas históricas reales cuando hay al menos 4 semanas de datos acumulados en `data/weekly_stats.json`. Antes de ese umbral, usa los días de referencia configurados. La leyenda al pie de cada herramienta indica qué fuente está usando.

#### Histórico de feriados

Agregar los feriados del año siguiente antes de fin de año en `data/feriados.txt`, un feriado por línea en formato `YYYY-MM-DD`. El sistema genera una alerta cuando detecta que el archivo no tiene fechas del año próximo.

#### Estructura de archivos clave

| Archivo | Contenido |
|---|---|
| `data/dashboard_bootstrap.json` | Snapshot completo del estado actual |
| `data/aria_plans_mock.json` | Planes ARIA importados |
| `data/rt_configuration.json` | Configuración guardada desde la UI (sobrescribe defaults) |
| `data/weekly_stats.json` | Estadísticas semanales acumuladas por etapa |
| `data/stage_transitions.json` | Transiciones de etapa detectadas (retención 90 días) |
| `data/patient_process_events.json` | Cambios de técnica y retrocesos de etapa |
| `data/turn_reservations.json` | Reservas de turno activas |
| `data/feriados.txt` | Feriados en formato YYYY-MM-DD |

#### Etiquetas de técnica de tratamiento

Las etiquetas combinan información de SitraMed (técnica clínica) y ARIA (modalidad e irradiación):

| Etiqueta | Técnica clínica | Modalidad ARIA |
|---|---|---|
| VMAT | IMRT o 3DC en SitraMed | ARC en ARIA |
| IMRT - estático | IMRT en SitraMed | STATIC >40 puntos de control |
| 3DC - 6X | 3DC en SitraMed | STATIC ≤40 PC, energía <7 MV |
| 3DC 10X / 15X / 18X | 3DC en SitraMed | Energía ≥10 MV |
| 3DC e- | 3DC en SitraMed | RadiationType = Electrones |
| SBRT - VMAT | SBRT en SitraMed | ARC en ARIA |
| SBRT - haz SRS | SBRT en SitraMed | Técnica SRS/STEREO en ARIA |
| RC - VMAT | RC en SitraMed | ARC en ARIA |
| TBI / TSET | TBI / Baño de Electrones en SitraMed | — |

Si el paciente todavía no tiene plan en ARIA, la etiqueta muestra solo la técnica de SitraMed (ej: "IMRT" sin especificar si es estático o VMAT).

#### Búsqueda de pacientes en ARIA con reingreso

Las HCs en SitraMed tienen formato `1-XXXXXX-N` donde N es el número de reingreso. En ARIA los reingresos se manejan como cursos del mismo paciente. Si no se encuentra un paciente con sufijo -N, el sistema busca automáticamente en -N-1, -N-2, hasta -0, tomando el plan activo con fecha de creación de no más de 1 mes.

---

## 5. Referencia rápida por perfil

| Perfil | Pestañas principales | Acceso especial |
|---|---|---|
| Seguimiento de proceso | Seguimiento, Alertas, Tendencias, Agendas | — |
| Oficina Técnica | Equipos, Inicios, Derivación, Buscar, Técnicas Especiales | Contraseña OT para reservas |
| Física Médica | Física, Técnicas Especiales, Alertas, Inicios, Tendencias | Contraseña SysAdmin para Configuración |
| Buscar paciente | Buscar | — |
| SysAdmin | Todas | Contraseña SysAdmin |

---

*MevaDash — Mevaterapia · Sistema interno · No distribuir externamente*
