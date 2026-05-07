using Newtonsoft.Json;

namespace Meva.Rt.AriaRunner;

class Program
{
    static int Main(string[] args)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var outputDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;

        var logPath = Path.Combine(outputDir, $"aria_runner_{timestamp}.log");
        var resultsPath = Path.Combine(outputDir, $"aria_results_{timestamp}.json");

        using var log = new RunnerLogger(logPath);

        log.Info("=== Meva.Rt AriaRunner iniciado ===");
        log.Info($"Directorio:  {outputDir}");
        log.Info($"Log:         {logPath}");
        log.Info($"Resultados:  {resultsPath}");

        // ─── 1. Archivo de entrada ────────────────────────────────────────────────
        var inputPath = ResolveInputPath(args, outputDir, log);
        if (!File.Exists(inputPath))
        {
            log.Error($"Archivo de entrada no encontrado: {inputPath}");
            log.Error("Creá un archivo 'pacientes.json' en la misma carpeta con el formato:");
            log.Error("  { \"patientIds\": [\"1-117505-0\", \"1-118031-0\"] }");
            return 1;
        }

        RunnerInput input;
        try
        {
            var json = File.ReadAllText(inputPath, System.Text.Encoding.UTF8);
            input = JsonConvert.DeserializeObject<RunnerInput>(json) ?? new RunnerInput();
            log.Info($"Entrada:     {inputPath} ({input.PatientIds.Count} pacientes)");
        }
        catch (Exception ex)
        {
            log.Error($"Error al leer el archivo de entrada: {inputPath}", ex);
            return 1;
        }

        if (input.PatientIds.Count == 0)
        {
            log.Warn("La lista de patientIds está vacía. Sin trabajo que hacer.");
            return 0;
        }

        // ─── 2. Test de conexión ──────────────────────────────────────────────────
        log.Info(new string('-', 60));
        log.Info("Probando conexión a ARIA...");
        var query = new AriaQuery(log);
        if (!query.TestConnection())
        {
            log.Error("Falló el test de conexión. Revisá el log y la configuración en AriaRunner.exe.config.");
            return 1;
        }

        // ─── 3. Búsqueda por paciente ──────────────────────────────────────────
        var output = new RunnerOutput { TotalRequested = input.PatientIds.Count };

        log.Info(new string('-', 60));
        log.Info($"Buscando {input.PatientIds.Count} pacientes...");

        for (var i = 0; i < input.PatientIds.Count; i++)
        {
            var id = (input.PatientIds[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                log.Warn($"[{i + 1}/{input.PatientIds.Count}] ID vacío ignorado");
                continue;
            }

            log.Info($"[{i + 1}/{input.PatientIds.Count}] {id}");
            var result = query.QueryPatient(id);
            output.Patients.Add(result);
        }

        output.TotalFound = output.Patients.Count(p => p.Found && p.Error == null);
        output.TotalNotFound = output.Patients.Count(p => !p.Found);
        output.TotalErrors = output.Patients.Count(p => p.Error != null);

        // ─── 4. Guardar resultados ─────────────────────────────────────────────
        var resultJson = JsonConvert.SerializeObject(output, Formatting.Indented);
        File.WriteAllText(resultsPath, resultJson, System.Text.Encoding.UTF8);

        log.Info(new string('=', 60));
        log.Info("Completado:");
        log.Info($"  Encontrados:    {output.TotalFound}/{output.TotalRequested}");
        log.Info($"  No encontrados: {output.TotalNotFound}");
        log.Info($"  Con errores:    {output.TotalErrors}");
        log.Info($"Resultados: {resultsPath}");
        log.Info($"Log:        {log.LogPath}");

        return 0;
    }

    private static string ResolveInputPath(string[] args, string outputDir, RunnerLogger log)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--input=", StringComparison.OrdinalIgnoreCase))
            {
                var path = arg["--input=".Length..].Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(path))
                {
                    log.Info($"Archivo de entrada: argumento --input ({path})");
                    return path;
                }
            }
        }

        var defaultPath = Path.Combine(outputDir, "pacientes.json");
        log.Info($"Archivo de entrada: por defecto ({defaultPath})");
        return defaultPath;
    }
}
