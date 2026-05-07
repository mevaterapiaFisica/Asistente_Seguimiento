using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Meva.Rt.Application;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.SitraMed;

public sealed class SitraMedAgendaExtractor : IAgendaExtractor
{
    private static readonly Regex AgendaRowRegex = new(
        "<tr[^>]*>\\s*(?:<td[^>]*>(?:\\s|<[^>]+>)*</td>\\s*)?<td[^>]*>(?<inicio>.*?)</td>\\s*<td[^>]*>(?<paciente>.*?)</td>\\s*<td[^>]*>(?<equipo>.*?)</td>\\s*<td[^>]*>(?<prioridad>.*?)</td>\\s*<td[^>]*>(?<observaciones>.*?)</td>\\s*<td[^>]*>(?<institucion>.*?)</td>\\s*<td[^>]*>(?<tipo>.*?)</td>\\s*<td[^>]*>(?<tratamiento>.*?)</td>\\s*<td[^>]*>(?<fechaInicio>.*?)</td>\\s*<td[^>]*>(?<fechaFin>.*?)</td>\\s*<td[^>]*>(?<horaFin>.*?)</td>\\s*<td[^>]*>(?<estado>.*?)</td>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly IRtSystemConfigurationProvider _configurationProvider;
    private readonly SitraMedRuntimeOptions _options;

    public SitraMedAgendaExtractor(IRtSystemConfigurationProvider configurationProvider, SitraMedRuntimeOptions options)
    {
        _configurationProvider = configurationProvider;
        _options = options;
    }

    public async Task<IReadOnlyList<MachineAppointmentSnapshot>> ExtractAsync(CancellationToken cancellationToken)
    {
        var machines = _configurationProvider.Configuration.Machines;
        var client = new PlaywrightSitraMedClient(_options);
        var remotePages = await client.DownloadAgendaPagesAsync(machines, DateOnly.FromDateTime(DateTime.Today), cancellationToken);
        if (remotePages.Count > 0)
        {
            return remotePages
                .SelectMany(snapshot =>
                {
                    if (snapshot.DomSnapshots is { Count: > 0 })
                    {
                        return snapshot.DomSnapshots;
                    }

                    return ParseAgendaSnapshots(snapshot);
                })
                .ToList();
        }

        IReadOnlyList<MachineAppointmentSnapshot> result = machines
            .Take(4)
            .Select((machine, index) => new MachineAppointmentSnapshot
            {
                CenterName = machine.CenterName,
                MachineName = machine.DisplayName,
                PatientName = $"Paciente Agenda {index + 1}",
                AgendaDate = DateOnly.FromDateTime(DateTime.Today),
                StartTime = $"{8 + index:00}:00",
                EndTime = $"{8 + index:00}:15",
                Treatment = "Demo"
            })
            .ToList();

        return result;
    }

    public async Task<IReadOnlyList<MachineAppointmentSnapshot>> ExtractForDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var machines = _configurationProvider.Configuration.Machines;
        var client = new PlaywrightSitraMedClient(_options);
        var remotePages = await client.DownloadAgendaPagesAsync(machines, date, cancellationToken);
        if (remotePages.Count > 0)
        {
            return remotePages
                .SelectMany(snapshot =>
                {
                    if (snapshot.DomSnapshots is { Count: > 0 })
                        return snapshot.DomSnapshots;
                    return ParseAgendaSnapshots(snapshot);
                })
                .ToList();
        }
        return Array.Empty<MachineAppointmentSnapshot>();
    }

    public async Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<MachineAppointmentSnapshot>>> ExtractForDatesAsync(
        IEnumerable<DateOnly> dates,
        CancellationToken cancellationToken)
    {
        var machines = _configurationProvider.Configuration.Machines;
        var dateList = dates.Distinct().OrderBy(d => d).ToList();
        var client = new PlaywrightSitraMedClient(_options);
        var rawByDate = await client.DownloadAgendaPagesForDatesAsync(machines, dateList, cancellationToken);

        var result = new Dictionary<DateOnly, IReadOnlyList<MachineAppointmentSnapshot>>();
        foreach (var (date, pages) in rawByDate)
        {
            result[date] = pages
                .SelectMany(snapshot =>
                {
                    if (snapshot.DomSnapshots is { Count: > 0 })
                        return snapshot.DomSnapshots;
                    return ParseAgendaSnapshots(snapshot);
                })
                .ToList();
        }
        return result;
    }

    private IReadOnlyList<MachineAppointmentSnapshot> ParseAgendaSnapshots(AgendaHtmlSnapshot source)
    {
        var rows = AgendaRowRegex.Matches(source.Html);
        var result = new List<MachineAppointmentSnapshot>();

        foreach (Match row in rows)
        {
            var startTime = DecodeAndStrip(row.Groups["inicio"].Value);
            var patientName = DecodeAndStrip(row.Groups["paciente"].Value);
            if (string.IsNullOrWhiteSpace(startTime) || string.IsNullOrWhiteSpace(patientName) || patientName == "-")
            {
                continue;
            }

            result.Add(new MachineAppointmentSnapshot
            {
                CenterName = source.CenterName,
                MachineName = source.MachineDisplayName,
                PatientName = patientName,
                AgendaDate = source.AgendaDate,
                StartTime = startTime,
                EndTime = DecodeAndStrip(row.Groups["horaFin"].Value),
                Treatment = DecodeAndStrip(row.Groups["tratamiento"].Value)
            });
        }

        return result;
    }

    private static string DecodeAndStrip(string value)
    {
        return WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(value, "<.*?>", " "), "\\s+", " ").Trim());
    }
}

public sealed class SitraMedFollowUpExtractor : IFollowUpExtractor
{
    private static readonly Regex RowStartRegex = new(
        "<tr>\\s*<td>(?<priority>\\d+)</td>\\s*<td>(?<date>\\d{2}[-/]\\d{2}[-/]\\d{4})</td>\\s*<td><span><a\\s+href=\"[^\"]+/overview\">(?<name>[^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Nro. HC: 1-3 dígitos guión 4-7 dígitos guión 1-3 dígitos (ej: 1-117505-0).
    // Excluye fechas dd-MM-yyyy (primer grupo 2 digs, último grupo 4 digs).
    private static readonly Regex PatientIdRegex = new(
        ">(?<id>\\d{1,3}-\\d{4,7}-\\d{1,3})<",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SelectedStageRegex = new(
        "<option\\s+value=\"(?<microstatus>[^\"]+)\"\\s+selected=\"\">(?<label>[^<]+)</option>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly string[] DateFormats = ["dd-MM-yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "yyyy/MM/dd"];

    private readonly IRtSystemConfigurationProvider _configurationProvider;
    private readonly SitraMedRuntimeOptions _options;

    public SitraMedFollowUpExtractor(IRtSystemConfigurationProvider configurationProvider, SitraMedRuntimeOptions options)
    {
        _configurationProvider = configurationProvider;
        _options = options;
    }

    public async Task<IReadOnlyList<ProcessPatientSnapshot>> ExtractAsync(CancellationToken cancellationToken)
    {
        var cfg = _configurationProvider.Configuration;
        var client = new PlaywrightSitraMedClient(_options);
        var remotePages = await client.DownloadFollowUpPagesAsync(cfg.Centers, cfg.Stages, cancellationToken);

        if (remotePages.Count > 0)
        {
            return ParseRemoteSnapshots(remotePages);
        }

        if (!_options.UseLocalExamplesFallback)
        {
            return Array.Empty<ProcessPatientSnapshot>();
        }

        var examplesDirectory = ResolveExamplesDirectory();
        if (examplesDirectory == null || !Directory.Exists(examplesDirectory))
        {
            return Array.Empty<ProcessPatientSnapshot>();
        }

        var localSources = Directory.GetFiles(examplesDirectory, "*.html", SearchOption.TopDirectoryOnly)
            .Select(path => new FollowUpHtmlSource
            {
                FileName = Path.GetFileName(path),
                Html = File.ReadAllText(path)
            })
            .ToList();

        return ParseSnapshots(localSources);
    }

    private IReadOnlyList<ProcessPatientSnapshot> ParseRemoteSnapshots(IReadOnlyList<FollowUpHtmlSnapshot> snapshots)
    {
        var result = new List<ProcessPatientSnapshot>();
        foreach (var snapshot in snapshots)
        {
            if (snapshot.DomRows is { Count: > 0 })
            {
                var stageDefinition = _configurationProvider.Configuration.Stages
                    .FirstOrDefault(s => string.Equals(s.Code, snapshot.StageCode, StringComparison.OrdinalIgnoreCase));
                if (stageDefinition == null) continue;

                foreach (var row in snapshot.DomRows)
                {
                    if (string.IsNullOrWhiteSpace(row.PatientName)) continue;

                    var stageStartDate = ParseDate(row.FirstConsultDate);
                    var daysInStage = stageStartDate.HasValue
                        ? Math.Max(0, DateOnly.FromDateTime(DateTime.Today).DayNumber - stageStartDate.Value.DayNumber)
                        : 0;

                    result.Add(new ProcessPatientSnapshot
                    {
                        PatientId = row.SitraMedId,
                        PatientName = row.PatientName,
                        CenterName = row.CenterName,
                        StageCode = stageDefinition.Code,
                        StageDisplayName = stageDefinition.DisplayName,
                        StageGroupName = stageDefinition.GroupName,
                        StageStartDate = stageStartDate,
                        DaysInStage = daysInStage,
                        ExpectedDaysInStage = stageDefinition.ExpectedDays,
                        IsDelayed = daysInStage > stageDefinition.ExpectedDays,
                        SourceCenterName = row.CenterName
                    });
                }
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.Html))
            {
                var htmlSource = new FollowUpHtmlSource
                {
                    FileName = $"{snapshot.CenterName}_{snapshot.StageCode}.html",
                    Html = snapshot.Html
                };
                result.AddRange(ParseSnapshots(new[] { htmlSource }));
            }
        }

        return result
            .GroupBy(x => $"{x.PatientId}|{x.StageCode}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.CenterName)
            .ThenBy(x => x.StageCode)
            .ThenBy(x => x.PatientName)
            .ToList();
    }

    private IReadOnlyList<ProcessPatientSnapshot> ParseSnapshots(IEnumerable<FollowUpHtmlSource> sources)
    {
        var result = new List<ProcessPatientSnapshot>();
        foreach (var source in sources)
        {
            var stageDefinition = ResolveStageDefinition(source.Html);
            if (stageDefinition == null)
            {
                continue;
            }

            var matches = RowStartRegex.Matches(source.Html);
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var segmentStart = match.Index;
                var segmentEnd = i + 1 < matches.Count ? matches[i + 1].Index : source.Html.Length;
                var segment = source.Html.Substring(segmentStart, segmentEnd - segmentStart);

                var patientName = HtmlDecode(match.Groups["name"].Value).Trim();
                var patientId = ResolvePatientId(segment);
                var centerName = ResolveCenterName(segment);
                var stageStartDate = FollowUpDateParser.ExtractStageEntryDate(segment, stageDefinition.Code)
                                     ?? ParseDate(match.Groups["date"].Value);

                if (string.IsNullOrWhiteSpace(patientName) || string.IsNullOrWhiteSpace(patientId))
                {
                    continue;
                }

                var daysInStage = stageStartDate.HasValue
                    ? Math.Max(0, (DateOnly.FromDateTime(DateTime.Today).DayNumber - stageStartDate.Value.DayNumber))
                    : 0;

                result.Add(new ProcessPatientSnapshot
                {
                    PatientId = patientId,
                    PatientName = patientName,
                    CenterName = centerName,
                    StageCode = stageDefinition.Code,
                    StageDisplayName = stageDefinition.DisplayName,
                    StageGroupName = stageDefinition.GroupName,
                    StageStartDate = stageStartDate,
                    DaysInStage = daysInStage,
                    ExpectedDaysInStage = stageDefinition.ExpectedDays,
                    IsDelayed = daysInStage > stageDefinition.ExpectedDays,
                    SourceCenterName = centerName
                });
            }
        }

        return result
            .GroupBy(x => $"{x.PatientId}|{x.StageCode}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(x => x.CenterName)
            .ThenBy(x => x.StageCode)
            .ThenBy(x => x.PatientName)
            .ToList();
    }

    private ProcessStageDefinition? ResolveStageDefinition(string html)
    {
        var selectedMatch = SelectedStageRegex.Match(html);
        if (!selectedMatch.Success)
        {
            return null;
        }

        var microstatus = selectedMatch.Groups["microstatus"].Value.Trim();
        return _configurationProvider.Configuration.Stages.FirstOrDefault(x =>
            string.Equals(x.SitraMicroStatus, microstatus, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolvePatientId(string segment)
    {
        var patientIdMatch = PatientIdRegex.Match(segment);
        return patientIdMatch.Success ? patientIdMatch.Groups["id"].Value.Trim() : string.Empty;
    }

    private string ResolveCenterName(string segment)
    {
        foreach (var center in _configurationProvider.Configuration.Centers.OrderByDescending(x => x.Name.Length))
        {
            if (segment.Contains($">{center.Name}<", StringComparison.OrdinalIgnoreCase))
            {
                return center.Name;
            }
        }

        return string.Empty;
    }

    private static DateOnly? ParseDate(string value)
    {
        if (DateOnly.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string HtmlDecode(string value)
    {
        return WebUtility.HtmlDecode(value);
    }

    private static string DecodeAndStrip(string value)
    {
        var withoutTags = Regex.Replace(value, "<.*?>", " ");
        return HtmlDecode(Regex.Replace(withoutTags, "\\s+", " ").Trim());
    }

    private static string? ResolveExamplesDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "Ejemplos seguimiento");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}

internal sealed class FollowUpHtmlSource
{
    public string FileName { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
}
