using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meva.Rt.Application;
using Meva.Rt.Core;
using Meva.Rt.Infrastructure.Aria;
using Meva.Rt.Infrastructure.Mail;
using Meva.Rt.Infrastructure.SitraMed;
using Meva.Rt.Infrastructure.Storage;
using Meva.Rt.Web;

var builder = WebApplication.CreateBuilder(args);

var snapshotsDirectory = Environment.GetEnvironmentVariable("MEVA_DATA_DIR")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var configurationHolder = new RtConfigurationHolder(snapshotsDirectory);

var sitraMedOptions = new SitraMedRuntimeOptions
{
    Username = Environment.GetEnvironmentVariable("MEVA_SITRAMED_USER") ?? string.Empty,
    Password = Environment.GetEnvironmentVariable("MEVA_SITRAMED_PASSWORD") ?? string.Empty,
    Headless = !string.Equals(Environment.GetEnvironmentVariable("MEVA_SITRAMED_HEADFUL"), "true", StringComparison.OrdinalIgnoreCase),
    UseLocalExamplesFallback = !string.Equals(Environment.GetEnvironmentVariable("MEVA_SITRAMED_NO_FALLBACK"), "true", StringComparison.OrdinalIgnoreCase),
    TimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("MEVA_SITRAMED_TIMEOUT_SECONDS"), out var timeoutSeconds) ? timeoutSeconds : 30,
    SaveAgendaHtmlCapture = string.Equals(Environment.GetEnvironmentVariable("MEVA_SITRAMED_SAVE_AGENDA_HTML"), "true", StringComparison.OrdinalIgnoreCase),
    AgendaHtmlCaptureDirectory = Path.Combine(snapshotsDirectory, "agenda-captures"),
    EnableDiagnostics = string.Equals(Environment.GetEnvironmentVariable("MEVA_SITRAMED_DIAGNOSTICS"), "true", StringComparison.OrdinalIgnoreCase),
    DiagnosticsDirectory = Path.Combine(snapshotsDirectory, "diagnostics")
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

var tbiMailOptions = new TbiMailOptions
{
    User = Environment.GetEnvironmentVariable("MEVA_TBI_MAIL_USER") ?? string.Empty,
    AppPassword = Environment.GetEnvironmentVariable("MEVA_TBI_MAIL_APP_PASSWORD") ?? string.Empty,
    Folder = Environment.GetEnvironmentVariable("MEVA_TBI_MAIL_FOLDER") ?? "INBOX",
    DiagnosticsPath = Path.Combine(snapshotsDirectory, "tbi_mail_unparsed.txt")
};

// Business day calculator — looks for feriados.txt next to the data directory
var feriadosPath = Environment.GetEnvironmentVariable("MEVA_FERIADOS_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "feriados.txt");
var businessDayCalc = BusinessDayCalculator.FromFile(feriadosPath);

builder.Services.AddWindowsService(options => options.ServiceName = "MevaRT");
builder.Services.AddSingleton(configurationHolder);
builder.Services.AddSingleton<IRtSystemConfigurationProvider>(_ => configurationHolder);
builder.Services.AddSingleton(sitraMedOptions);
builder.Services.AddSingleton(ariaOptions);
builder.Services.AddSingleton(homeSnapshotOptions);
builder.Services.AddSingleton(businessDayCalc);
builder.Services.AddSingleton<ISnapshotStore>(_ => new JsonSnapshotStore(snapshotsDirectory));
builder.Services.AddSingleton<IStageTransitionStore>(_ => new StageTransitionStore(snapshotsDirectory));
builder.Services.AddSingleton<IWeeklyStatsStore>(_ => new WeeklyStatsStore(snapshotsDirectory));
builder.Services.AddSingleton<IPatientProcessEventStore>(_ => new PatientProcessEventStore(snapshotsDirectory));
builder.Services.AddSingleton<AriaJobState>();
builder.Services.AddSingleton<PlaywrightSitraMedClient>();
builder.Services.AddSingleton<IAgendaExtractor, SitraMedAgendaExtractor>();
builder.Services.AddSingleton<ITomographAgendaExtractor, SitraMedTomographExtractor>();
builder.Services.AddSingleton<IFollowUpExtractor, SitraMedFollowUpExtractor>();
builder.Services.AddSingleton<IAriaPatientRootProvider, NullAriaPatientRootProvider>();
builder.Services.AddSingleton<IAriaPlanResolver, AriaPlanResolver>();
builder.Services.AddSingleton<IPatientHcResolver, SitraMedPatientHcFetcher>();
builder.Services.AddSingleton<IPatientPhoneResolver, SitraMedPatientPhoneFetcher>();
builder.Services.AddSingleton<IAttendedPatientsExtractor, SitraMedAttendedPatientsExtractor>();
builder.Services.AddSingleton<BootstrapService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(_ => new TurnReservationStore(snapshotsDirectory, businessDayCalc));
builder.Services.AddSingleton(_ => new PedidoStore(snapshotsDirectory));
builder.Services.AddSingleton(_ => new QaEspecificoStore(snapshotsDirectory));
builder.Services.AddSingleton(_ => new TbiMailStore(snapshotsDirectory));
builder.Services.AddSingleton(_ => new TbiDoseStore(snapshotsDirectory));
builder.Services.AddSingleton(tbiMailOptions);
builder.Services.AddSingleton<TbiMailClient>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name == "index.html")
            ctx.Context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    }
});

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

app.MapGet("/api/status", async (ISnapshotStore snapshotStore, CancellationToken ct) =>
{
    var data = await snapshotStore.TryLoadAsync<DashboardBootstrapData>("dashboard_bootstrap", ct);
    var appJsPath = Path.Combine(app.Environment.WebRootPath, "app.js");
    var appVersion = File.Exists(appJsPath)
        ? new DateTimeOffset(File.GetLastWriteTimeUtc(appJsPath), TimeSpan.Zero).ToUnixTimeSeconds().ToString()
        : "0";
    return Results.Ok(new { generatedAtUtc = data?.GeneratedAtUtc, appVersion });
});

app.MapPost("/api/home/refresh", async Task<IResult> (
        BootstrapService bootstrapService,
        IRtSystemConfigurationProvider configurationProvider,
        TurnReservationStore reservationStore,
        CancellationToken cancellationToken) =>
{
    var data = await bootstrapService.BuildAsync(cancellationToken);
    await reservationStore.PruneExpiredAsync(2, cancellationToken);
    return TypedResults.Ok(HomeResponseMapper.Map(data, configurationProvider));
});

app.MapPost("/api/home/refresh-no-aria", async Task<IResult> (
        BootstrapService bootstrapService,
        IRtSystemConfigurationProvider configurationProvider,
        ISnapshotStore snapshotStore,
        CancellationToken cancellationToken) =>
{
    var data = await bootstrapService.BuildAsync(cancellationToken, skipAria: true);

    // Solo incluir pacientes desde Planificacion (F6A) en adelante — los anteriores no tienen planes ARIA
    var stageMap = configurationProvider.Configuration.Stages
        .ToDictionary(s => s.Code, s => s.SortOrder, StringComparer.OrdinalIgnoreCase);
    var ariaThreshold = configurationProvider.Configuration.Stages
        .Where(s => string.Equals(s.GroupName, "Planificación", StringComparison.OrdinalIgnoreCase))
        .Select(s => (int?)s.SortOrder)
        .Min() ?? 40;

    var guidHcMapNoAria = await snapshotStore.TryLoadAsync<Dictionary<string, string>>("guid_hc_map", cancellationToken)
                          ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    var followUpExportIds = data.FollowUpPatients
        .Where(p => stageMap.TryGetValue(p.StageCode, out var so) && so >= ariaThreshold)
        .Select(p => p.PatientId)
        .Where(id => !string.IsNullOrWhiteSpace(id));

    var agendaExportIds = data.AgendaItems
        .Where(a => !string.IsNullOrWhiteSpace(a.SitraMedGuid) && guidHcMapNoAria.ContainsKey(a.SitraMedGuid!))
        .Select(a => guidHcMapNoAria[a.SitraMedGuid!])
        .Where(id => !string.IsNullOrWhiteSpace(id));

    var baseExportIds = followUpExportIds
        .Concat(agendaExportIds)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    // SitraMed sufija el HC por seguimiento/curso concurrente (ej. "1-114893-1"); ARIA guarda al
    // paciente bajo un único PatientId (típicamente sufijo "-0"). Exportamos también esa variante.
    var ids = baseExportIds
        .Concat(baseExportIds.Select(BootstrapService.NormalizeAriaBaseId).Where(id => !baseExportIds.Contains(id)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(id => id)
        .ToList();
    await snapshotStore.SaveAsync("pacientes", new { patientIds = ids }, cancellationToken);

    return TypedResults.Ok(HomeResponseMapper.Map(data, configurationProvider));
});

// ─── Stats ────────────────────────────────────────────────────────────────────

app.MapGet("/api/stats/weekly", async Task<IResult> (
        IWeeklyStatsStore weeklyStatsStore,
        CancellationToken cancellationToken) =>
    TypedResults.Ok(await weeklyStatsStore.LoadAsync()));

app.MapGet("/api/stats/transitions", async Task<IResult> (
        IStageTransitionStore stageTransitionStore,
        CancellationToken cancellationToken) =>
{
    var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-90);
    var events = (await stageTransitionStore.LoadAsync())
        .Where(e => e.StageEndDate >= cutoff)
        .OrderByDescending(e => e.StageEndDate)
        .ToList();
    return TypedResults.Ok(events);
});

app.MapGet("/api/patient-events", async Task<IResult> (
        IPatientProcessEventStore eventStore,
        int? days,
        string? type,
        string? center,
        CancellationToken cancellationToken) =>
{
    var lookback = Math.Max(1, Math.Min(days ?? 30, 365));
    var events = await eventStore.LoadRecentAsync(lookback, cancellationToken);

    if (!string.IsNullOrWhiteSpace(type))
        events = events.Where(e => string.Equals(e.EventType.ToString(), type, StringComparison.OrdinalIgnoreCase)).ToList();

    if (!string.IsNullOrWhiteSpace(center))
        events = events.Where(e => string.Equals(e.CenterName, center, StringComparison.OrdinalIgnoreCase)).ToList();

    return TypedResults.Ok(events.OrderByDescending(e => e.DetectedAtUtc).ToList());
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

app.MapPost("/api/scraping/test-tomograph", async Task<IResult> (
        PlaywrightSitraMedClient client,
        RtConfigurationHolder holder,
        string? centerName,
        DateOnly? date,
        CancellationToken cancellationToken) =>
{
    var tomograph = string.IsNullOrWhiteSpace(centerName)
        ? holder.Configuration.Tomographs.FirstOrDefault()
        : holder.Configuration.Tomographs.FirstOrDefault(t =>
            string.Equals(t.CenterName, centerName, StringComparison.OrdinalIgnoreCase));
    if (tomograph == null)
        return TypedResults.BadRequest(new ScrapingTestResult { Success = false, Message = "Centro o tomografo no encontrado. Centros disponibles: " + string.Join(", ", holder.Configuration.Tomographs.Select(t => t.CenterName)) });

    try
    {
        return TypedResults.Ok(await client.RunTomographTestAsync(tomograph, date ?? DateOnly.FromDateTime(DateTime.Today), cancellationToken));
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
        ISnapshotStore snapshotStore,
        CancellationToken cancellationToken) =>
{
    var snapshot = await snapshotStore.TryLoadAsync<DashboardBootstrapData>("dashboard_bootstrap", cancellationToken);
    if (snapshot == null)
        return TypedResults.Problem("No hay snapshot disponible. Ejecutá /api/home/refresh primero.");

    var guidHcMapExport = await snapshotStore.TryLoadAsync<Dictionary<string, string>>("guid_hc_map", cancellationToken)
                          ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    var followUpExport = snapshot.FollowUpPatients
        .Select(p => p.PatientId)
        .Where(id => !string.IsNullOrWhiteSpace(id));

    var agendaExport = snapshot.AgendaItems
        .Where(a => !string.IsNullOrWhiteSpace(a.SitraMedGuid) && guidHcMapExport.ContainsKey(a.SitraMedGuid!))
        .Select(a => guidHcMapExport[a.SitraMedGuid!])
        .Where(id => !string.IsNullOrWhiteSpace(id));

    var baseFollowUpIds = followUpExport
        .Concat(agendaExport)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    var ids = baseFollowUpIds
        .Concat(baseFollowUpIds.Select(BootstrapService.NormalizeAriaBaseId).Where(id => !baseFollowUpIds.Contains(id)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(id => id)
        .ToList();
    return TypedResults.Ok(new { patientIds = ids, count = ids.Count, snapshotAge = DateTime.UtcNow - snapshot.GeneratedAtUtc });
});

// NOTA: Los campos IrradiationModality y ExactBeamEnergy en el JSON importado requieren
// AriaRunner versión actual (que incluye Modalidad() y DetermineExactBeamEnergy()).
// Si esos campos llegan null, el runner es una versión anterior y hay que recompilarlo y re-ejecutarlo.
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

    var plans = ParseAriaOutput(runnerOutput, configurationProvider.Configuration.Machines);
    var withMachine = plans.Count(p => !string.IsNullOrWhiteSpace(p.PlannedMachineDisplayName));

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

app.MapPost("/api/home/apply-aria", async Task<IResult> (
        ISnapshotStore snapshotStore,
        IAriaPlanResolver ariaPlanResolver,
        IRtSystemConfigurationProvider configurationProvider,
        CancellationToken cancellationToken) =>
{
    var data = await snapshotStore.TryLoadAsync<DashboardBootstrapData>("dashboard_bootstrap", cancellationToken);
    if (data == null)
        return TypedResults.Problem("No hay snapshot. Ejecutá refresh-no-aria primero.", statusCode: 503);

    var guidHcMapApply = await snapshotStore.TryLoadAsync<Dictionary<string, string>>("guid_hc_map", cancellationToken)
                         ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (var p in data.FollowUpPatients) { p.PlannedMachineDisplayName = null; p.BeamType = null; p.NumberOfFractions = null; p.IrradiationModality = null; p.ExactBeamEnergy = null; }
    foreach (var a in data.AgendaItems) { a.BeamType = null; a.IrradiationModality = null; }

    var followUpIdSet = data.FollowUpPatients
        .Select(p => p.PatientId)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Agenda-pura: pacientes en agenda que NO están en seguimiento — se leen de mock.json
    var agendaOnlyIds = data.AgendaItems
        .Where(a => !string.IsNullOrWhiteSpace(a.SitraMedGuid) && guidHcMapApply.ContainsKey(a.SitraMedGuid!))
        .Select(a => guidHcMapApply[a.SitraMedGuid!])
        .Where(id => !string.IsNullOrWhiteSpace(id) && !followUpIdSet.Contains(id))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    var baseIds = followUpIdSet.Concat(agendaOnlyIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    // SitraMed sufija el HC por seguimiento/curso concurrente (ej. "1-114893-1"); ARIA guarda al
    // paciente bajo un único PatientId (típicamente sufijo "-0"). Consultamos también esa variante.
    var patientIds = baseIds
        .Concat(baseIds.Select(BootstrapService.NormalizeAriaBaseId).Where(id => !baseIds.Contains(id)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    var aria = (await ariaPlanResolver.ResolveAsync(patientIds, cancellationToken)).ToList();
    var ariaByPatient = aria.ToDictionary(x => x.PatientId, StringComparer.OrdinalIgnoreCase);

    foreach (var patient in data.FollowUpPatients)
    {
        if (!BootstrapService.TryFindAriaPlan(ariaByPatient, patient.PatientId, out var plan)) continue;
        if (!string.IsNullOrWhiteSpace(plan.PlannedMachineDisplayName))
            patient.PlannedMachineDisplayName = plan.PlannedMachineDisplayName;
        if (!string.IsNullOrWhiteSpace(plan.BeamType))
            patient.BeamType = plan.BeamType;
        patient.NumberOfFractions = plan.NumberOfFractions;
        if (!string.IsNullOrWhiteSpace(plan.IrradiationModality))
            patient.IrradiationModality = plan.IrradiationModality;
        if (!string.IsNullOrWhiteSpace(plan.ExactBeamEnergy))
            patient.ExactBeamEnergy = plan.ExactBeamEnergy;
        patient.Plans = plan.Plans;
    }

    foreach (var item in data.AgendaItems)
    {
        if (string.IsNullOrWhiteSpace(item.SitraMedGuid)) continue;
        if (!guidHcMapApply.TryGetValue(item.SitraMedGuid, out var hc)) continue;
        if (!BootstrapService.TryFindAriaPlan(ariaByPatient, hc, out var plan)) continue;
        if (!string.IsNullOrWhiteSpace(plan.BeamType)) item.BeamType = plan.BeamType;
        if (!string.IsNullOrWhiteSpace(plan.IrradiationModality)) item.IrradiationModality = plan.IrradiationModality;
    }

    // Recalcular TreatmentLabel con los datos ARIA actualizados
    foreach (var patient in data.FollowUpPatients)
    {
        patient.TreatmentLabel = TreatmentClassifier.BuildLabel(
            patient.TreatmentTechnique,
            patient.IrradiationModality,
            patient.ExactBeamEnergy,
            patient.BeamType);
    }

    // Propagar TreatmentLabel desde seguimiento a sus turnos de agenda
    var followUpLabelMapApply = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in data.FollowUpPatients)
        if (!string.IsNullOrWhiteSpace(p.PatientId) && !string.IsNullOrWhiteSpace(p.TreatmentLabel))
            followUpLabelMapApply[p.PatientId!] = p.TreatmentLabel!;
    foreach (var item in data.AgendaItems)
    {
        if (string.IsNullOrWhiteSpace(item.SitraMedGuid)) continue;
        if (!guidHcMapApply.TryGetValue(item.SitraMedGuid, out var hc)) continue;
        if (followUpLabelMapApply.TryGetValue(hc, out var label))
        {
            item.TreatmentLabel = label;  // paciente en seguimiento: label preciso
        }
        else if (ariaByPatient.TryGetValue(hc, out var plan))
        {
            // Paciente agenda-pura: calcular label desde datos ARIA + texto de agenda
            var tech = TreatmentClassifier.Classify(item.Treatment);
            item.TreatmentLabel = TreatmentClassifier.BuildLabel(tech, plan.IrradiationModality, plan.ExactBeamEnergy, plan.BeamType);
        }
        // RC refinement: fraccionada / fracción única según NumberOfFractions de ARIA
        if (item.TreatmentLabel?.Contains("RC") == true
            && ariaByPatient.TryGetValue(hc, out var rcPlan)
            && rcPlan.NumberOfFractions.HasValue)
        {
            item.TreatmentLabel = rcPlan.NumberOfFractions.Value == 1
                ? "RC fracción única"
                : "RC fraccionada";
        }
    }

    data.StageSummary = BootstrapService.ComputeStageSummary(
        data.FollowUpPatients,
        configurationProvider.Configuration.Stages.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase));

    data.AriaPlans = aria;
    data.GeneratedAtUtc = DateTime.UtcNow;

    await snapshotStore.SaveAsync("dashboard_bootstrap", data, cancellationToken);
    return TypedResults.Ok(HomeResponseMapper.Map(data, configurationProvider));
});

// Consulta ARIA en background (fire-and-forget).
// Cuando hay runner exe configurado retorna 202 inmediatamente; el cliente hace polling a /api/aria/query-status.
// Cuando no hay runner exe importa el aria_results_*.json más reciente de forma sincrónica (rápido).
app.MapPost("/api/aria/run-query", async Task<IResult> (
        AriaJobState jobState,
        ISnapshotStore snapshotStore,
        IRtSystemConfigurationProvider configurationProvider,
        AriaRuntimeOptions ariaOptions,
        CancellationToken cancellationToken) =>
{
    var hcRegex = new Regex(@"^\d{1,3}-\d{4,7}-\d{1,3}$");
    var runnerExe = Environment.GetEnvironmentVariable("MEVA_ARIA_RUNNER_EXE");

    if (!string.IsNullOrWhiteSpace(runnerExe) && File.Exists(runnerExe))
    {
        // ── Camino async: AriaRunner en background ────────────────────────
        if (jobState.IsRunning)
            return TypedResults.Conflict(new { error = "Ya hay una consulta ARIA en curso. Usar /api/aria/query-status para ver el progreso." });

        var snapshot = await snapshotStore.TryLoadAsync<DashboardBootstrapData>("dashboard_bootstrap", cancellationToken);
        var guidHcMapQuery = await snapshotStore.TryLoadAsync<Dictionary<string, string>>("guid_hc_map", cancellationToken)
                             ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Seguimiento: siempre frescos
        var followUpHcIds = (snapshot?.FollowUpPatients ?? [])
            .Select(p => p.PatientId ?? "")
            .Where(id => hcRegex.IsMatch(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Agenda-pura: solo los que NO están ya en mock.json (primera vez o pacientes nuevos)
        var mockPath0 = ariaOptions.MockPlansJsonPath;
        var existingMockIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(mockPath0) && File.Exists(mockPath0))
        {
            try
            {
                var existing0 = JsonSerializer.Deserialize<List<AriaPlanSnapshot>>(
                    await File.ReadAllTextAsync(mockPath0, cancellationToken),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (existing0 != null)
                    foreach (var e in existing0) existingMockIds.Add(e.PatientId);
            }
            catch { }
        }

        var agendaNewIds = (snapshot?.AgendaItems ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a.SitraMedGuid) && guidHcMapQuery.ContainsKey(a.SitraMedGuid!))
            .Select(a => guidHcMapQuery[a.SitraMedGuid!])
            .Where(id => hcRegex.IsMatch(id) && !followUpHcIds.Contains(id) && !existingMockIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var baseHcIds = followUpHcIds.Concat(agendaNewIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // SitraMed sufija el HC por seguimiento/curso concurrente; ARIA guarda al paciente bajo un
        // único PatientId (típicamente sufijo "-0"). Consultamos también esa variante.
        var hcIds = baseHcIds
            .Concat(baseHcIds.Select(BootstrapService.NormalizeAriaBaseId).Where(id => !baseHcIds.Contains(id)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (hcIds.Count == 0)
            return TypedResults.BadRequest(new { error = "No hay pacientes con HC válida en el snapshot." });

        if (!jobState.TryStart(hcIds.Count, snapshotsDirectory))
            return TypedResults.Conflict(new { error = "Consulta ya iniciada (race condition)." });

        var inputPath = Path.Combine(snapshotsDirectory, "aria_input_tmp.json");
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(new { patientIds = hcIds }), cancellationToken);

        var runnerDir = Path.GetDirectoryName(runnerExe)!;
        var psi = new ProcessStartInfo(runnerExe)
        {
            Arguments = $"--input=\"{inputPath}\" --output-dir=\"{snapshotsDirectory}\"",
            WorkingDirectory = runnerDir,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var proc = Process.Start(psi);
        if (proc == null)
        {
            jobState.Complete(false, 0, "No se pudo iniciar AriaRunner.exe.");
            return TypedResults.Problem("No se pudo iniciar AriaRunner.exe.", statusCode: 500);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await proc.WaitForExitAsync();

                if (proc.ExitCode != 0)
                {
                    var stderr = await proc.StandardError.ReadToEndAsync();
                    jobState.Complete(false, 0, $"AriaRunner salio con codigo {proc.ExitCode}. {stderr.Trim()}");
                    return;
                }

                // Importar resultado
                var resultFile = Directory.GetFiles(snapshotsDirectory, "aria_results_*.json")
                    .OrderByDescending(f => f).FirstOrDefault();

                if (resultFile == null) { jobState.Complete(false, 0, "No se encontró archivo de resultados."); return; }

                var json = await File.ReadAllTextAsync(resultFile);
                var output = JsonSerializer.Deserialize<AriaRunnerOutput>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (output?.Patients == null) { jobState.Complete(false, 0, "Archivo de resultados vacío."); return; }

                var plans = ParseAriaOutput(output, configurationProvider.Configuration.Machines);

                var mockPath = ariaOptions.MockPlansJsonPath;
                if (!string.IsNullOrWhiteSpace(mockPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(mockPath)!);

                    // Mergear: nuevos resultados + datos anteriores de pacientes no re-consultados
                    var newIds = plans.Select(p => p.PatientId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (File.Exists(mockPath))
                    {
                        try
                        {
                            var old = JsonSerializer.Deserialize<List<AriaPlanSnapshot>>(
                                await File.ReadAllTextAsync(mockPath),
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (old != null)
                                plans.AddRange(old.Where(p => !newIds.Contains(p.PatientId)));
                        }
                        catch { }
                    }

                    await File.WriteAllTextAsync(mockPath,
                        JsonSerializer.Serialize(plans, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                }

                jobState.Complete(true, plans.Count, null);
            }
            catch (Exception ex) { jobState.Complete(false, 0, ex.Message); }
            finally { proc.Dispose(); }
        });

        return TypedResults.Accepted("/api/aria/query-status", new { status = "started", totalPatients = hcIds.Count });
    }

    // ── Camino sincrónico: importar resultado existente (sin runner) ──────
    var resolvedPath = Directory.GetFiles(snapshotsDirectory, "aria_results_*.json")
        .OrderByDescending(f => f).FirstOrDefault();

    if (resolvedPath == null)
        return TypedResults.BadRequest(new { error = "No se encontro ningun archivo aria_results_*.json." });

    AriaRunnerOutput? runnerOutput2;
    try
    {
        var json = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
        runnerOutput2 = JsonSerializer.Deserialize<AriaRunnerOutput>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (Exception ex) { return TypedResults.BadRequest(new { error = $"Error leyendo {resolvedPath}: {ex.Message}" }); }

    if (runnerOutput2?.Patients == null)
        return TypedResults.BadRequest(new { error = "Archivo vacio o formato invalido." });

    var plans2 = ParseAriaOutput(runnerOutput2, configurationProvider.Configuration.Machines);

    var mockPath2 = ariaOptions.MockPlansJsonPath;
    if (string.IsNullOrWhiteSpace(mockPath2))
        return TypedResults.Problem("MockPlansJsonPath no configurado.", statusCode: 500);

    Directory.CreateDirectory(Path.GetDirectoryName(mockPath2)!);
    await File.WriteAllTextAsync(mockPath2,
        JsonSerializer.Serialize(plans2, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        cancellationToken);

    return TypedResults.Ok(new { queriedAria = false, importedFile = Path.GetFileName(resolvedPath), withActivePlan = plans2.Count });
});

app.MapGet("/api/aria/query-status", (AriaJobState jobState) =>
{
    var (current, total) = jobState.ReadProgress();
    var pct = total > 0 ? current * 100 / total : 0;
    return Results.Ok(new
    {
        isRunning = jobState.IsRunning,
        startedAt = jobState.StartedAt,
        currentPatient = current,
        totalPatients = total,
        progressPct = pct,
        lastRunSucceeded = jobState.LastRunSucceeded,
        lastWithActivePlan = jobState.LastWithActivePlan,
        lastError = jobState.LastError,
        completedAt = jobState.CompletedAt
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

// Returns whether the feriados.txt needs to be updated for next year.
app.MapGet("/api/alerts/feriados", (BusinessDayCalculator bdCalc) =>
{
    var today = DateOnly.FromDateTime(DateTime.Today);
    var endOfYear = new DateOnly(today.Year, 12, 31);
    var daysToYearEnd = endOfYear.DayNumber - today.DayNumber;
    var nextYear = today.Year + 1;
    var show = daysToYearEnd <= 10 && !bdCalc.HasHolidaysForYear(nextYear);
    return TypedResults.Ok(new { show, year = nextYear });
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
    List<string> scrapingErrors = [];

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
                var result = await agendaExtractor.ExtractForDateAsync(targetDate, cancellationToken);
                scraped = result.Slots;
                scrapingErrors = result.ScrapingErrors.ToList();
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
            // Enriquecer slots scrapeados con TreatmentLabel del paciente en seguimiento
            // (el texto de la agenda es genérico; el de seguimiento tiene la técnica real)
            var guidHcMapAgenda = await snapshotStore.TryLoadAsync<Dictionary<string, string>>("guid_hc_map", cancellationToken)
                                  ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var followUpLabelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in bootstrap.FollowUpPatients)
                if (!string.IsNullOrWhiteSpace(p.PatientId) && !string.IsNullOrWhiteSpace(p.TreatmentLabel))
                    followUpLabelMap[p.PatientId!] = p.TreatmentLabel!;
            foreach (var slot in slots)
            {
                if (string.IsNullOrWhiteSpace(slot.SitraMedGuid)) continue;
                if (!guidHcMapAgenda.TryGetValue(slot.SitraMedGuid, out var hc)) continue;
                if (!followUpLabelMap.TryGetValue(hc, out var label)) continue;
                slot.TreatmentLabel ??= label;
            }
            var stages = configProvider.Configuration.Stages.OrderBy(s => s.SortOrder).ToList();
            var machines = configProvider.Configuration.Machines;

            var f4bSortOrder = stages.FirstOrDefault(s =>
                string.Equals(s.Code, "F4B", StringComparison.OrdinalIgnoreCase))?.SortOrder ?? int.MaxValue;

            // Upper bound: last scraped date on disk (avoids generating slots beyond scrape range)
            var scrapedDates = Directory.Exists(snapshotsDirectory)
                ? Directory.GetFiles(snapshotsDirectory, "agenda_????-??-??.json")
                    .Select(f => Path.GetFileNameWithoutExtension(f).Replace("agenda_", ""))
                    .Where(d => DateOnly.TryParse(d, out _))
                    .Select(DateOnly.Parse)
                    .Where(d => d > today)
                    .ToList()
                : [];
            var maxScrapedDate = scrapedDates.DefaultIfEmpty(today).Max();

            // Patients with a real (scraped) appointment on any upcoming date must not also get an estimate.
            var patientsWithRealSlot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scrapedDate in scrapedDates)
            {
                var daySlots = await snapshotStore.TryLoadAsync<List<MachineAppointmentSnapshot>>(
                    $"agenda_{scrapedDate:yyyy-MM-dd}", cancellationToken);
                if (daySlots == null) continue;
                foreach (var s in daySlots)
                {
                    if (string.IsNullOrWhiteSpace(s.SitraMedGuid)) continue;
                    if (guidHcMapAgenda.TryGetValue(s.SitraMedGuid, out var hc))
                        patientsWithRealSlot.Add(hc);
                }
            }

            // Un mismo paciente puede aparecer en FollowUpPatients bajo dos etapas simultáneas
            // (ver BUG_AGENDA_EQUIPOS_Y_ESTIMADOS.md, "bug pendiente") — sin dedup generaba dos
            // slots estimados duplicados, mismo equipo, mismo día. Se dedupea por PatientId y se
            // conserva la etapa más avanzada (mayor SortOrder), la más cercana a tratamiento.
            //
            // TODO (2026-09-16e, sin resolver): confirmado a mano en SitraMed que al menos un caso
            // (SARCHIONI, 1-118582-0) no es un duplicado espurio sino 3 FLUJOS DE TRATAMIENTO
            // REALES Y DISTINTOS (BQT, IMRT Retroperitoneo, IMRT Pelvis) — este dedup descarta en
            // silencio el estimado del flujo menos avanzado aunque sea un tratamiento activo que
            // necesita su propio turno. No hay hoy una forma barata de distinguir "duplicado
            // espurio" de "flujos concurrentes reales" (posible pista: comparar TreatmentZone/
            // técnica entre las filas del mismo paciente). Ver doc para el plan de retomar esto.
            var dedupedFollowUpPatients = bootstrap.FollowUpPatients
                .GroupBy(p => string.IsNullOrWhiteSpace(p.PatientId) ? Guid.NewGuid().ToString() : p.PatientId,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(p => stages.FirstOrDefault(s =>
                        string.Equals(s.Code, p.StageCode, StringComparison.OrdinalIgnoreCase))?.SortOrder ?? -1)
                    .First())
                .ToList();

            foreach (var patient in dedupedFollowUpPatients)
            {
                if (!string.IsNullOrWhiteSpace(patient.PatientId) && patientsWithRealSlot.Contains(patient.PatientId))
                    continue;

                // Resolve machine name and source
                string? machineName;
                string estimatedSource;

                var stageIdx = stages.FindIndex(s =>
                    string.Equals(s.Code, patient.StageCode, StringComparison.OrdinalIgnoreCase));
                if (stageIdx < 0) continue;

                if (!string.IsNullOrWhiteSpace(patient.PlannedMachineDisplayName))
                {
                    machineName = patient.PlannedMachineDisplayName;
                    estimatedSource = "aria";
                }
                else
                {
                    // Infer from single-machine center for patients past F4B (tomosimulación)
                    if (stages[stageIdx].SortOrder < f4bSortOrder) continue;

                    var centerMachines = machines
                        .Where(m => string.Equals(m.CenterName, patient.CenterName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (centerMachines.Count != 1) continue;

                    machineName = centerMachines[0].DisplayName;
                    estimatedSource = "center";
                }

                // Stagger the estimate by how long the patient has already been sitting in
                // their current stage, instead of assuming every patient in a stage just
                // started it today — otherwise everyone in the same stage lands on the exact
                // same projected date, an artificial pile-up that doesn't reflect reality.
                var currentStageRemaining = Math.Max(stages[stageIdx].ExpectedDays - patient.DaysInStage, 0);
                var laterStagesDays = stages.Skip(stageIdx + 1).Sum(s => s.ExpectedDays);
                var remainingDays = currentStageRemaining + laterStagesDays;
                var daysToStart = Math.Max(remainingDays, 1);
                var estimatedStart = bdCalc.AddBusinessDays(today, daysToStart);

                if (targetDate < estimatedStart || targetDate > maxScrapedDate) continue;

                bool inWindow;
                if (patient.NumberOfFractions is > 0)
                {
                    var treatmentDays = bdCalc.GetUpcomingBusinessDays(
                        estimatedStart.AddDays(-1), patient.NumberOfFractions.Value);
                    inWindow = treatmentDays.Contains(targetDate);
                }
                else
                {
                    inWindow = estimatedStart == targetDate;
                }

                if (!inWindow) continue;

                // Center must match the assigned machine, not the patient's registered
                // treating center in SitraMed — patients get transferred between centers,
                // so patient.CenterName can point at a different center than where ARIA
                // actually planned the machine. Grouping by CenterName (dashboard) with a
                // mismatched value silently hides the estimate from the right center's view.
                var slotCenterName = machines
                    .FirstOrDefault(m => string.Equals(m.DisplayName, machineName, StringComparison.OrdinalIgnoreCase))
                    ?.CenterName ?? patient.CenterName ?? string.Empty;

                slots.Add(new AgendaSlotDto
                {
                    CenterName = slotCenterName,
                    MachineName = machineName,
                    PatientName = patient.PatientName,
                    AgendaDate = targetDate.ToString("yyyy-MM-dd"),
                    Treatment = patient.StageDisplayName,
                    TreatmentTechnique = patient.TreatmentTechnique,
                    BeamType = patient.BeamType,
                    IrradiationModality = patient.IrradiationModality,
                    TreatmentLabel = patient.TreatmentLabel
                        ?? TreatmentClassifier.BuildLabel(
                            patient.TreatmentTechnique,
                            patient.IrradiationModality,
                            patient.ExactBeamEnergy,
                            patient.BeamType),
                    SitraMedGuid = patient.SitraMedGuid,
                    IsEstimated = true,
                    EstimatedFromStage = $"{patient.StageCode} - {patient.StageDisplayName}",
                    EstimatedPatientId = patient.PatientId,
                    EstimatedSource = estimatedSource,
                    Priority = patient.Priority
                });
            }
        }
    }

    return TypedResults.Ok(new { slots, scrapingErrors });
});

// ─── Tomograph Agenda ─────────────────────────────────────────────────────────

app.MapGet("/api/tomograph-agenda/available-dates", () =>
{
    if (!Directory.Exists(snapshotsDirectory))
        return Results.Ok(Array.Empty<string>());

    var dates = Directory.GetFiles(snapshotsDirectory, "tomograph_agenda_????-??-??.json")
        .Select(f => Path.GetFileNameWithoutExtension(f).Replace("tomograph_agenda_", ""))
        .Where(d => DateOnly.TryParse(d, out _))
        .Order()
        .ToArray();

    return Results.Ok(dates);
});

app.MapPost("/api/tomograph-agenda/scrape-upcoming", async Task<IResult> (
        ITomographAgendaExtractor tomographExtractor,
        ISnapshotStore snapshotStore,
        BusinessDayCalculator bdCalc,
        int? days,
        CancellationToken cancellationToken) =>
{
    var count = Math.Max(1, Math.Min(days ?? 15, 30));
    var today = DateOnly.FromDateTime(DateTime.Today);
    var upcoming = bdCalc.GetUpcomingBusinessDays(today, count);

    var allItems = await tomographExtractor.ExtractForDatesAsync(upcoming, cancellationToken);

    var saved = new List<string>();
    foreach (var (date, items) in allItems.OrderBy(kv => kv.Key))
    {
        await snapshotStore.SaveAsync($"tomograph_agenda_{date:yyyy-MM-dd}", items.ToList(), cancellationToken);
        saved.Add(date.ToString("yyyy-MM-dd"));
    }

    return TypedResults.Ok(new { savedDates = saved, totalDays = saved.Count });
});

app.MapGet("/api/tomograph-agenda", async Task<IResult> (
        ITomographAgendaExtractor tomographExtractor,
        ISnapshotStore snapshotStore,
        DateOnly? date,
        CancellationToken cancellationToken) =>
{
    var targetDate = date ?? DateOnly.FromDateTime(DateTime.Today);
    var today = DateOnly.FromDateTime(DateTime.Today);

    IReadOnlyList<MachineAppointmentSnapshot> scraped;

    var stored = await snapshotStore.TryLoadAsync<List<MachineAppointmentSnapshot>>($"tomograph_agenda_{targetDate:yyyy-MM-dd}", cancellationToken);
    if (stored != null)
    {
        scraped = stored;
    }
    else if (targetDate >= today)
    {
        try
        {
            scraped = await tomographExtractor.ExtractForDateAsync(targetDate, cancellationToken);
            await snapshotStore.SaveAsync($"tomograph_agenda_{targetDate:yyyy-MM-dd}", scraped.ToList(), cancellationToken);
        }
        catch (Exception ex)
        {
            return TypedResults.Problem($"Error al obtener agenda de tomógrafos para {targetDate}: {ex.Message}", statusCode: 500);
        }
    }
    else
    {
        scraped = [];
    }

    var slots = scraped.Select(s => new AgendaSlotDto(s)).ToList();
    return TypedResults.Ok(slots);
});

// ─── Reservas de turno ───────────────────────────────────────────────────────

app.MapGet("/api/reservations", async (TurnReservationStore reservationStore, CancellationToken ct) =>
{
    await reservationStore.PruneExpiredAsync(2, ct);
    return TypedResults.Ok(await reservationStore.LoadAllActiveAsync(ct));
});

app.MapGet("/api/reservations/{patientId}", async (string patientId, TurnReservationStore reservationStore, CancellationToken ct) =>
{
    var res = await reservationStore.GetByPatientIdAsync(patientId, ct);
    return res is not null ? Results.Ok(res) : Results.NotFound();
});

app.MapPost("/api/reservations", async (HttpContext httpContext, IMemoryCache memoryCache,
    TurnReservationStore reservationStore, ISnapshotStore snapshotStore,
    CreateReservationRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.PatientId) || string.IsNullOrWhiteSpace(req.PatientName) ||
        string.IsNullOrWhiteSpace(req.MachineDisplayName) || string.IsNullOrWhiteSpace(req.ReservedDate) ||
        string.IsNullOrWhiteSpace(req.ReservedTime) || string.IsNullOrWhiteSpace(req.Username) ||
        string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Faltan campos requeridos" });

    var authReject = CheckOftechPassword(httpContext, memoryCache, req.Password);
    if (authReject is not null) return authReject;

    if (!DateOnly.TryParse(req.ReservedDate, out var reservedDate))
        return Results.BadRequest(new { error = "Fecha inválida" });

    var now = DateTime.UtcNow;
    var snapshot = await snapshotStore.TryLoadAsync<DashboardBootstrapData>("dashboard_bootstrap", ct);
    var patient = snapshot?.FollowUpPatients?.FirstOrDefault(p =>
        string.Equals(p.PatientId, req.PatientId, StringComparison.OrdinalIgnoreCase));

    var reservation = new PatientTurnReservation
    {
        ReservationId = $"RES_{req.PatientId}_{now:yyyyMMddHHmmss}",
        PatientId = req.PatientId,
        PatientName = req.PatientName,
        CenterName = req.CenterName ?? string.Empty,
        MachineDisplayName = req.MachineDisplayName,
        ReservedDate = reservedDate,
        ReservedTime = req.ReservedTime,
        Observations = req.Observations ?? string.Empty,
        RegisteredByUsername = req.Username,
        RegisteredAtUtc = now,
        PlannedMachineAtReservation = patient?.PlannedMachineDisplayName
    };

    await reservationStore.SaveOrUpdateAsync(reservation, ct);
    return TypedResults.Created($"/api/reservations/{req.PatientId}", reservation);
});

app.MapDelete("/api/reservations/{reservationId}", async (string reservationId, HttpContext httpContext,
    IMemoryCache memoryCache, TurnReservationStore reservationStore, CancellationToken ct) =>
{
    DeleteReservationRequest? req;
    try { req = await httpContext.Request.ReadFromJsonAsync<DeleteReservationRequest>(ct); }
    catch { req = null; }
    if (req is null || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Contraseña requerida" });

    var authRejectDel = CheckOftechPassword(httpContext, memoryCache, req.Password);
    if (authRejectDel is not null) return authRejectDel;

    await reservationStore.DeleteByIdAsync(reservationId, ct);
    return TypedResults.NoContent();
});

// ─── Pedidos ─────────────────────────────────────────────────────────────────

app.MapGet("/api/pedidos", async (PedidoStore pedidoStore, CancellationToken ct) =>
    TypedResults.Ok(await pedidoStore.LoadAllAsync(ct)));

app.MapPost("/api/pedidos", async (PedidoStore pedidoStore, PedidoItem req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Type))
        return Results.BadRequest(new { error = "Falta el tipo de pedido" });

    req.Id = $"PED_{Guid.NewGuid():N}";
    req.CreatedAtUtc = DateTime.UtcNow;
    await pedidoStore.SaveOrUpdateAsync(req, ct);
    return TypedResults.Created($"/api/pedidos/{req.Id}", req);
});

app.MapPut("/api/pedidos/{id}", async (string id, PedidoStore pedidoStore, PedidoItem req, CancellationToken ct) =>
{
    req.Id = id;
    await pedidoStore.SaveOrUpdateAsync(req, ct);
    return TypedResults.Ok(req);
});

app.MapPost("/api/pedidos/{id}/complete", async (string id, PedidoStore pedidoStore, CancellationToken ct) =>
{
    var all = await pedidoStore.LoadAllAsync(ct);
    var item = all.FirstOrDefault(p => p.Id == id);
    if (item is null) return Results.NotFound();
    item.Completed = true;
    await pedidoStore.SaveOrUpdateAsync(item, ct);
    return TypedResults.Ok(item);
});

app.MapPost("/api/pedidos/{id}/pin", async (string id, PedidoStore pedidoStore, CancellationToken ct) =>
{
    var all = await pedidoStore.LoadAllAsync(ct);
    var item = all.FirstOrDefault(p => p.Id == id);
    if (item is null) return Results.NotFound();
    item.Pinned = !item.Pinned;
    await pedidoStore.SaveOrUpdateAsync(item, ct);
    return TypedResults.Ok(item);
});

app.MapDelete("/api/pedidos/{id}", async (string id, PedidoStore pedidoStore, CancellationToken ct) =>
{
    await pedidoStore.DeleteByIdAsync(id, ct);
    return TypedResults.NoContent();
});

// ─── QA Paciente Específico ────────────────────────────────────────────────

app.MapGet("/api/qa-especifico", async (QaEspecificoStore qaStore, CancellationToken ct) =>
    TypedResults.Ok(await qaStore.LoadAllAsync(ct)));

app.MapPost("/api/qa-especifico", async (QaEspecificoStore qaStore, QaEspecificoItem req, CancellationToken ct) =>
{
    if (req.Origin == "Auto" && !string.IsNullOrWhiteSpace(req.PlanId))
    {
        var all = await qaStore.LoadAllAsync(ct);
        var existing = all.FirstOrDefault(x =>
            x.Origin == "Auto" && !x.Excluded &&
            string.Equals(x.PatientId, req.PatientId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.PlanId, req.PlanId, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return Results.Ok(existing);
    }
    req.Id = $"QA_{Guid.NewGuid():N}";
    req.CreatedAtUtc = DateTime.UtcNow;
    await qaStore.SaveOrUpdateAsync(req, ct);
    return Results.Created($"/api/qa-especifico/{req.Id}", req);
});

app.MapPut("/api/qa-especifico/{id}", async (string id, QaEspecificoStore qaStore, QaEspecificoItem req, CancellationToken ct) =>
{
    req.Id = id;
    await qaStore.SaveOrUpdateAsync(req, ct);
    return TypedResults.Ok(req);
});

app.MapPost("/api/qa-especifico/{id}/pin", async (string id, QaEspecificoStore qaStore, CancellationToken ct) =>
{
    var all = await qaStore.LoadAllAsync(ct);
    var item = all.FirstOrDefault(p => p.Id == id);
    if (item is null) return Results.NotFound();
    item.Pinned = !item.Pinned;
    await qaStore.SaveOrUpdateAsync(item, ct);
    return TypedResults.Ok(item);
});

app.MapDelete("/api/qa-especifico/{id}", async (string id, QaEspecificoStore qaStore, CancellationToken ct) =>
{
    var all = await qaStore.LoadAllAsync(ct);
    var item = all.FirstOrDefault(p => p.Id == id);
    if (item is null) return TypedResults.NoContent();
    if (item.Origin == "Auto")
    {
        item.Excluded = true;
        await qaStore.SaveOrUpdateAsync(item, ct);
    }
    else
    {
        await qaStore.DeleteByIdAsync(id, ct);
    }
    return TypedResults.NoContent();
});

// ─── TBI — mails ─────────────────────────────────────────────────────────────

app.MapGet("/api/tbi-mail", async (TbiMailStore tbiMailStore, CancellationToken ct) =>
    TypedResults.Ok(await tbiMailStore.LoadAllAsync(ct)));

app.MapPost("/api/tbi-mail/refresh", async (TbiMailClient tbiMailClient, TbiMailStore tbiMailStore, TurnReservationStore reservationStore, TbiMailOptions tbiMailOptions, CancellationToken ct) =>
{
    if (!tbiMailOptions.IsConfigured) return Results.StatusCode(StatusCodes.Status501NotImplemented);
    var found = await tbiMailClient.FetchNewAsync(ct);
    foreach (var r in found)
    {
        var info = new TbiMailInfo
        {
            PatientId = r.PatientId,
            PatientName = r.PatientName,
            TomographyDate = r.TomographyDate,
            TotalApplications = r.TotalApplications,
            MessageId = r.MessageId,
            ReceivedAtUtc = r.ReceivedAtUtc
        };
        var applied = await tbiMailStore.UpsertFromMailAsync(info, ct);
        if (!applied) continue; // mismo mail ya procesado — no re-tocar el turno reservado

        if (r.TreatmentStartDate is not null && !string.IsNullOrWhiteSpace(r.MachineDisplayName))
        {
            await reservationStore.SaveOrUpdateAsync(new PatientTurnReservation
            {
                ReservationId = $"RES_TBI_{r.PatientId}",
                PatientId = r.PatientId,
                PatientName = r.PatientName,
                CenterName = "MEVA-Central",
                MachineDisplayName = r.MachineDisplayName,
                ReservedDate = r.TreatmentStartDate.Value,
                ReservedTime = r.TreatmentStartTime ?? string.Empty,
                Observations = "TBI",
                RegisteredByUsername = $"{r.SenderEmail ?? "Desconocido"} - cargado automáticamente",
                RegisteredAtUtc = DateTime.UtcNow,
                PendingReview = true
            }, ct);
        }
    }
    return TypedResults.Ok(new { imported = found.Count });
});

app.MapPut("/api/tbi-mail/{patientId}", async (string patientId, TbiMailStore tbiMailStore, TurnReservationStore reservationStore, TbiMailEditRequest req, CancellationToken ct) =>
{
    var updated = await tbiMailStore.UpdateAsync(patientId, req.PatientName, req.TomographyDate, req.TotalApplications, ct);

    if (req.TreatmentStartDate is not null && !string.IsNullOrWhiteSpace(req.MachineDisplayName))
    {
        var existing = await reservationStore.GetByPatientIdAsync(patientId, ct);
        await reservationStore.SaveOrUpdateAsync(new PatientTurnReservation
        {
            ReservationId = $"RES_TBI_{patientId}",
            PatientId = patientId,
            PatientName = req.PatientName,
            CenterName = "MEVA-Central",
            MachineDisplayName = req.MachineDisplayName,
            ReservedDate = req.TreatmentStartDate.Value,
            ReservedTime = existing?.ReservedTime ?? string.Empty,
            Observations = "TBI",
            RegisteredByUsername = string.IsNullOrWhiteSpace(req.RegisteredBy) ? (existing?.RegisteredByUsername ?? string.Empty) : req.RegisteredBy,
            RegisteredAtUtc = existing?.RegisteredAtUtc ?? DateTime.UtcNow,
            PendingReview = false
        }, ct);
    }
    else
    {
        await reservationStore.DeleteByIdAsync($"RES_TBI_{patientId}", ct);
    }

    var reservation = await reservationStore.GetByPatientIdAsync(patientId, ct);
    return TypedResults.Ok(new { info = updated, reservation });
});

// ─── TBI — dosis (SitraMed) ──────────────────────────────────────────────────

app.MapGet("/api/tbi-dose", async (TbiDoseStore tbiDoseStore, CancellationToken ct) =>
    TypedResults.Ok(await tbiDoseStore.LoadAllAsync(ct)));

app.MapPost("/api/tbi-dose/refresh", async (
    PlaywrightSitraMedClient sitraMedClient, TbiDoseStore tbiDoseStore,
    ISnapshotStore snapshotStore, IRtSystemConfigurationProvider configProvider, CancellationToken ct) =>
{
    if (!sitraMedClient.CanUseRemoteScraping())
        return Results.StatusCode(StatusCodes.Status501NotImplemented);

    var snapshot = await snapshotStore.TryLoadAsync<DashboardBootstrapData>("dashboard_bootstrap", ct);
    var f6aOrder = configProvider.Configuration.Stages.FirstOrDefault(s => s.Code == "F6A")?.SortOrder ?? 0;
    var stageOrderByCode = configProvider.Configuration.Stages.ToDictionary(s => s.Code, s => s.SortOrder);

    var candidates = (snapshot?.FollowUpPatients ?? [])
        .Where(p => p.TreatmentTechnique == "TBI" && !string.IsNullOrWhiteSpace(p.SitraMedGuid))
        .Where(p => stageOrderByCode.TryGetValue(p.StageCode, out var order) && order >= f6aOrder)
        .Select(p => (p.PatientId, p.SitraMedGuid!))
        .ToList();

    var found = await sitraMedClient.FetchTbiDosesForGuidsAsync(candidates, ct);
    foreach (var (patientId, dose) in found)
    {
        await tbiDoseStore.UpsertAsync(new TbiDoseInfo
        {
            PatientId = patientId,
            DailyDoseCGy = dose.DailyDoseCGy,
            TotalDoseCGy = dose.TotalDoseCGy,
            FetchedAtUtc = DateTime.UtcNow
        }, ct);
    }
    return TypedResults.Ok(new { scanned = candidates.Count, found = found.Count });
});

app.MapGet("/api/machine-capacity", async (string date, string machine,
    ISnapshotStore snapshotStore, IRtSystemConfigurationProvider configProvider, CancellationToken ct) =>
{
    if (!DateOnly.TryParse(date, out var targetDate))
        return Results.BadRequest(new { error = "Fecha inválida" });

    var slots = await snapshotStore.TryLoadAsync<List<MachineAppointmentSnapshot>>($"agenda_{targetDate:yyyy-MM-dd}", ct)
                ?? [];
    var realSlots = slots.Count(s => string.Equals(s.MachineName, machine, StringComparison.OrdinalIgnoreCase));

    var cap = configProvider.Configuration.MachineCapacities
        .FirstOrDefault(c => string.Equals(c.MachineName, machine, StringComparison.OrdinalIgnoreCase));
    var capacity = 0;
    if (cap is not null && cap.StandardSlotMinutes > 0)
    {
        var workMin = (double)(cap.WorkingHours - cap.ReservedSpecialHours) * 60;
        capacity = (int)(workMin / cap.StandardSlotMinutes);
    }

    return Results.Ok(new { realSlots, capacity, overload = Math.Max(0, realSlots - capacity) });
});

// ─── Auth ────────────────────────────────────────────────────────────────────

app.MapPost("/api/auth/verify", (HttpContext httpContext, IMemoryCache memoryCache, AuthVerifyRequest req) =>
{
    var profile = req.Profile?.ToLowerInvariant();
    if (profile != "sysadmin" && profile != "oftech")
        return Results.BadRequest(new { valid = false, error = "Perfil inválido" });

    var envVar = profile == "sysadmin" ? "MEVA_PWD_SYSADMIN_HASH" : "MEVA_PWD_OFTECH_HASH";
    var expectedHash = Environment.GetEnvironmentVariable(envVar);
    if (string.IsNullOrEmpty(expectedHash))
        return Results.Json(new { valid = false, error = "Perfil no configurado" }, statusCode: 503);

    var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var rateCacheKey = $"auth_rate_{ip}_{profile}";
    memoryCache.TryGetValue(rateCacheKey, out int failCount);

    if (failCount >= 5)
        return Results.Json(new { valid = false, error = "Demasiados intentos. Espere 5 minutos." }, statusCode: 429);

    var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.Password ?? "")));
    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
    {
        memoryCache.Set(rateCacheKey, failCount + 1,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        return Results.Json(new { valid = false }, statusCode: 401);
    }

    memoryCache.Remove(rateCacheKey);
    return Results.Ok(new { valid = true });
});

// ─── Derivación ──────────────────────────────────────────────────────────────

app.MapGet("/api/derivation/attended-patients", async (
    string machine, string date,
    IRtSystemConfigurationProvider configProvider,
    IAttendedPatientsExtractor extractor,
    CancellationToken ct) =>
{
    if (!DateOnly.TryParse(date, out var dateOnly))
        return Results.BadRequest(new { error = "Formato de fecha inválido (use YYYY-MM-DD)" });

    var machineConfig = configProvider.Configuration.Machines
        .FirstOrDefault(m => string.Equals(m.DisplayName, machine, StringComparison.OrdinalIgnoreCase));
    if (machineConfig == null)
        return Results.NotFound(new { error = $"Equipo no encontrado: {machine}" });

    try
    {
        var guids = await extractor.ExtractAttendedGuidsAsync(
            machineConfig.CenterName, machineConfig.SitraName, dateOnly, ct);
        return Results.Ok(new { attendedGuids = guids });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Console.Error.WriteLine($"[attended-patients] Error: {ex.Message}");
        return Results.Ok(new { attendedGuids = Array.Empty<string>(), error = ex.Message });
    }
});

// ─── Helpers ─────────────────────────────────────────────────────────────────

static IResult? CheckOftechPassword(HttpContext ctx, IMemoryCache cache, string? password)
{
    var expected = Environment.GetEnvironmentVariable("MEVA_PWD_OFTECH_HASH");
    if (string.IsNullOrEmpty(expected))
        return Results.Json(new { error = "Perfil no configurado" }, statusCode: 503);

    var key = $"auth_rate_{ctx.Connection.RemoteIpAddress ?? (object)"unknown"}_oftech";
    cache.TryGetValue(key, out int fails);
    if (fails >= 5)
        return Results.Json(new { error = "Demasiados intentos. Espere 5 minutos." }, statusCode: 429);

    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password ?? "")));
    if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
    {
        cache.Set(key, fails + 1, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        return Results.Json(new { valid = false }, statusCode: 401);
    }
    cache.Remove(key);
    return null;
}

static List<AriaPlanSnapshot> ParseAriaOutput(AriaRunnerOutput output, IReadOnlyList<RtMachine> machines)
{
    string? MachineDisplayFor(string? ariaMachineId) => machines
        .FirstOrDefault(m => string.Equals(m.AriaName, ariaMachineId, StringComparison.OrdinalIgnoreCase))
        ?.DisplayName;

    var plans = new List<AriaPlanSnapshot>();
    foreach (var p in output.Patients)
    {
        if (!p.Found || p.ActivePlan == null) continue;
        var snap = new AriaPlanSnapshot
        {
            PatientId            = p.PatientId,
            PlannedMachineAriaId = p.ActivePlan.MachineAriaId,
            PlanStatus           = p.ActivePlan.Status,
            BeamType             = p.ActivePlan.BeamType,
            NumberOfFractions    = p.ActivePlan.NumberOfFractions,
            IrradiationModality  = p.ActivePlan.IrradiationModality,
            ExactBeamEnergy      = p.ActivePlan.ExactBeamEnergy
        };
        snap.PlannedMachineDisplayName = MachineDisplayFor(p.ActivePlan.MachineAriaId);

        var candidates = new List<AriaRunnerPlan> { p.ActivePlan };
        candidates.AddRange(p.AllPlans.Where(x => x.Status is "PlanApproval" or "TreatApproval"));
        snap.Plans = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x.PlanId))
            .GroupBy(x => x.PlanId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(x => new AriaPlanInfo
            {
                PlanId = x.PlanId,
                PlanName = x.PlanName,
                Status = x.Status,
                IrradiationModality = x.IrradiationModality,
                MachineDisplayName = MachineDisplayFor(x.MachineAriaId)
            })
            .ToList();

        plans.Add(snap);
    }
    return plans;
}

// ─── App run ──────────────────────────────────────────────────────────────────

app.Run();

record AuthVerifyRequest(string Profile, string Password);
record CreateReservationRequest(
    string PatientId, string PatientName, string? CenterName,
    string MachineDisplayName, string ReservedDate, string ReservedTime,
    string? Observations, string Username, string Password);
record DeleteReservationRequest(string Username, string Password);
record TbiMailEditRequest(string PatientName, DateOnly? TomographyDate, DateOnly? TreatmentStartDate, string? MachineDisplayName, string? RegisteredBy, int? TotalApplications);
