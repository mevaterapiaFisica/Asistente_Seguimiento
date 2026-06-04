using System.Text.Json;
using Meva.Rt.Application;
using Meva.Rt.Core;

namespace Meva.Rt.Web;

public sealed class RtConfigurationHolder : IRtSystemConfigurationProvider
{
    private readonly string _configurationPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public RtConfigurationHolder(string contentRootPath)
    {
        _configurationPath = Path.Combine(contentRootPath, "data", "rt_configuration.json");
        Configuration = LoadOrCreateDefault();
    }

    public RtSystemConfiguration Configuration { get; private set; }

    private RtSystemConfiguration LoadOrCreateDefault()
    {
        if (!File.Exists(_configurationPath))
        {
            return AppConfiguration.BuildDefault();
        }

        try
        {
            var json = File.ReadAllText(_configurationPath);
            var config = JsonSerializer.Deserialize<RtSystemConfiguration>(json) ?? AppConfiguration.BuildDefault();
            // Backfill new fields added after the saved JSON was written
            if (config.MachineCapabilities.Count == 0)
                config.MachineCapabilities = AppConfiguration.BuildDefault().MachineCapabilities;
            return config;
        }
        catch
        {
            return AppConfiguration.BuildDefault();
        }
    }

    public void Save(RtSystemConfiguration configuration)
    {
        var directory = Path.GetDirectoryName(_configurationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_configurationPath, JsonSerializer.Serialize(configuration, _jsonOptions));
        Configuration = configuration;
    }
}
