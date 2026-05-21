namespace Meva.Rt.AriaRunner;

public sealed class RunnerLogger : IDisposable
{
    private readonly StreamWriter _file;

    public RunnerLogger(string logPath)
    {
        LogPath = logPath;
        _file = new StreamWriter(logPath, append: false, encoding: System.Text.Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public string LogPath { get; }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception ex) =>
        Write("ERROR", $"{message} | {ex.GetType().Name}: {ex.Message}\n         StackTrace: {ex.StackTrace}");

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        _file.WriteLine(line);
        if (level == "ERROR")
            Console.WriteLine(line);
    }

    public void Dispose() => _file.Dispose();
}
