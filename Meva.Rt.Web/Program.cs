using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meva.Rt.Application;
using Meva.Rt.Core;
using Meva.Rt.Infrastructure.Aria;
using Meva.Rt.Infrastructure.SitraMed;
using Meva.Rt.Infrastructure.Storage;
using Meva.Rt.Web;

var builder = WebApplication.CreateBuilder(args);

var snapshotsDirectory = Environment.GetEnvironmentVariable("MEVA_DATA_DIR")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
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
builder.Services.AddSingleton<BootstrapService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(_ => new TurnReservationStore(snapshotsDirectory, businessDayCalc));

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
        .Where(s => string.Equals(s.GroupName, "Planificacion", StringComparison.OrdinalIgnoreCase))
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

    var ids = followUpExportIds
        .Concat(agendaExportIds)
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

    var ids = followUpExport
        .Concat(agendaExport)
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
            PlanStatus = patient.ActivePlan.Status,
            BeamType = patient.ActivePlan.BeamType,
            NumberOfFractions = patient.ActivePlan.NumberOfFractions,
            IrradiationModality = patient.ActivePlan.IrradiationModality,
            ExactBeamEnergy = patient.ActivePlan.ExactBeamEnergy
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

    var patientIds = followUpIdSet.Concat(agendaOnlyIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    var aria = (await ariaPlanResolver.ResolveAsync(patientIds, cancellationToken)).ToList();
    var ariaByPatient = aria.ToDictionary(x => x.PatientId, StringComparer.OrdinalIgnoreCase);

    foreach (var patient in data.FollowUpPatients)
    {
        if (!ariaByPatient.TryGetValue(patient.PatientId, out var plan)) continue;
        if (!string.IsNullOrWhiteSpace(plan.PlannedMachineDisplayName))
            patient.PlannedMachineDisplayName = plan.PlannedMachineDisplayName;
        if (!string.IsNullOrWhiteSpace(plan.BeamType))
            patient.BeamType = plan.BeamType;
        patient.NumberOfFractions = plan.NumberOfFractions;
        if (!string.IsNullOrWhiteSpace(plan.IrradiationModality))
            patient.IrradiationModality = plan.IrradiationModality;
        if (!string.IsNullOrWhiteSpace(plan.ExactBeamEnergy))
            patient.ExactBeamEnergy = plan.ExactBeamEnergy;
    }

    foreach (var item in data.AgendaItems)
    {
        if (string.IsNullOrWhiteSpace(item.SitraMedGuid)) continue;
        if (!guidHcMapApply.TryGetValue(item.SitraMedGuid, out var hc)) continue;
        if (!ariaByPatient.TryGetValue(hc, out var plan)) continue;
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

    var stageByCodeApply = configurationProvider.Configuration.Stages
        .ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
    data.StageSummary = data.FollowUpPatients
        .GroupBy(x => new { x.CenterName, x.StageCode })
        .Select(group =>
        {
            var stageDef = stageByCodeApply.GetValueOrDefault(group.Key.StageCode);
            var countable = group.Where(x => !x.IsLongWait).ToList();
            return new StageSummaryItem
            {
                CenterName = group.Key.CenterName,
                StageCode = group.Key.StageCode,
                StageGroupName = stageDef?.GroupName ?? group.First().StageGroupName,
                PatientCount = group.Count(),
                AverageDaysInStage = countable.Count > 0 ? countable.Average(x => x.DaysInStage) : 0,
                ExpectedDays = stageDef?.ExpectedDays ?? 0,
                DelayedCount = group.Count(x => x.IsDelayed),
                LongWaitCount = group.Count(x => x.IsLongWait)
            };
        })
        .OrderBy(x => x.CenterName)
        .ThenBy(x => x.StageGroupName)
        .ToList();

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

        var hcIds = followUpHcIds.Concat(agendaNewIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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

                var machines = configurationProvider.Configuration.Machines;
                var plans = new List<AriaPlanSnapshot>();
                foreach (var patient in output.Patients)
                {
                    if (!patient.Found || patient.ActivePlan == null) continue;
                    var snap = new AriaPlanSnapshot
                    {
                        PatientId = patient.PatientId,
                        PlannedMachineAriaId = patient.ActivePlan.MachineAriaId,
                        PlanStatus = patient.ActivePlan.Status,
                        BeamType = patient.ActivePlan.BeamType,
                        NumberOfFractions = patient.ActivePlan.NumberOfFractions,
                        IrradiationModality = patient.ActivePlan.IrradiationModality,
                        ExactBeamEnergy = patient.ActivePlan.ExactBeamEnergy
                    };
                    var machine = machines.FirstOrDefault(m =>
                        string.Equals(m.AriaName, patient.ActivePlan.MachineAriaId, StringComparison.OrdinalIgnoreCase));
                    snap.PlannedMachineDisplayName = machine?.DisplayName;
                    plans.Add(snap);
                }

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

    var machines2 = configurationProvider.Configuration.Machines;
    var plans2 = new List<AriaPlanSnapshot>();
    foreach (var patient in runnerOutput2.Patients)
    {
        if (!patient.Found || patient.ActivePlan == null) continue;
        var snap = new AriaPlanSnapshot
        {
            PatientId = patient.PatientId,
            PlannedMachineAriaId = patient.ActivePlan.MachineAriaId,
            PlanStatus = patient.ActivePlan.Status,
            BeamType = patient.ActivePlan.BeamType,
            NumberOfFractions = patient.ActivePlan.NumberOfFractions,
            IrradiationModality = patient.ActivePlan.IrradiationModality,
            ExactBeamEnergy = patient.ActivePlan.ExactBeamEnergy
        };
        var machine = machines2.FirstOrDefault(m =>
            string.Equals(m.AriaName, patient.ActivePlan.MachineAriaId, StringComparison.OrdinalIgnoreCase));
        snap.PlannedMachineDisplayName = machine?.DisplayName;
        plans2.Add(snap);
    }

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
            var maxScrapedDate = Directory.Exists(snapshotsDirectory)
                ? Directory.GetFiles(snapshotsDirectory, "agenda_????-??-??.json")
                    .Select(f => Path.GetFileNameWithoutExtension(f).Replace("agenda_", ""))
                    .Where(d => DateOnly.TryParse(d, out _))
                    .Select(d => DateOnly.Parse(d))
                    .Where(d => d > today)
                    .DefaultIfEmpty(today)
                    .Max()
                : today;

            foreach (var patient in bootstrap.FollowUpPatients)
            {
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

                var remainingDays = stages.Skip(stageIdx).Sum(s => s.ExpectedDays);
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

                slots.Add(new AgendaSlotDto
                {
                    CenterName = patient.CenterName ?? string.Empty,
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
    TypedResults.Ok(await reservationStore.LoadAllActiveAsync(ct)));

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

    var expectedHash = Environment.GetEnvironmentVariable("MEVA_PWD_OFTECH_HASH");
    if (string.IsNullOrEmpty(expectedHash))
        return Results.Json(new { error = "Perfil no configurado" }, statusCode: 503);

    var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var rateCacheKey = $"auth_rate_{ip}_oftech";
    memoryCache.TryGetValue(rateCacheKey, out int failCount);
    if (failCount >= 5)
        return Results.Json(new { error = "Demasiados intentos. Espere 5 minutos." }, statusCode: 429);

    var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.Password)));
    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
    {
        memoryCache.Set(rateCacheKey, failCount + 1,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        return Results.Json(new { valid = false }, statusCode: 401);
    }
    memoryCache.Remove(rateCacheKey);

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

    var expectedHash = Environment.GetEnvironmentVariable("MEVA_PWD_OFTECH_HASH");
    if (string.IsNullOrEmpty(expectedHash))
        return Results.Json(new { error = "Perfil no configurado" }, statusCode: 503);

    var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var rateCacheKey = $"auth_rate_{ip}_oftech";
    memoryCache.TryGetValue(rateCacheKey, out int failCount);
    if (failCount >= 5)
        return Results.Json(new { error = "Demasiados intentos. Espere 5 minutos." }, statusCode: 429);

    var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.Password)));
    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
    {
        memoryCache.Set(rateCacheKey, failCount + 1,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        return Results.Json(new { valid = false }, statusCode: 401);
    }
    memoryCache.Remove(rateCacheKey);

    await reservationStore.DeleteByIdAsync(reservationId, ct);
    return TypedResults.NoContent();
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

// ─── App run ──────────────────────────────────────────────────────────────────

app.Run();

record AuthVerifyRequest(string Profile, string Password);
record CreateReservationRequest(
    string PatientId, string PatientName, string? CenterName,
    string MachineDisplayName, string ReservedDate, string ReservedTime,
    string? Observations, string Username, string Password);
record DeleteReservationRequest(string Username, string Password);
