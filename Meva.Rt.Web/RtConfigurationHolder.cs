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
            var def = AppConfiguration.BuildDefault();
            Persist(def);
            return def;
        }

        try
        {
            var json = File.ReadAllText(_configurationPath);
            var config = JsonSerializer.Deserialize<RtSystemConfiguration>(json) ?? AppConfiguration.BuildDefault();
            // Backfill fields added after the saved JSON was written; persist so next restart is clean
            bool dirty = false;
            if (config.MachineCapabilities.Count == 0)
            {
                config.MachineCapabilities = AppConfiguration.BuildDefault().MachineCapabilities;
                dirty = true;
            }
            if (config.TechniqueDurations.Count == 0)
            {
                config.TechniqueDurations = AppConfiguration.BuildDefault().TechniqueDurations;
                dirty = true;
            }
            if (config.P1AlertThresholdDays == 0)
            {
                config.P1AlertThresholdDays = AppConfiguration.BuildDefault().P1AlertThresholdDays;
                dirty = true;
            }
            if (dirty) Persist(config);
            return config;
        }
        catch
        {
            return AppConfiguration.BuildDefault();
        }
    }

    private void Persist(RtSystemConfiguration configuration) =>
        File.WriteAllText(_configurationPath, JsonSerializer.Serialize(configuration, _jsonOptions));

    public void Save(RtSystemConfiguration configuration)
    {
        var directory = Path.GetDirectoryName(_configurationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        Persist(configuration);
        Configuration = configuration;
    }
}
