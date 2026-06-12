================================================================
  Meva.Rt AriaRunner
================================================================

AriaRunner es un ejecutable de linea de comandos que consulta la
base de datos de ARIA en bulk y guarda los resultados en JSON.
Corre en la misma PC Win10 que el servidor MevaRT.

USO NORMAL
----------
El servidor lo lanza automaticamente via el boton "Actualizar" en
la web, o a traves del script scripts\refresh.bat.

No se necesita correrlo manualmente salvo para diagnostico.

USO MANUAL
----------
  AriaRunner.exe --input="C:\MevaRT\data\pacientes.json" --output-dir="C:\MevaRT\data"

  Requiere la variable de entorno ARIA_VARIAN_PASSWORD con la
  contrasena del usuario ECL-FISICA2\varian.

RESULTADOS
----------
  aria_results_YYYYMMDD_HHMMSS.json  →  datos de planes por paciente
  aria_runner_YYYYMMDD_HHMMSS.log    →  log de ejecucion

PUBLICAR
--------
  scripts\publish.ps1  (como Administrador)
  Compila y copia a C:\MevaRT\AriaRunner\

================================================================
