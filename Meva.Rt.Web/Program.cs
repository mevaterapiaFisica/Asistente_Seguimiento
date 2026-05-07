using System.Text.Json;
using Meva.Rt.Application;
using Meva.Rt.Core;
using Meva.Rt.Infrastructure.Aria;
using Meva.Rt.Infrastructure.SitraMed;
using Meva.Rt.Infrastructure.Storage;
using Meva.Rt.Web;

var builder = WebApplication.CreateBuilder(args);

var snapshotsDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
var configurationHolder = new RtConfigurationHolder(builder.Environment.ContentRootPath);

var sitraMedOptions = new SitraMedRuntimeOptions
{
    Username = Environment.GetEnvironmentVariable("MEVA_SITRAMED_USER") ?? string.Empty,
    Password = Environment.GetEnvironmentVariable("MEVA_SITRAMED_PASSWORD") ?? string.Empty,
    Headless = !string.Equals(Environment.GetEnvironmentVariable("MEVA_SITRAMED_HEADFUL"), "true", StringComparison.OrdinalIgnoreCase),
    UseLocalExamplesFallback = !string.Equals(Environment.GetEnvironmentVariable("MEVA_SITRAMED_NO_FALLBACK"), "true", StringComparison.OrdinalIgnoreCase),
    TimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("MEVA_SITRAMED_TIMEOUT_SECONDS"), out var timeoutSeconds) ? timeoutSeconds : 30,
    SaveAgendaHtmlCapture = string.Equals(Environment.GetEnvironmentVariable("MEVA_SITRAMED_SAVE_AGENDA_HTML"), "true", StringComparison.OrdinalIgnoreCase),
    AgendaHtmlCaptureDirectory = Path.Combine(builder.Environment.ContentRootPath, "data", "agenda-captures"),
    EnableDiagnostics = string.Equals(Environment.GetEnvironmentVariable("MEVA_SITRAMED_DIAGNOSTICS"), "true", StringComparison.OrdinalIgnoreCase),
    DiagnosticsDirectory = Path.Combine(builder.Environment.ContentRootPath, "data", "diagnostics")
};

var ariaMapDefaultPath = Path.Combine(AppContext.BaseDirectory, "config", "mapEquiposAriaSitra.txt");
var ariaOptions = new AriaRuntimeOptions
{
    MapFilePath = Environment.GetEnvironmentVariable("MEVA_ARIA_MAP_PATH") ?? ariaMapDefaultPath,
    MockPlansJsonPath = Environment.GetEnvironmentVariable("MEVA_ARIA_MOCK_JSON")
                        ?? Path.Combine(builder.Environment.ContentRootPath, "data", "aria_plans_mock.json")
};

var homeSnapshotOptions = new HomeSnapshotOptions
{
    RefreshMode = Environment.GetEnvironmentVariable("MEVA_HOME_REFRESH_MODE") ?? "snapshot_first"
};

// Business day calculator — looks for feriados.txt next to the data directory
var feriadosPath = Environment.GetEnvironmentVariable("MEVA_FERIADOS_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "feriados.txt");
var businessDayCalc = BusinessDayCalculator.FromFile(feriadosPath);

builder.Services.AddSingleton(configurationHolder);
builder.Services.AddSingleton<IRtSystemConfigurationProvider>(_ => configurationHolder);
builder.Services.AddSingleton(sitraMedOptions);
builder.Services.AddSingleton(ariaOptions);
builder.Services.AddSingleton(homeSnapshotOptions);
builder.Services.AddSingleton(businessDayCalc);
builder.Services.AddSingleton<ISnapshotStore>(_ => new JsonSnapshotStore(snapshotsDirectory));
builder.Services.AddSingleton<PlaywrightSitraMedClient>();
builder.Services.AddSingleton<IAgendaExtractor, SitraMedAgendaExtractor>();
builder.Services.AddSingleton<IFollowUpExtractor, SitraMedFollowUpExtractor>();
builder.Services.AddSingleton<IAriaPatientRootProvider, NullAriaPatientRootProvider>();
builder.Services.AddSingleton<IAriaPlanResolver, AriaPlanResolver>();
builder.Services.AddSingleton<BootstrapService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ─── Dashboard ───────────────────────────────────────────────────────────────

app.MapGet("/api/home", async Task<IResult> (
        BootstrapService bootstrapService,
        ISnapshotStore snapshotStore,
        IRtSystemConfigurationProvider configurationProvider,
        HomeSnapshotOptions snapshotOptions,
        CancellationToken cancellationToken) =>
{
    var mode = snapshotOptions.RefreshMode.Trim().ToLowerInvariant();
    DashboardBootstrapData? data = mode switch
    {
        "snapshot_only" => await snapshotStore.TryLoadAsync<DashboardBootstrapData>("dashboard_bootstrap", cancellationToken),
        "snapshot_first" => await snapshotStore.TryLoadAsync<DashboardBootstrapData>("dashboard_bootstrap", cancellationToken)
                           ?? await bootstrapService.BuildAsync(cancellationToken),
        _ => await bootstrapService.BuildAsync(cancellationToken)
    };

    if (data == null)
        return TypedResults.Problem("No hay snapshot dashboard_bootstrap.json en modo snapshot_only.", statusCode: 503);

    return TypedResults.Ok(HomeResponseMapper.Map(data, configurationProvider));
});

app.MapPost("/api/home/refresh", async Task<IResult> (
        BootstrapService bootstrapService,
        IRtSystemConfigurationProvider configurationProvider,
        CancellationToken cancellationToken) =>
{
    var data = await bootstrapService.BuildAsync(cancellationToken);
    return TypedResults.Ok(HomeResponseMapper.Map(data, configurationProvider));
});

app.MapPost("/api/home/refresh-no-aria", async Task<IResult> (
        BootstrapService bootstrapService,
        IRtSystemConfigurationProvider configurationProvider,
        CancellationToken cancellationToken) =>
{
    var data = await bootstrapService.BuildAsync(cancellationToken, skipAria: true);
    return TypedResults.Ok(HomeResponseMapper.Map(data, configurationProvider));
});

// ─── Configuration ────────────────────────────────────────────────────────────

app.MapGet("/api/configuration", (RtConfigurationHolder holder) => Results.Ok(holder.Configuration));

app.MapPut("/api/configuration", (RtConfigurationHolder holder, RtSystemConfiguration body) =>
{
    holder.Save(body);
    return Results.Ok(holder.Configuration);
});

// ─── Scraping tests ───────────────────────────────────────────────────────────

app.MapPost("/api/scraping/test", async Task<IResult> (
        PlaywrightSitraMedClient client,
        RtConfigurationHolder holder,
        CancellationToken cancellationToken) =>
{
    var center = holder.Configuration.Centers.FirstOrDefault();
    var stage = holder.Configuration.Stages.FirstOrDefault(x => x.Enabled);
    if (center == null || stage == null)
        return TypedResults.BadRequest(new ScrapingTestResult { Success = false, Message = "No hay centro o etapa configurados." });

    try
    {
        return TypedResults.Ok(await client.RunFollowUpTestAsync(center, stage, cancellationToken));
    }
    catch (Exception ex)
    {
        return TypedResults.Ok(new ScrapingTestResult { Success = false, Message = ex.Message });
    }
});

app.MapPost("/api/scraping/test-agenda", async Task<IResult> (
        PlaywrightSitraMedClient client,
        RtConfigurationHolder holder,
        string? machine,
        DateOnly? date,
        CancellationToken cancellationToken) =>
{
    var selectedMachine = string.IsNullOrWhiteSpace(machine)
        ? holder.Configuration.Machines.FirstOrDefault()
        : holder.Configuration.Machines.FirstOrDefault(x =>
            string.Equals(x.DisplayName, machine, StringComparison.OrdinalIgnoreCase));
    if (selectedMachine == null)
        return TypedResults.BadRequest(new ScrapingTestResult { Success = false, Message = "Equipo invalido o no configurado." });

    try
    {
        return TypedResults.Ok(await client.RunAgendaTestAsync(selectedMachine, date ?? DateOnly.FromDateTime(DateTime.Today), cancellationToken));
    }
    catch (Exception ex)
    {
        return TypedResults.Ok(new ScrapingTestResult { Success = false, Message = ex.Message });
    }
});

app.MapPost("/api/scraping/test-followup-full", async Task<IResult> (
        PlaywrightSitraMedClient client,
        RtConfigurationHolder holder,
        CancellationToken cancellationToken) =>
{
    var cfg = holder.Configuration;
    if (cfg.Centers.Count == 0 || !cfg.Stages.Any(x => x.Enabled))
        return TypedResults.BadRequest(new { error = "No hay centros o fases habilitadas configurados." });

    try
    {
        var snapshots = await client.DownloadFollowUpPagesAsync(cfg.Centers, cfg.Stages.Where(x => x.Enabled).ToList(), cancellationToken);
        var summary = snapshots.Select(s => new
        {
            Center = s.CenterName,
            Stage = s.StageCode,
            MicroStatus = s.StageMicroStatus,
            DomRowsExtracted = s.DomRows?.Count ?? 0,
            UsedFallbackRegex = s.DomRows == null,
            HtmlLength = s.Html.Length,
            Patients = s.DomRows?.Select(r => new { r.PatientName, r.SitraMedId, r.FirstConsultDate, r.Institution, r.DoctorHc })
        }).ToList();
        return TypedResults.Ok(summary);
    }
    catch (Exception ex)
    {
        return TypedResults.Ok(new { error = ex.Message });
    }
});

// ─── ARIA ─────────────────────────────────────────────────────────────────────

app.MapGet("/api/aria/export-patient-ids", async Task<IResult> (
        IFollowUpExtractor followUpExtractor,
        CancellationToken cancellationToken) =>
{
    var patients = await followUpExtractor.ExtractAsync(cancellationToken);
    var ids = patients
        .Select(p => p.PatientId)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(id => id)
        .ToList();
    return TypedResults.Ok(new { patientIds = ids, count = ids.Count });
});

app.MapPost("/api/aria/import-results", async Task<IResult> (
        IRtSystemConfigurationProvider configurationProvider,
        AriaRuntimeOptions ariaOptions,
        string? filePath,
        CancellationToken cancellationToken) =>
{
    var resolvedPath = filePath;
    if (string.IsNullOrWhiteSpace(resolvedPath))
    {
        if (!Directory.Exists(snapshotsDirectory))
            return TypedResults.BadRequest(new { error = "Directorio data/ no existe." });

        resolvedPath = Directory.GetFiles(snapshotsDirectory, "aria_results_*.json")
            .OrderByDescending(f => f)
            .FirstOrDefault();

        if (resolvedPath == null)
            return TypedResults.BadRequest(new
            {
                error = $"No se encontro ningun archivo aria_results_*.json en {snapshotsDirectory}."
            });
    }

    if (!File.Exists(resolvedPath))
        return TypedResults.BadRequest(new { error = $"Archivo no encontrado: {resolvedPath}" });

    AriaRunnerOutput? runnerOutput;
    try
    {
        var json = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
        runnerOutput = JsonSerializer.Deserialize<AriaRunnerOutput>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (Exception ex)
    {
        return TypedResults.BadRequest(new { error = $"Error leyendo {resolvedPath}: {ex.Message}" });
    }

    if (runnerOutput?.Patients == null)
        return TypedResults.BadRequest(new { error = "Archivo vacio o formato invalido." });

    var machines = configurationProvider.Configuration.Machines;
    var plans = new List<AriaPlanSnapshot>();
    var withMachine = 0;

    foreach (var patient in runnerOutput.Patients)
    {
        if (!patient.Found || patient.ActivePlan == null) continue;

        var snap = new AriaPlanSnapshot
        {
            PatientId = patient.PatientId,
            PlannedMachineAriaId = patient.ActivePlan.MachineAriaId,
            PlanStatus = patient.ActivePlan.Status
        };

        if (!string.IsNullOrWhiteSpace(patient.ActivePlan.MachineAriaId))
        {
            var machine = machines.FirstOrDefault(m =>
                string.Equals(m.AriaName, patient.ActivePlan.MachineAriaId, StringComparison.OrdinalIgnoreCase));
            snap.PlannedMachineDisplayName = machine?.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(snap.PlannedMachineDisplayName))
            withMachine++;

        plans.Add(snap);
    }

    var mockPath = ariaOptions.MockPlansJsonPath;
    if (string.IsNullOrWhiteSpace(mockPath))
        return TypedResults.Problem("MockPlansJsonPath no configurado.", statusCode: 500);

    Directory.CreateDirectory(Path.GetDirectoryName(mockPath)!);
    await File.WriteAllTextAsync(mockPath,
        JsonSerializer.Serialize(plans, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        cancellationToken);

    return TypedResults.Ok(new
    {
        importedFile = Path.GetFileName(resolvedPath),
        totalInFile = runnerOutput.Patients.Count,
        withActivePlan = plans.Count,
        withMachineResolved = withMachine,
        savedTo = Path.GetFileName(mockPath)
    });
});

// ─── Agenda ───────────────────────────────────────────────────────────────────

// Returns sorted list of dates that have a stored agenda snapshot.
app.MapGet("/api/agenda/available-dates", () =>
{
    if (!Directory.Exists(snapshotsDirectory))
        return Results.Ok(Array.Empty<string>());

    var dates = Directory.GetFiles(snapshotsDirectory, "agenda_????-??-??.json")
        .Select(f => Path.GetFileNameWithoutExtension(f).Replace("agenda_", ""))
        .Where(d => DateOnly.TryParse(d, out _))
        .Order()
        .ToArray();

    return Results.Ok(dates);
});

// Scrapes the next N business days of agenda and stores each as a per-date snapshot.
// Called by the Task Scheduler; can take several minutes.
app.MapPost("/api/agenda/scrape-upcoming", async Task<IResult> (
        IAgendaExtractor agendaExtractor,
        ISnapshotStore snapshotStore,
        BusinessDayCalculator bdCalc,
        int? days,
        CancellationToken cancellationToken) =>
{
    var count = Math.Max(1, Math.Min(days ?? 15, 30));
    var today = DateOnly.FromDateTime(DateTime.Today);
    var upcoming = bdCalc.GetUpcomingBusinessDays(today, count);

    var allItems = await agendaExtractor.ExtractForDatesAsync(upcoming, cancellationToken);

    var saved = new List<string>();
    foreach (var (date, items) in allItems.OrderBy(kv => kv.Key))
    {
        await snapshotStore.SaveAsync($"agenda_{date:yyyy-MM-dd}", items.ToList(), cancellationToken);
        saved.Add(date.ToString("yyyy-MM-dd"));
    }

    return TypedResults.Ok(new { savedDates = saved, totalDays = saved.Count });
});

// Returns agenda for a specific date. Enriches future dates with estimated appointments
// projected from follow-up patients (based on remaining stage time + planned machine).
app.MapGet("/api/agenda", async Task<IResult> (
        IAgendaExtractor agendaExtractor,
        ISnapshotStore snapshotStore,
        BusinessDayCalculator bdCalc,
        IRtSystemConfigurationProvider configProvider,
        DateOnly? date,
        CancellationToken cancellationToken) =>
{
    var targetDate = date ?? DateOnly.FromDateTime(DateTime.Today);
    var today = DateOnly.FromDateTime(DateTime.Today);

    IReadOnlyList<MachineAppointmentSnapshot> scraped;

    if (targetDate == today)
    {
        var cached = await snapshotStore.TryLoadAsync<DashboardBootstrapData>("dashboard_bootstrap", cancellationToken);
        scraped = cached?.AgendaItems ?? [];
    }
    else
    {
        var stored = await snapshotStore.TryLoadAsync<List<MachineAppointmentSnapshot>>($"agenda_{targetDate:yyyy-MM-dd}", cancellationToken);
        if (stored != null)
        {
            scraped = stored;
        }
        else if (targetDate > today)
        {
            try
            {
                scraped = await agendaExtractor.ExtractForDateAsync(targetDate, cancellationToken);
            }
            catch (Exception ex)
            {
                return TypedResults.Problem($"Error al obtener agenda para {targetDate}: {ex.Message}", statusCode: 500);
            }
        }
        else
        {
            scraped = [];
        }
    }

    var slots = scraped.Select(s => new AgendaSlotDto(s)).ToList();

    // Append estimated appointments for future dates
    if (targetDate > today)
    {
        var bootstrap = await snapshotStore.TryLoadAsync<DashboardBootstrapData>("dashboard_bootstrap", cancellationToken);
        if (bootstrap != null)
        {
            var stages = configProvider.Configuration.Stages.OrderBy(s => s.SortOrder).ToList();

            foreach (var patient in bootstrap.FollowUpPatients)
            {
                if (string.IsNullOrWhiteSpace(patient.PlannedMachineDisplayName)) continue;

                var stageIdx = stages.FindIndex(s =>
                    string.Equals(s.Code, patient.StageCode, StringComparison.OrdinalIgnoreCase));
                if (stageIdx < 0) continue;

                var remainingDays = stages.Skip(stageIdx).Sum(s => s.ExpectedDays);
                var daysToStart = Math.Max(remainingDays, 1);
                var estimatedStart = bdCalc.AddBusinessDays(today, daysToStart);

                if (estimatedStart == targetDate)
                {
                    slots.Add(new AgendaSlotDto
                    {
                        CenterName = patient.CenterName ?? string.Empty,
                        MachineName = patient.PlannedMachineDisplayName,
                        PatientName = patient.PatientName,
                        AgendaDate = targetDate.ToString("yyyy-MM-dd"),
                        Treatment = patient.StageDisplayName,
                        IsEstimated = true,
                        EstimatedFromStage = $"{patient.StageCode} - {patient.StageDisplayName}",
                        EstimatedPatientId = patient.PatientId
                    });
                }
            }
        }
    }

    return TypedResults.Ok(slots);
});

// ─── App run ──────────────────────────────────────────────────────────────────

app.Run();
