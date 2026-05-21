using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;

namespace Meva.Rt.AriaRunner;

class Program
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LogonUser(string lpszUsername, string lpszDomain, string lpszPassword,
        int dwLogonType, int dwLogonProvider, out IntPtr phToken);

    // Usa credenciales solo para conexiones de red (equivale a runas /netonly)
    const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
    const int LOGON32_PROVIDER_WINNT50 = 3;

    static int Main(string[] args)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
        var outputDir = ResolveOutputDir(args, exeDir);

        var logPath = Path.Combine(outputDir, $"aria_runner_{timestamp}.log");
        var resultsPath = Path.Combine(outputDir, $"aria_results_{timestamp}.json");

        using var log = new RunnerLogger(logPath);

        log.Info("=== Meva.Rt AriaRunner iniciado ===");
        log.Info($"Exe dir:     {exeDir}");
        log.Info($"Output dir:  {outputDir}");
        log.Info($"Log:         {logPath}");
        log.Info($"Resultados:  {resultsPath}");

        // ─── 1. Archivo de entrada ────────────────────────────────────────────────
        var inputPath = ResolveInputPath(args, exeDir, log);
        if (!File.Exists(inputPath))
        {
            log.Error($"Archivo de entrada no encontrado: {inputPath}");
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

        // ─── 2. Impersonación para conectar a ARIAMEVADB-SVR ──────────────────────
        var ariaPassword = Environment.GetEnvironmentVariable("ARIA_VARIAN_PASSWORD")
                        ?? ResolveArgValue(args, "--aria-password");

        if (!string.IsNullOrEmpty(ariaPassword))
        {
            log.Info("Impersonando ECL-FISICA2\\varian para conexión a ARIAMEVADB-SVR...");
            bool ok = LogonUser("varian", "ECL-FISICA2", ariaPassword,
                LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out IntPtr tokenHandle);

            if (!ok)
            {
                log.Error($"No se pudo crear el token de impersonación. Error Win32: {Marshal.GetLastWin32Error()}");
                log.Error("Verificá que la contraseña en ARIA_VARIAN_PASSWORD sea correcta.");
                return 1;
            }

            log.Info("Impersonación activa — las conexiones de red usarán ECL-FISICA2\\varian.");
            using var safeHandle = new SafeAccessTokenHandle(tokenHandle);
            return WindowsIdentity.RunImpersonated(safeHandle,
                () => RunQueries(input, resultsPath, log));
        }

        log.Warn("ARIA_VARIAN_PASSWORD no definida — conectando con usuario de Windows actual.");
        return RunQueries(input, resultsPath, log);
    }

    static int RunQueries(RunnerInput input, string resultsPath, RunnerLogger log)
    {
        // ─── Test de conexión ─────────────────────────────────────────────────────
        log.Info(new string('-', 60));
        log.Info("Probando conexión a ARIA...");
        var query = new AriaQuery(log);
        if (!query.TestConnection())
        {
            log.Error("Falló el test de conexión. Revisá ARIA_VARIAN_PASSWORD y conectividad a ARIAMEVADB-SVR.");
            return 1;
        }

        // ─── Búsqueda por paciente ────────────────────────────────────────────────
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
            output.Patients.Add(query.QueryPatient(id));
        }

        output.TotalFound = output.Patients.Count(p => p.Found && p.Error == null);
        output.TotalNotFound = output.Patients.Count(p => !p.Found);
        output.TotalErrors = output.Patients.Count(p => p.Error != null);

        // ─── Guardar resultados ───────────────────────────────────────────────────
        File.WriteAllText(resultsPath,
            JsonConvert.SerializeObject(output, Formatting.Indented),
            System.Text.Encoding.UTF8);

        log.Info(new string('=', 60));
        log.Info("Completado:");
        log.Info($"  Encontrados:    {output.TotalFound}/{output.TotalRequested}");
        log.Info($"  No encontrados: {output.TotalNotFound}");
        log.Info($"  Con errores:    {output.TotalErrors}");
        log.Info($"Resultados: {resultsPath}");
        log.Info($"Log:        {log.LogPath}");

        return 0;
    }

    static string ResolveInputPath(string[] args, string exeDir, RunnerLogger log)
    {
        var fromArg = ResolveArgValue(args, "--input");
        if (!string.IsNullOrWhiteSpace(fromArg))
        {
            log.Info($"Archivo de entrada: --input ({fromArg})");
            return fromArg;
        }
        var defaultPath = Path.Combine(exeDir, "pacientes.json");
        log.Info($"Archivo de entrada: por defecto ({defaultPath})");
        return defaultPath;
    }

    static string ResolveOutputDir(string[] args, string exeDir)
    {
        var fromArg = ResolveArgValue(args, "--output-dir");
        return !string.IsNullOrWhiteSpace(fromArg) ? fromArg : exeDir;
    }

    static string? ResolveArgValue(string[] args, string name)
    {
        var prefix = $"{name}=";
        foreach (var arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return arg.Substring(prefix.Length).Trim('"', '\'');
        }
        return null;
    }
}
