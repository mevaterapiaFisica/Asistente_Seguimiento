namespace Meva.Rt.Web;

/// <summary>
/// Singleton que trackea el estado de la consulta ARIA en background.
/// Permite al frontend hacer polling en lugar de esperar el HTTP request.
/// </summary>
sealed class AriaJobState
{
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public int TotalPatients { get; private set; }
    public bool? LastRunSucceeded { get; private set; }
    public int LastWithActivePlan { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    private string? _dataDir;

    public bool TryStart(int totalPatients, string dataDir)
    {
        lock (_lock)
        {
            if (IsRunning) return false;
            IsRunning = true;
            StartedAt = DateTime.UtcNow;
            TotalPatients = totalPatients;
            LastRunSucceeded = null;
            LastError = null;
            CompletedAt = null;
            LastWithActivePlan = 0;
            _dataDir = dataDir;
            return true;
        }
    }

    public void Complete(bool succeeded, int withActivePlan, string? error)
    {
        lock (_lock)
        {
            IsRunning = false;
            LastRunSucceeded = succeeded;
            LastWithActivePlan = withActivePlan;
            LastError = error;
            CompletedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Lee el progreso actual del log del AriaRunner (paciente actual / total).</summary>
    public (int current, int total) ReadProgress()
    {
        var dataDir = _dataDir;
        var startedAt = StartedAt;
        if (dataDir == null || !startedAt.HasValue) return (0, TotalPatients);

        // Encontrar el log más reciente creado después del inicio del job
        var logFile = Directory
            .GetFiles(dataDir, "aria_runner_*.log")
            .Where(f => File.GetCreationTimeUtc(f) >= startedAt.Value.AddSeconds(-10))
            .OrderByDescending(f => f)
            .FirstOrDefault();

        if (logFile == null || !File.Exists(logFile)) return (0, TotalPatients);

        try
        {
            using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var readSize = (int)Math.Min(fs.Length, 4096);
            fs.Seek(-readSize, SeekOrigin.End);
            var buf = new byte[readSize];
            var read = fs.Read(buf, 0, readSize);
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);

            var match = System.Text.RegularExpressions.Regex.Match(
                text, @"\[(\d+)/(\d+)\]",
                System.Text.RegularExpressions.RegexOptions.RightToLeft);

            if (match.Success)
                return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        }
        catch { }

        return (0, TotalPatients);
    }
}
