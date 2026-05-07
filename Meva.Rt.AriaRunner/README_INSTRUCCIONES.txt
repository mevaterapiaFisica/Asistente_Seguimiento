========================================================
  Meva.Rt AriaRunner - Consulta de pacientes en ARIA
========================================================

REQUISITOS EN LA PC CON ARIA:
  - .NET 9 Runtime  (https://dotnet.microsoft.com/download/dotnet/9.0)
    O publicar como "self-contained" (ver Paso 0 abajo)
  - Acceso de red/local a la base de datos SQL Server de ARIA
  - Los archivos de esta carpeta copiados completos

========================================================
  PASO 0 (OPCIONAL): Publicar como self-contained
========================================================

Si la PC con ARIA NO tiene .NET 9 instalado, desde ESTA PC
(donde tenés el código fuente) ejecutar:

  cd C:\Pablo\WebScrapSitra\Meva.Rt\Meva.Rt.AriaRunner
  dotnet publish -r win-x64 -p:SelfContained=true -c Release -o .\publish

Esto genera una carpeta "publish\" con todo incluido.
Copiar esa carpeta completa a la PC con ARIA.

========================================================
  PASO 1: Preparar el archivo de entrada (input_patients.json)
========================================================

Creá un archivo "input_patients.json" en la misma carpeta
que AriaRunner.exe, con este formato:

  {
    "patientIds": [
      "12-345678-9",
      "23-456789-0",
      "34-567890-1"
    ]
  }

Los IDs son los PatientId de ARIA.

CÓMO OBTENER LOS IDs:
  En la aplicación Meva.Rt.Web (que tenés en tu PC), hay un
  endpoint GET /api/home que devuelve los pacientes con su
  "patientId". Podés usarlo para armar la lista.

========================================================
  PASO 2: Configurar la conexión a ARIA
========================================================

OPCIÓN A - Variable de entorno (recomendada, más segura):
  Abrí cmd.exe y ejecutá ANTES de correr el programa:

  set ARIA_CONNECTION_STRING=Data Source=SERVIDOR\INSTANCIA;Initial Catalog=AriaDW;Integrated Security=True;

  Reemplazá:
    SERVIDOR\INSTANCIA → nombre del servidor SQL (preguntar a sistemas)
    AriaDW             → nombre de la base de datos ARIA (puede variar)

  Si usás usuario/contraseña SQL (no Windows Integrated):
  set ARIA_CONNECTION_STRING=Data Source=SERVIDOR;Initial Catalog=AriaDW;User ID=usuario;Password=contraseña;

OPCIÓN B - Argumento:
  AriaRunner.exe --conn="Data Source=SERVIDOR;Initial Catalog=AriaDW;Integrated Security=True;"

OPCIÓN C - Interactivo:
  Si no se configuró ninguna de las opciones anteriores,
  el programa pedirá la cadena de conexión al arrancar.

Información típica de ARIA:
  - Servidor: preguntar al equipo de sistemas/ARIA admin
  - Base de datos: generalmente "AriaDW"
  - Autenticación: usualmente Windows (Integrated Security=True)
    → La cuenta Windows usada debe tener acceso a la BD de ARIA

========================================================
  PASO 3: Ejecutar
========================================================

Abrí una ventana de comandos (cmd) en la carpeta del programa:

  AriaRunner.exe

O con argumentos:
  AriaRunner.exe --input="C:\ruta\pacientes.json" --conn="..."

Si publicaste como self-contained (Paso 0):
  publish\AriaRunner.exe

========================================================
  RESULTADOS (se crean en la misma carpeta)
========================================================

aria_results_YYYYMMDD_HHMMSS.json
  - Resultados estructurados por paciente:
    * Nombre y apellido
    * Fecha de nacimiento / sexo
    * Oncólogo principal
    * Plan activo: ID, estado, equipo, fracciones, sitio
    * Todos los planes no-rechazados

aria_runner_YYYYMMDD_HHMMSS.log
  - Log completo con timestamps de toda la ejecución.
  - IMPORTANTE: Compartir este archivo ante cualquier error.

========================================================
  SOLUCIÓN DE PROBLEMAS
========================================================

ERROR de conexión ("SqlException", "Unable to connect..."):
  → Verificar nombre del servidor y base de datos
  → Verificar que la cuenta Windows tenga acceso
  → Verificar que SQL Server esté corriendo

ERROR "Aria does not contain a constructor":
  → No debería ocurrir. Si pasa, compartir el log completo.

Paciente aparece como "found: false":
  → El PatientId puede tener un formato distinto en ARIA.
     Verificar el ID directamente en la aplicación ARIA.

CUALQUIER OTRO ERROR:
  → Compartir el archivo .log completo.

========================================================
  ARCHIVOS REQUERIDOS EN LA CARPETA
========================================================

  AriaRunner.exe            (o AriaRunner.dll si es framework-dependent)
  AriaQ.dll                 (copiada de C:\Pablo\WebScrapSitra\Insumos ARIA\)
  EntityFramework.dll       (copiada automáticamente por el build)
  EntityFramework.SqlServer.dll
  Newtonsoft.Json.dll
  input_patients.json       (creada por vos con los IDs)

========================================================
