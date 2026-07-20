using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Meva.Rt.Application;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.SitraMed;

public sealed class SitraMedAgendaExtractor : IAgendaExtractor
{
    private static readonly Regex AgendaRowRegex = new(
        "<tr(?<trAttrs>[^>]*)>\\s*(?:<td[^>]*>(?:\\s|<[^>]+>)*</td>\\s*)?<td[^>]*>(?<inicio>.*?)</td>\\s*<td[^>]*>(?<paciente>.*?)</td>\\s*<td[^>]*>(?<equipo>.*?)</td>\\s*<td[^>]*>(?<prioridad>.*?)</td>\\s*<td[^>]*>(?<observaciones>.*?)</td>\\s*<td[^>]*>(?<institucion>.*?)</td>\\s*<td[^>]*>(?<tipo>.*?)</td>\\s*<td[^>]*>(?<tratamiento>.*?)</td>\\s*<td[^>]*>(?<fechaInicio>.*?)</td>\\s*<td[^>]*>(?<fechaFin>.*?)</td>\\s*<td[^>]*>(?<horaFin>.*?)</td>\\s*<td[^>]*>(?<estado>.*?)</td>",
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

    public async Task<AgendaExtractionResult> ExtractForDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var machines = _configurationProvider.Configuration.Machines;
        var client = new PlaywrightSitraMedClient(_options);
        var remotePages = await client.DownloadAgendaPagesAsync(machines, date, cancellationToken);
        if (remotePages.Count > 0)
        {
            var errors = remotePages
                .Where(s => s.HasScrapingError)
                .Select(s => s.MachineDisplayName)
                .ToList();
            var slots = remotePages
                .Where(s => !s.HasScrapingError)
                .SelectMany(snapshot =>
                {
                    if (snapshot.DomSnapshots is { Count: > 0 })
                        return snapshot.DomSnapshots;
                    return ParseAgendaSnapshots(snapshot);
                })
                .ToList();
            return new AgendaExtractionResult { Slots = slots, ScrapingErrors = errors };
        }
        return new AgendaExtractionResult { Slots = [] };
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
            // Skip rows with grey background (future appointments mixed into today's view)
            if (IsGreyBackground(row.Groups["trAttrs"].Value))
                continue;

            var startTime = DecodeAndStrip(row.Groups["inicio"].Value);
            var patientName = DecodeAndStrip(row.Groups["paciente"].Value);
            if (string.IsNullOrWhiteSpace(startTime) || string.IsNullOrWhiteSpace(patientName) || patientName == "-")
            {
                continue;
            }

            // Skip rows whose estimated end date (fechaFin) is before the requested date.
            // These are past-treatment remnants rendered in grey by SitraMed ("Tratamiento Estima Finalizado").
            var fechaFinRaw = DecodeAndStrip(row.Groups["fechaFin"].Value);
            if (!string.IsNullOrWhiteSpace(fechaFinRaw) &&
                DateOnly.TryParseExact(fechaFinRaw,
                    new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var rowDate) &&
                rowDate < source.AgendaDate)
                continue;

            result.Add(new MachineAppointmentSnapshot
            {
                CenterName = source.CenterName,
                MachineName = source.MachineDisplayName,
                PatientName = patientName,
                AgendaDate = source.AgendaDate,
                StartTime = startTime,
                EndTime = DecodeAndStrip(row.Groups["horaFin"].Value),
                Treatment = DecodeAndStrip(row.Groups["tratamiento"].Value),
                Priority = int.TryParse(DecodeAndStrip(row.Groups["prioridad"].Value), out var ap) ? ap : (int?)null
            });
        }

        return result;
    }

    private static string DecodeAndStrip(string value)
    {
        return WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(value, "<.*?>", " "), "\\s+", " ").Trim());
    }

    // Detects grey-ish background colors (R≈G≈B, not white, not black).
    // Used to filter future appointment rows that SitraMed renders in grey instead of blue.
    private static bool IsGreyBackground(string style)
    {
        if (string.IsNullOrEmpty(style)) return false;
        var s = style.ToLowerInvariant();
        if (s.Contains("gray") || s.Contains("grey") || s.Contains("silver")) return true;

        var hex = Regex.Match(s, @"#([0-9a-f]{3,6})\b");
        if (hex.Success)
        {
            var h = hex.Groups[1].Value;
            int r, g, b;
            if (h.Length == 3) { r = Convert.ToInt32($"{h[0]}{h[0]}", 16); g = Convert.ToInt32($"{h[1]}{h[1]}", 16); b = Convert.ToInt32($"{h[2]}{h[2]}", 16); }
            else if (h.Length == 6) { r = Convert.ToInt32(h[..2], 16); g = Convert.ToInt32(h[2..4], 16); b = Convert.ToInt32(h[4..6], 16); }
            else return false;
            var maxC = Math.Max(r, Math.Max(g, b)); var minC = Math.Min(r, Math.Min(g, b));
            return maxC - minC < 25 && maxC > 100 && maxC < 245;
        }

        var rgb = Regex.Match(s, @"rgba?\((\d+),\s*(\d+),\s*(\d+)");
        if (rgb.Success)
        {
            var r = int.Parse(rgb.Groups[1].Value); var g = int.Parse(rgb.Groups[2].Value); var b = int.Parse(rgb.Groups[3].Value);
            var maxC = Math.Max(r, Math.Max(g, b)); var minC = Math.Min(r, Math.Min(g, b));
            return maxC - minC < 25 && maxC > 100 && maxC < 245;
        }

        return false;
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
        @"<td[^>]*>\s*(?<zone>(?=[^<]*(?:Tridimensional|\b3DC?\b|Intensidad(?:\s|&nbsp;)+Modulada|\bSBRT\b|\bIGRT\b|\bTBI\b|\bVMAT\b|\barco\b|Irradiaci[oó]n?(?:\s|&nbsp;)+[Cc]orporal|Radiocirug[ií]a|Braquiterapia|Intraoperatoria|\bIMRT\b|\bIORT\b|\bRXCX\b))[^<]+)\s*</td>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly string[] DateFormats = ["dd-MM-yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "yyyy/MM/dd"];

    private readonly IRtSystemConfigurationProvider _configurationProvider;
    private readonly SitraMedRuntimeOptions _options;
    private readonly BusinessDayCalculator _bdCalc;

    public SitraMedFollowUpExtractor(IRtSystemConfigurationProvider configurationProvider, SitraMedRuntimeOptions options, BusinessDayCalculator bdCalc)
    {
        _configurationProvider = configurationProvider;
        _options = options;
        _bdCalc = bdCalc;
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
                    var today = DateOnly.FromDateTime(DateTime.Today);
                    var daysInStage = stageStartDate.HasValue
                        ? _bdCalc.CountBusinessDays(stageStartDate.Value, today)
                        : 0;

                    result.Add(new ProcessPatientSnapshot
                    {
                        PatientId = row.SitraMedId,
                        SitraMedGuid = string.IsNullOrWhiteSpace(row.SitraMedGuid) ? null : row.SitraMedGuid,
                        AssignedPhysicist = NormalizePhysicist(row.AssignedPhysicist),
                        TreatmentType = TreatmentClassifier.Classify(row.TreatmentZone),
                        TreatmentTechnique = TreatmentClassifier.Classify(row.TreatmentZone),
                        PatientName = row.PatientName,
                        CenterName = row.CenterName,
                        StageCode = stageDefinition.Code,
                        StageDisplayName = stageDefinition.DisplayName,
                        StageGroupName = stageDefinition.GroupName,
                        StageStartDate = stageStartDate,
                        TomographyDate = row.TomographyDate,
                        ResponsibleDoctor = row.ResponsibleDoctor,
                        PostponedUntil = row.PostponedUntil,
                        ExpectantStartDate = row.ExpectantStartDate,
                        ExpectantObservations = row.ExpectantObservations,
                        ExpectantUser = row.ExpectantUser,
                        PvAppointmentDate = row.PvAppointmentDate,
                        DaysInStage = daysInStage,
                        ExpectedDaysInStage = stageDefinition.ExpectedDays,
                        IsDelayed = daysInStage > stageDefinition.ExpectedDays,
                        SourceCenterName = row.CenterName,
                        Priority = row.Priority
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
                var tomographyDate = FollowUpDateParser.ExtractTomographyDate(segment);
                var responsibleDoctor = FollowUpDateParser.ExtractResponsibleDoctor(segment);
                var postponedUntil = FollowUpDateParser.ExtractPostponedUntil(segment);
                var expectantStartDate = FollowUpDateParser.ExtractExpectantStartDate(segment);
                var expectantObservations = FollowUpDateParser.ExtractExpectantObservations(segment);
                var expectantUser = FollowUpDateParser.ExtractExpectantUser(segment);
                var pvAppointmentDate = FollowUpDateParser.ExtractPvAppointmentDate(segment);

                if (string.IsNullOrWhiteSpace(patientName) || string.IsNullOrWhiteSpace(patientId))
                {
                    continue;
                }

                var today = DateOnly.FromDateTime(DateTime.Today);
                var daysInStage = stageStartDate.HasValue
                    ? _bdCalc.CountBusinessDays(stageStartDate.Value, today)
                    : 0;

                result.Add(new ProcessPatientSnapshot
                {
                    PatientId = patientId,
                    SitraMedGuid = sitraMedGuid,
                    AssignedPhysicist = NormalizePhysicist(assignedPhysicist),
                    TreatmentType = TreatmentClassifier.Classify(treatmentZone),
                    TreatmentTechnique = TreatmentClassifier.Classify(treatmentZone),
                    PatientName = patientName,
                    CenterName = centerName,
                    StageCode = stageDefinition.Code,
                    StageDisplayName = stageDefinition.DisplayName,
                    StageGroupName = stageDefinition.GroupName,
                    StageStartDate = stageStartDate,
                    TomographyDate = tomographyDate,
                    ResponsibleDoctor = responsibleDoctor,
                    PostponedUntil = postponedUntil,
                    ExpectantStartDate = expectantStartDate,
                    ExpectantObservations = expectantObservations,
                    ExpectantUser = expectantUser,
                    PvAppointmentDate = pvAppointmentDate,
                    DaysInStage = daysInStage,
                    ExpectedDaysInStage = stageDefinition.ExpectedDays,
                    IsDelayed = daysInStage > stageDefinition.ExpectedDays,
                    SourceCenterName = centerName,
                    Priority = int.TryParse(match.Groups["priority"].Value, out var pp) ? pp : (int?)null
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

    private static string? NormalizePhysicist(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // El select muestra "-- Seleccione una opción --" cuando no hay físico asignado
        if (value.Contains("Seleccione") || value.StartsWith("--")) return null;
        return value;
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
        if (matches.Count == 1) return DecodeAndStrip(matches[0].Groups["zone"].Value);

        var zones = matches
            .Select(m => (zone: DecodeAndStrip(m.Groups["zone"].Value),
                          tech: TreatmentClassifier.Classify(DecodeAndStrip(m.Groups["zone"].Value)),
                          date: ExtractRowDate(segment, m.Index)))
            .ToList();

        var candidates = zones
            .Where(x => x.tech != "BQT" && x.tech != "IORT")
            .OrderByDescending(x => x.date ?? DateOnly.MinValue)
            .ToList();

        if (candidates.Count > 0) return candidates[0].zone;

        // Fallback: BQT/IORT — still pick most recent
        return zones.OrderByDescending(x => x.date ?? DateOnly.MinValue).First().zone;
    }

    private static readonly Regex RowDateRegex = new(@"\d{2}/\d{2}/\d{4}", RegexOptions.Compiled);

    private static DateOnly? ExtractRowDate(string segment, int matchIndex)
    {
        var trStart = segment.LastIndexOf("<tr", matchIndex, StringComparison.OrdinalIgnoreCase);
        if (trStart < 0) return null;

        var td1 = segment.IndexOf("<td", trStart, StringComparison.OrdinalIgnoreCase);
        if (td1 < 0) return null;
        var td2 = segment.IndexOf("<td", td1 + 3, StringComparison.OrdinalIgnoreCase);
        if (td2 < 0) return null;

        var closeTd2 = segment.IndexOf("</td>", td2, StringComparison.OrdinalIgnoreCase);
        if (closeTd2 < 0) return null;

        var dateCell = segment.Substring(td2, closeTd2 - td2);
        var dateMatch = RowDateRegex.Match(dateCell);
        if (!dateMatch.Success) return null;

        return DateOnly.TryParseExact(dateMatch.Value, "dd/MM/yyyy",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
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

public sealed class SitraMedAttendedPatientsExtractor : IAttendedPatientsExtractor
{
    private static readonly Regex RowRegex = new(
        @"<tr[^>]*>(?<row>.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AttendedBtnRegex = new(
        @"<button[^>]*>\s*Atendido\s*</button>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex GuidRegex = new(
        @"medical_histories/(?<guid>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/overview",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly PlaywrightSitraMedClient _client;

    public SitraMedAttendedPatientsExtractor(PlaywrightSitraMedClient client)
    {
        _client = client;
    }

    public async Task<IReadOnlySet<string>> ExtractAttendedGuidsAsync(
        string centerName, string machineSitraName, DateOnly date, CancellationToken cancellationToken)
    {
        var html = await _client.DownloadAgendaPageHtmlForMachineAsync(centerName, machineSitraName, date, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            Console.Error.WriteLine($"[AttendedExtractor] HTML vacío para {centerName}/{machineSitraName}/{date}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        return ParseAttendedGuids(html);
    }

    internal static IReadOnlySet<string> ParseAttendedGuids(string html)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match rowMatch in RowRegex.Matches(html))
        {
            var row = rowMatch.Groups["row"].Value;
            if (!AttendedBtnRegex.IsMatch(row)) continue;
            var guidMatch = GuidRegex.Match(row);
            if (guidMatch.Success)
                result.Add(guidMatch.Groups["guid"].Value);
            else
                Console.Error.WriteLine("[AttendedExtractor] Fila con Atendido sin GUID extraíble");
        }
        return result;
    }
}
