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
        "<tr>\\s*<td>(?<priority>\\d+)</td>\\s*<td>(?<date>\\d{2}[-/]\\d{2}[-/]\\d{4})</td>\\s*<td><span><a\\s+href=\"[^\"]*medical_histories/(?<guid>[^/\"]+)/overview\">(?<name>[^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Nro. HC: 1-3 dígitos guión 4-7 dígitos guión 1-3 dígitos (ej: 1-117505-0).
    // Excluye fechas dd-MM-yyyy (primer grupo 2 digs, último grupo 4 digs).
    private static readonly Regex PhysicistRegex = new(
        "select[^>]*physicist[^>]*>.*?<option[^>]+selected[^>]*>\\s*([^<]+?)\\s*</option>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex PatientIdRegex = new(
        ">(?<id>\\d{1,3}-\\d{4,7}-\\d{1,3})<",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SelectedStageRegex = new(
        "<option\\s+value=\"(?<microstatus>[^\"]+)\"\\s+selected=\"\">(?<label>[^<]+)</option>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TreatmentZoneRegex = new(
        @"<td[^>]*>\s*(?<zone>(?=[^<]*(?:Tridimensional|\b3DC?\b|Intensidad(?:\s|&nbsp;)+Modulada|\bSBRT\b|\bIGRT\b|\bTBI\b|Irradiaci[oó]n?(?:\s|&nbsp;)+[Cc]orporal|Radiocirug[ií]a|Braquiterapia|Intraoperatoria|\bIMRT\b|\bIORT\b|\bRXCX\b))[^<]+)\s*</td>",
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
                        SitraMedGuid = string.IsNullOrWhiteSpace(row.SitraMedGuid) ? null : row.SitraMedGuid,
                        AssignedPhysicist = string.IsNullOrWhiteSpace(row.AssignedPhysicist) ? null : row.AssignedPhysicist,
                        TreatmentType = TreatmentClassifier.Classify(row.TreatmentZone),
                        TreatmentTechnique = TreatmentClassifier.Classify(row.TreatmentZone),
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
                var sitraMedGuid = match.Groups["guid"].Success ? match.Groups["guid"].Value.Trim() : null;
                var assignedPhysicist = ResolvePhysicist(segment);
                var treatmentZone = ResolveTreatmentZone(segment);
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
                    SitraMedGuid = sitraMedGuid,
                    AssignedPhysicist = string.IsNullOrWhiteSpace(assignedPhysicist) ? null : assignedPhysicist,
                    TreatmentType = TreatmentClassifier.Classify(treatmentZone),
                    TreatmentTechnique = TreatmentClassifier.Classify(treatmentZone),
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

    private string ResolvePhysicist(string segment)
    {
        var m = PhysicistRegex.Match(segment);
        return m.Success ? HtmlDecode(m.Groups[1].Value).Trim() : string.Empty;
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

    private static string ResolveTreatmentZone(string segment)
    {
        var matches = TreatmentZoneRegex.Matches(segment);
        if (matches.Count == 0) return string.Empty;
        // Si hay múltiples tratamientos preferir el que no sea BQT/IORT
        foreach (Match m in matches)
        {
            var zone = DecodeAndStrip(m.Groups["zone"].Value);
            var tech = TreatmentClassifier.Classify(zone);
            if (tech != "BQT" && tech != "IORT") return zone;
        }
        return DecodeAndStrip(matches[0].Groups["zone"].Value);
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

public sealed class SitraMedTomographExtractor : ITomographAgendaExtractor
{
    // Captures the full content of a table row (all cells inside <tr>...</tr>).
    private static readonly Regex TomographRowRegex = new(
        "<tr[^>]*>(?<row>(?:\\s*<td[^>]*>.*?</td>)+)\\s*</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Extracts individual cell content from a row.
    private static readonly Regex CellRegex = new(
        "<td[^>]*>(?<cell>.*?)</td>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex GuidFromHrefRegex = new(
        @"medical_histories/([^/]+)/overview",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IRtSystemConfigurationProvider _configurationProvider;
    private readonly SitraMedRuntimeOptions _options;

    public SitraMedTomographExtractor(IRtSystemConfigurationProvider configurationProvider, SitraMedRuntimeOptions options)
    {
        _configurationProvider = configurationProvider;
        _options = options;
    }

    public async Task<IReadOnlyList<MachineAppointmentSnapshot>> ExtractForDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var tomographs = _configurationProvider.Configuration.Tomographs;
        var client = new PlaywrightSitraMedClient(_options);
        var remotePages = await client.DownloadTomographAgendaPagesAsync(tomographs, date, cancellationToken);
        if (remotePages.Count > 0)
        {
            return remotePages
                .SelectMany(snapshot =>
                {
                    if (snapshot.DomSnapshots is { Count: > 0 }) return snapshot.DomSnapshots;
                    return ParseTomographSnapshots(snapshot);
                })
                .ToList();
        }
        return Array.Empty<MachineAppointmentSnapshot>();
    }

    public async Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<MachineAppointmentSnapshot>>> ExtractForDatesAsync(
        IEnumerable<DateOnly> dates,
        CancellationToken cancellationToken)
    {
        var tomographs = _configurationProvider.Configuration.Tomographs;
        var dateList = dates.Distinct().OrderBy(d => d).ToList();
        var client = new PlaywrightSitraMedClient(_options);
        var rawByDate = await client.DownloadTomographAgendaPagesForDatesAsync(tomographs, dateList, cancellationToken);

        var result = new Dictionary<DateOnly, IReadOnlyList<MachineAppointmentSnapshot>>();
        foreach (var (date, pages) in rawByDate)
        {
            result[date] = pages
                .SelectMany(snapshot =>
                {
                    if (snapshot.DomSnapshots is { Count: > 0 }) return snapshot.DomSnapshots;
                    return ParseTomographSnapshots(snapshot);
                })
                .ToList();
        }
        return result;
    }

    private IReadOnlyList<MachineAppointmentSnapshot> ParseTomographSnapshots(TomographAgendaHtmlSnapshot source)
    {
        var rowMatches = TomographRowRegex.Matches(source.Html);
        var result = new List<MachineAppointmentSnapshot>();

        foreach (Match rowMatch in rowMatches)
        {
            var cellMatches = CellRegex.Matches(rowMatch.Groups["row"].Value);
            var cells = cellMatches
                .Select(m => DecodeAndStrip(m.Groups["cell"].Value))
                .Where(v => v.Length > 0 || true)  // keep all, including empty (signs col)
                .ToArray();

            if (cells.Length < 2) continue;

            // Skip header rows
            var joined = string.Join(' ', cells).ToLowerInvariant();
            if (joined.Contains("paciente") && (joined.Contains("inicio") || joined.Contains("hora"))) continue;

            // Skip empty leading cells
            var offset = 0;
            while (offset < cells.Length - 1 && string.IsNullOrWhiteSpace(cells[offset])) offset++;

            var startTime = offset < cells.Length ? cells[offset] : string.Empty;
            var patient   = offset + 1 < cells.Length ? cells[offset + 1] : string.Empty;

            // Extract GUID from raw patient cell HTML (before DecodeAndStrip loses the href)
            string? patientGuid = null;
            if (offset + 1 < cellMatches.Count)
            {
                var rawCell = cellMatches[offset + 1].Groups["cell"].Value;
                var gm = GuidFromHrefRegex.Match(rawCell);
                if (gm.Success) patientGuid = gm.Groups[1].Value;
            }

            // Scan all cells (offset+2 onwards) for a treatment keyword
            var tipoTurno = string.Empty;
            for (var j = offset + 2; j < cells.Length; j++)
            {
                if (IsValidTreatmentSlot(cells[j]))
                {
                    tipoTurno = TreatmentClassifier.Classify(cells[j]);
                    if (string.IsNullOrEmpty(tipoTurno)) tipoTurno = cells[j];
                    break;
                }
            }
            if (string.IsNullOrEmpty(tipoTurno)) continue;
            if (string.IsNullOrWhiteSpace(startTime) || string.IsNullOrWhiteSpace(patient) || patient == "-") continue;

            result.Add(new MachineAppointmentSnapshot
            {
                CenterName  = source.CenterName,
                MachineName = source.TomographDisplayName,
                PatientName = patient,
                AgendaDate  = source.AgendaDate,
                StartTime   = startTime,
                EndTime     = string.Empty,
                Treatment   = tipoTurno,
                SitraMedGuid = patientGuid
            });
        }

        return result;
    }

    private static readonly string[] ValidTreatmentKeywords = ["3D", "IMRT", "SBRT", "RxCx", "Modulada", "Tridimensional", "Braquiterapia", "Radiocirug", "Intraoperatoria", "IORT", "IGRT", "TBI"];

    private static bool IsValidTreatmentSlot(string tipoTurno)
    {
        if (string.IsNullOrWhiteSpace(tipoTurno) || tipoTurno.Trim() == "-") return false;
        if (tipoTurno.Contains("actividad", StringComparison.OrdinalIgnoreCase)) return false;
        return ValidTreatmentKeywords.Any(k => tipoTurno.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string DecodeAndStrip(string value)
    {
        return WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(value, "<.*?>", " "), "\\s+", " ").Trim());
    }
}

internal sealed class FollowUpHtmlSource
{
    public string FileName { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
}
