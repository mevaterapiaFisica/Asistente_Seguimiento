# ARIA (AriaQ.dll / EF6) — esquema y aprendizajes de consultas

Consolidado de lo confirmado contra ARIA real durante el desarrollo de "Frecuencia de Imágenes"
(monitoreo de cumplimiento de placas de verificación, `Meva.Rt.AriaRunner/AriaQuery.cs`). Objetivo:
que la próxima query nueva contra ARIA no repita la misma ronda de debugging.

## Acceso/conexión

- EF6 vía `AriaQ.dll` (Varian, externo, no modificable).
- Connection string `variansystemEntities` en `App.config` (`AriaRunner.exe.config`).
- Auth: Windows impersonando `ECL-FISICA2\varian` con `LogonUser` P/Invoke
  (`LOGON32_LOGON_NEW_CREDENTIALS` / `LOGON32_PROVIDER_WINNT50`). Password en
  `ARIA_VARIAN_PASSWORD`.
- **Sin ESAPI/Eclipse en ningún punto** — todo SQL/EF6, igual que el resto de Meva.Rt.
- SQL Server tiene límite de 2100 parámetros — consultas con `IN (...)` grandes (ej. miles de
  `PatientId`) van batcheadas (`QueryInBatches<TKey,TResult>` en `AriaQuery.cs`).

## Entidades clave y campos confirmados

| Entidad | Campos relevantes |
|---|---|
| `PlanSetup` | `Status` (`"Unapproved"`/`"PlanApproval"`/`"TreatApproval"`), `Course` nav |
| `Course` | `CourseId` (string), `CourseSer`, `PatientSer` |
| `RTPlan` | `RTPlanSer`, `PlanSetupSer`, `NoFractions`, `CreationDate` |
| `TreatmentRecord` | `RTPlanSer`, `PatientSer`, `TreatmentRecordDateTime`, `RadiationDevice`→`Machine` |
| `RadiationHstry` | `TreatmentRecordSer`, `TreatmentDeliveryType` (`"TREATMENT"` / `"OPEN_PORTFILM"` / `"TRMT_PORTFILM"`), `FractionNumber` (int, no nullable), `TreatmentStartTime` |
| `Image` | `PatientSer` (nullable long), `ImageType` (`"ImagePI"` / `"ImageCT"` / `"Image"` / `"ImageDRR"`), `FractionNumber` (nullable int), `CreationDate`, `SeriesSer` |
| `Series` | `SeriesModality` (`"CT"` / `"RTIMAGE"`), `AcquisitionType` (`"Import"` / `"PredictedPortal"` / `"Planning"`), `CreationDate`, `RelatedCourseSer`/`RelatedPlanSetupSer`/`RelatedRadiationSer`, `Machine` nav |

## Gotchas confirmados contra datos reales

Esta es la parte que costó varias rondas de falsos positivos/negativos en producción (casos
reales usados como referencia: Garay, Ghiggeri, Mazza, Do Santos, Qiu — solo por apellido, sin
otros datos del paciente).

- **`SessionProcedure` NO sirve para detectar imágenes reales.** Se probó como fuente para
  kV/CBCT: `SessionProcedureTemplateId=="kV"` con `Status=="COMPLETE"` se marca en TODAS las
  sesiones (15/15 en un paciente de prueba), siempre apuntando al mismo `SeriesSer` de
  **planificación** (`Series.Modality=="PLAN"`, 0 imágenes reales) — no indica si se tomó una
  placa ese día. Usar `Image`/`Series` en su lugar.

- **CBCT no tiene `ImageType` propio** — se identifica por `Series.SeriesModality=="CT"`.
  Su `Image.CreationDate` es un **timestamp de importación masiva**: cientos de imágenes (~80-150
  por escaneo) comparten el mismo instante exacto, no es la fecha real de adquisición. La fecha
  real está en `Series.CreationDate` (confirmado: un valor distinto por día real de tratamiento).

- **`ImageType=="ImagePI"`** (Portal Image = EPID, siempre MV; el kV del OBI NO viene como ImagePI) sí tiene `Image.CreationDate` confiable —
  confirmado contra el gráfico de tratamiento de ARIA de varios pacientes, coincide exacto.

- **`Series.AcquisitionType=="PredictedPortal"`** = imagen sintética de predicción de dosis de
  portal, no es una placa real adquirida — excluir siempre (caso real: Garay, un falso positivo
  venía de acá).

- **Cursos de QA de portal dosimetry** (física, no posicionamiento de paciente) siempre tienen
  `"QA"` en el `CourseId` (ej. `"C0_Sep26_QA"`, `"C0_Ago26_QA"`). Suelen tener `NoFractions=1` y
  sus imágenes reportan `FractionNumber=1` — por casualidad puede coincidir en fecha+máquina+
  fracción con un día real de tratamiento, dando un falso positivo. `Series.RelatedCourseSer` /
  `RelatedPlanSetupSer` están **siempre null** en esta instalación → no hay link directo
  Image→Course, el filtrado tiene que ser indirecto vía TreatmentRecord/RTPlan.

- **Un mismo Course puede tener varios RTPlans con el mismo `NoFractions`** por
  re-optimizaciones/replanes a mitad de curso (caso real: Do Santos, 5 RTPlans, todos
  `NoFractions=30`, bajo un mismo curso). Escopar TreatmentRecords a nivel de curso completo
  (para cubrir replanes legítimos) termina trayendo TreatmentRecords de un RTPlan viejo/
  reemplazado. Escopar al **RTPlan seleccionado** (`CreationDate` más reciente) es más seguro.

- **Ni fecha+máquina+fracción alcanza siempre.** Una sesión de QA de la mañana puede caer el
  mismo día calendario que el tratamiento real de la tarde, coincidiendo en fecha+máquina+
  fracción (caso real: Do Santos, imagen 9:07-9:10 AM vs. TreatmentRecord real 3:58 PM el mismo
  día — 7hs de diferencia). Hace falta además una verificación de **cercanía horaria (±4h)**
  entre el timestamp de la imagen y el del `TreatmentRecord`.

- **`Image`/`Series` no tienen máquina propia confiable** — `Series.Machine` sale `null` incluso
  en imágenes reales. Se resuelve cruzando la fecha de la imagen contra `TreatmentRecord` del
  mismo día en la máquina planificada del plan (`TreatmentRecord.RadiationDevice.Machine`) — si
  no hubo tratamiento ahí ese día, la imagen no cuenta. Esto además resuelve gratis la regla de
  "no cuenta si se tomó en un equipo sustituto".

- **`PlanSetup.Status=="TreatApproval"` NO significa "en tratamiento activo hoy".** Ese status
  queda para siempre en ARIA, no se resetea al terminar el tratamiento (probado: trajo pacientes
  con status TreatApproval desde 2016). Para detectar "en tratamiento" usar actividad reciente de
  `TreatmentRecord` (ej. algún registro en los últimos N días), no el status del plan.

- **Combinar varios `.Include()` de colecciones independientes en una sola query EF6 genera un
  join cartesiano** (ej. `TreatmentRecords.RadiationHstries` + `SessionRTPlans.Session.
  SessionProcedures` juntos) — con miles de planes esto causa timeout SQL. Separar en queries
  batcheadas por clave (`RTPlanSer`/`PatientSer`, patrón `QueryInBatches` ya existente en
  `AriaQuery.cs`) y unir en memoria con `ToLookup`/`Dictionary`.

- **ExacTrac es un sistema externo sin ningún registro en ARIA** — no hay campo ni tabla que lo
  marque. La exclusión de estos pacientes (y de electrones sin IGRT) requiere una lista
  mantenida a mano en Meva.Rt (`ImagingMonitoringExclusionStore`, patrón
  `PedidoStore`/`QaEspecificoStore`, archivo bajo `MEVA_DATA_DIR`).

## Dónde se aplica hoy

`Meva.Rt.AriaRunner/AriaQuery.cs` — `GetImagingFrequencyReport`, `FindLastValidImage`,
`GetTreatmentProgress` implementan todo lo anterior y son el caso de uso real que validó estos
hallazgos contra ARIA en producción.

## Corrimientos del matcheo online (Vrt/Lng/Lat)

Modo `couchShifts` de AriaRunner (`{"couchShifts":true,"patientIds":[...]}` → `couch_shifts_*.json`,
`AriaQuery.GetCouchShifts`). Validado contra Offline Review (7 sets de un paciente de prueba, error < 0.05 cm).

- `MatchResult`/`ImageMatchResult` están **vacías** en toda la base — no usar.
- Fuente: `SpatialRegistration` `RegSubType=="Online2DPaired"` (imágenes = ImageDRR; `Transformation` = 12
  doubles: R 3x3 + t x,y,z en mm) emparejada con la `RegTypeCodeMeaning=="Frame of Reference Identity"`
  guardada a la vez (mismo `StatusDate`, ±ms) cuyas imágenes son las ImagePI. No hay FK directa.
- Hay varias versiones por set de placas (re-matcheos): se exporta la última.
- Iso por imagen: `Image.ImageSlice → Slouse.SliceRT.IsoCenterPositionX/Y/Z` (= camilla al adquirir:
  `(-Lat,-Vrt,-Lng)*10`); `Slouse.CouchVrt/Lng/Lat` = camilla cruda. Iso del DRR = mismo campo en la ImageDRR.
- `s = t - isoImagen + isoDRR` (mm); `Vrt=-s.y/10, Lng=-s.z/10, Lat=-s.x/10` (cm).
- `applied`: la camilla cambió entre el set y el siguiente set del día / el tratamiento
  (`ExternalFieldCommonHstry.CouchVrt/Lng/Lat`; `CouchCorrection*` vienen NULL).
- Rtn no exportado (matriz trae ±0.1° pero la pantalla mostró 0.0, sin validar).
- Plan del set: `SliceRT.PlanSetupSer` e `Image`→plan vienen **vacíos**; el vínculo real es DRR → `Radiation.RefImageSer`
  (campos del plan) → `Radiation.PlanSetupSer` → `PlanSetup` (`PlanSetupId`, `Course.CourseId`). Exportado como `courseId`/`planId`.
