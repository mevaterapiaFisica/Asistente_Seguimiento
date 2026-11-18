using System.Text;

namespace Meva.Rt.Web;

/// <summary>
/// Duplica todo lo que se escribe en Console.Error a un archivo diario bajo MEVA_DATA_DIR/logs.
/// Como servicio de Windows no hay consola: sin esto los `[SitraMed]`/`[BuildAsync]` se pierden.
/// Rotación: un archivo por día (meva_yyyyMMdd.log), se borran los de más de 14 días al arrancar.
/// </summary>
public sealed class FileTeeWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly string _dir;
    private readonly object _lock = new();
    private bool _atLineStart = true;

    public FileTeeWriter(TextWriter inner, string directory, int keepDays = 14)
    {
        _inner = inner;
        _dir = directory;
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var f in Directory.GetFiles(_dir, "meva_*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
        }
        catch { /* el log nunca debe tumbar la app */ }
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value) => Emit(value.ToString());
    public override void Write(string? value) => Emit(value);
    public override void WriteLine(string? value) => Emit((value ?? string.Empty) + Environment.NewLine);
    public override void WriteLine() => Emit(Environment.NewLine);

    private void Emit(string? s)
    {
        if (string.IsNullOrEmpty(s)) return;
        lock (_lock)
        {
            try { _inner.Write(s); } catch { }
            try
            {
                var now = DateTime.Now;
                var text = _atLineStart ? $"{now:HH:mm:ss} {s}" : s;
                File.AppendAllText(Path.Combine(_dir, $"meva_{now:yyyyMMdd}.log"), text);
                _atLineStart = s.EndsWith('\n');
            }
            catch { }
        }
    }
}
