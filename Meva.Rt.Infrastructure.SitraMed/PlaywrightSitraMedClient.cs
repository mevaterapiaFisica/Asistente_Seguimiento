using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.SitraMed;

public sealed class PlaywrightSitraMedClient
{
    public const string LoginUrl = "https://sitramed.mevaterapia.com.ar/session/new";
    public const string FollowUpUrl = "https://sitramed.mevaterapia.com.ar/follow_up_search";
    public const string MachineAgendaUrl = "https://sitramed.mevaterapia.com.ar/reception/appointments/machine";
    public const string TomographAgendaUrl = "https://sitramed.mevaterapia.com.ar/reception/appointments/tomograph";

    private const int MaxParallelPages = 2;

    private readonly SitraMedRuntimeOptions _options;

    public PlaywrightSitraMedClient(SitraMedRuntimeOptions options)
    {
        _options = options;
    }

    public bool CanUseRemoteScraping()
    {
        return !string.IsNullOrWhiteSpace(_options.Username) && !string.IsNullOrWhiteSpace(_options.Password);
    }

    public async Task<IReadOnlyList<FollowUpHtmlSnapshot>> DownloadFollowUpPagesAsync(
        IReadOnlyList<RtCenter> centers,
        IReadOnlyList<ProcessStageDefinition> stages,
        CancellationToken cancellationToken)
    {
        if (!CanUseRemoteScraping())
            return Array.Empty<FollowUpHtmlSnapshot>();

        var combos = centers
            .SelectMany(c => stages.Where(s => s.Enabled).Select(s => (center: c, stage: s)))
            .ToList();

        await using var session = await CreateLoggedPageAsync(cancellationToken);

        // Todas las páginas comparten el mismo contexto (cookies de sesión) — un solo login.
        var sem = new SemaphoreSlim(MaxParallelPages);
        var tasks = combos.Select(async combo =>
        {
            await sem.WaitAsync(cancellationToken);
            var page = await session.Context.NewPageAsync();
            page.SetDefaultTimeout(_options.TimeoutSeconds * 1000);
            page.SetDefaultNavigationTimeout(_options.TimeoutSeconds * 1000);
            try
            {
                var download = await DownloadFollowUpAsync(page, combo.center, combo.stage, cancellationToken);
                return new FollowUpHtmlSnapshot
                {
                    CenterId = combo.center.Id,
                    CenterName = combo.center.Name,
                    StageCode = combo.stage.Code,
                    StageMicroStatus = combo.stage.SitraMicroStatus,
                    Html = download.Html,
                    DomRows = download.DomRows.Count > 0 ? download.DomRows : null
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[SitraMed] Seguimiento falló {combo.center.Name}/{combo.stage.Code}: {ex.Message}");
                return new FollowUpHtmlSnapshot
                {
                    CenterId = combo.center.Id,
                    CenterName = combo.center.Name,
                    StageCode = combo.stage.Code,
                    StageMicroStatus = combo.stage.SitraMicroStatus,
                    Html = string.Empty
                };
            }
            finally
            {
                await page.CloseAsync();
                sem.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<AgendaHtmlSnapshot>>> DownloadAgendaPagesForDatesAsync(
        IReadOnlyList<RtMachine> machines,
        IReadOnlyList<DateOnly> dates,
        CancellationToken cancellationToken)
    {
        if (!CanUseRemoteScraping() || dates.Count == 0 || machines.Count == 0)
            return new Dictionary<DateOnly, IReadOnlyList<AgendaHtmlSnapshot>>();

        var combos = dates
            .SelectMany(d => machines.Select(m => (date: d, machine: m)))
            .ToList();

        await using var session = await CreateLoggedPageAsync(cancellationToken);

        var sem = new SemaphoreSlim(MaxParallelPages);
        var tasks = combos.Select(async combo =>
        {
            await sem.WaitAsync(cancellationToken);
            var page = await session.Context.NewPageAsync();
            page.SetDefaultTimeout(_options.TimeoutSeconds * 1000);
            page.SetDefaultNavigationTimeout(_options.TimeoutSeconds * 1000);
            try
            {
                var html = await DownloadAgendaHtmlAsync(page, combo.machine, combo.date, cancellationToken);
                var domSnapshots = await TryExtractAgendaDomAsync(page, combo.machine, combo.date, cancellationToken);
                return new AgendaHtmlSnapshot
                {
                    CenterName = combo.machine.CenterName,
                    MachineDisplayName = combo.machine.DisplayName,
                    AgendaDate = combo.date,
                    Html = html,
                    DomSnapshots = domSnapshots.Count > 0 ? domSnapshots : null
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[SitraMed] Agenda falló {combo.machine.DisplayName}/{combo.date}: {ex.Message}");
                return new AgendaHtmlSnapshot
                {
                    CenterName = combo.machine.CenterName,
                    MachineDisplayName = combo.machine.DisplayName,
                    AgendaDate = combo.date,
                    Html = string.Empty,
                    HasScrapingError = true
                };
            }
            finally
            {
                await page.CloseAsync();
                sem.Release();
            }
        });

        var all = await Task.WhenAll(tasks);
        return all
            .GroupBy(s => s.AgendaDate)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AgendaHtmlSnapshot>)g.ToList());
    }

    public async Task<IReadOnlyList<AgendaHtmlSnapshot>> DownloadAgendaPagesAsync(
        IReadOnlyList<RtMachine> machines,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        if (!CanUseRemoteScraping())
            return Array.Empty<AgendaHtmlSnapshot>();

        await using var session = await CreateLoggedPageAsync(cancellationToken);

        var sem = new SemaphoreSlim(MaxParallelPages);
        var tasks = machines.Select(async machine =>
        {
            await sem.WaitAsync(cancellationToken);
            var page = await session.Context.NewPageAsync();
            page.SetDefaultTimeout(_options.TimeoutSeconds * 1000);
            page.SetDefaultNavigationTimeout(_options.TimeoutSeconds * 1000);
            try
            {
                var html = await DownloadAgendaHtmlAsync(page, machine, date, cancellationToken);
                var domSnapshots = await TryExtractAgendaDomAsync(page, machine, date, cancellationToken);
                return new AgendaHtmlSnapshot
                {
                    CenterName = machine.CenterName,
                    MachineDisplayName = machine.DisplayName,
                    AgendaDate = date,
                    Html = html,
                    DomSnapshots = domSnapshots.Count > 0 ? domSnapshots : null
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[SitraMed] Agenda falló {machine.DisplayName}/{date}: {ex.Message}");
                return new AgendaHtmlSnapshot
                {
                    CenterName = machine.CenterName,
                    MachineDisplayName = machine.DisplayName,
                    AgendaDate = date,
                    Html = string.Empty,
                    HasScrapingError = true
                };
            }
            finally
            {
                await page.CloseAsync();
                sem.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<string?> DownloadAgendaPageHtmlForMachineAsync(
        string centerName, string sitraName, DateOnly date, CancellationToken cancellationToken)
    {
        if (!CanUseRemoteScraping()) return null;

        await using var session = await CreateLoggedPageAsync(cancellationToken);
        var page = await session.Context.NewPageAsync();
        page.SetDefaultTimeout(_options.TimeoutSeconds * 1000);
        page.SetDefaultNavigationTimeout(_options.TimeoutSeconds * 1000);
        try
        {
            var machine = new RtMachine { CenterName = centerName, SitraName = sitraName, DisplayName = sitraName };
            return await DownloadAgendaHtmlAsync(page, machine, date, cancellationToken);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async Task<IReadOnlyList<TomographAgendaHtmlSnapshot>> DownloadTomographAgendaPagesAsync(
        IReadOnlyList<RtTomograph> tomographs,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        if (!CanUseRemoteScraping() || tomographs.Count == 0)
            return Array.Empty<TomographAgendaHtmlSnapshot>();

        await using var page = await CreateLoggedPageAsync(cancellationToken);
        var results = new List<TomographAgendaHtmlSnapshot>();

        foreach (var tomograph in tomographs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var html = await DownloadTomographAgendaHtmlAsync(page.Page, tomograph, date, cancellationToken);
            var domSnapshots = await TryExtractTomographAgendaDomAsync(page.Page, tomograph, date, cancellationToken);

            results.Add(new TomographAgendaHtmlSnapshot
            {
                CenterName            = tomograph.CenterName,
                TomographDisplayName  = tomograph.DisplayName,
                AgendaDate            = date,
                Html                  = html,
                DomSnapshots          = domSnapshots.Count > 0 ? domSnapshots : null
            });
        }

        return results;
    }

    public async Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<TomographAgendaHtmlSnapshot>>> DownloadTomographAgendaPagesForDatesAsync(
        IReadOnlyList<RtTomograph> tomographs,
        IReadOnlyList<DateOnly> dates,
        CancellationToken cancellationToken)
    {
        if (!CanUseRemoteScraping() || dates.Count == 0 || tomographs.Count == 0)
            return new Dictionary<DateOnly, IReadOnlyList<TomographAgendaHtmlSnapshot>>();

        await using var page = await CreateLoggedPageAsync(cancellationToken);
        var result = new Dictionary<DateOnly, IReadOnlyList<TomographAgendaHtmlSnapshot>>();

        foreach (var date in dates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dateResults = new List<TomographAgendaHtmlSnapshot>();

            foreach (var tomograph in tomographs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var html = await DownloadTomographAgendaHtmlAsync(page.Page, tomograph, date, cancellationToken);
                var domSnapshots = await TryExtractTomographAgendaDomAsync(page.Page, tomograph, date, cancellationToken);

                dateResults.Add(new TomographAgendaHtmlSnapshot
                {
                    CenterName           = tomograph.CenterName,
                    TomographDisplayName = tomograph.DisplayName,
                    AgendaDate           = date,
                    Html                 = html,
                    DomSnapshots         = domSnapshots.Count > 0 ? domSnapshots : null
                });
            }

            result[date] = dateResults;
        }

        return result;
    }

    public async Task<ScrapingTestResult> RunFollowUpTestAsync(
        RtCenter center,
        ProcessStageDefinition stage,
        CancellationToken cancellationToken)
    {
        if (!CanUseRemoteScraping())
        {
            return new ScrapingTestResult
            {
                Success = false,
                Message = "Faltan credenciales MEVA_SITRAMED_USER / MEVA_SITRAMED_PASSWORD."
            };
        }

        await using var page = await CreateLoggedPageAsync(cancellationToken);
        var download = await DownloadFollowUpAsync(page.Page, center, stage, cancellationToken);
        var title = await page.Page.TitleAsync();
        var finalUrl = page.Page.Url;

        return new ScrapingTestResult
        {
            Success = true,
            Message = "Login y descarga OK.",
            Url = finalUrl,
            PageTitle = title,
            HtmlLength = download.Html.Length,
            HtmlPreview = download.Html.Length > 500 ? download.Html[..500] : download.Html,
            FollowUpSearchAction = download.SearchAction,
            SelectedCenterValue = download.CenterValueSelected,
            SelectedMicroStatusValue = download.MicroStatusValueSelected,
            CapturedFilePath = download.DiagnosticDirectory
        };
    }

    public async Task<ScrapingTestResult> RunAgendaTestAsync(
        RtMachine machine,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        if (!CanUseRemoteScraping())
        {
            return new ScrapingTestResult
            {
                Success = false,
                Message = "Faltan credenciales MEVA_SITRAMED_USER / MEVA_SITRAMED_PASSWORD."
            };
        }

        await using var page = await CreateLoggedPageAsync(cancellationToken);
        var html = await DownloadAgendaHtmlAsync(page.Page, machine, date, cancellationToken);
        var domRows = await TryExtractAgendaDomAsync(page.Page, machine, date, cancellationToken);
        var title = await page.Page.TitleAsync();
        var finalUrl = page.Page.Url;
        var capturedFilePath = SaveAgendaHtmlCapture(machine, date, html);

        return new ScrapingTestResult
        {
            Success = true,
            Message = domRows.Count > 0
                ? "Login y descarga de agenda OK (parse DOM detectado)."
                : "Login y descarga de agenda OK (sin parse DOM, revisar HTML).",
            Url = finalUrl,
            PageTitle = title,
            HtmlLength = html.Length,
            HtmlPreview = html.Length > 500 ? html[..500] : html,
            AgendaDomRows = domRows.Count,
            CapturedFilePath = capturedFilePath
        };
    }

    public async Task<ScrapingTestResult> RunTomographTestAsync(
        RtTomograph tomograph,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        if (!CanUseRemoteScraping())
        {
            return new ScrapingTestResult
            {
                Success = false,
                Message = "Faltan credenciales MEVA_SITRAMED_USER / MEVA_SITRAMED_PASSWORD."
            };
        }

        await using var page = await CreateLoggedPageAsync(cancellationToken);

        // Capture network requests made after login to understand what AJAX calls the page makes
        var capturedRequests = new System.Collections.Concurrent.ConcurrentBag<string>();
        page.Page.Request += (_, req) =>
        {
            if (!req.Url.Contains(".js") && !req.Url.Contains(".css") &&
                !req.Url.Contains("fonts") && !req.Url.Contains(".ico") &&
                !req.Url.Contains(".png") && !req.Url.Contains(".woff"))
            {
                capturedRequests.Add($"{req.Method} {req.Url}");
            }
        };

        await page.Page.GotoAsync(TomographAgendaUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        try { await page.Page.WaitForSelectorAsync("#search_center_id", new PageWaitForSelectorOptions { Timeout = 10000 }); } catch { }

        // Snapshot center and tomograph options before selection
        var centerOpts = await page.Page.EvaluateAsync<string>(
            "() => JSON.stringify(Array.from(document.querySelectorAll('#search_center_id option')).map(o => o.textContent?.trim()))");

        // Step 1: select center
        await SelectFirstByLabelAsync(page.Page, new[] { "#search_center_id", "select[name='search[center_id]']" }, tomograph.CenterName);
        await page.Page.WaitForTimeoutAsync(600);

        var tomoOptsBefore = await page.Page.EvaluateAsync<string>(
            "() => JSON.stringify(Array.from(document.querySelectorAll('#search_tomograph_id option')).map(o => o.textContent?.trim()))");

        // Step 2: set date (BEFORE selecting tomograph — on the live page, setting date after
        //         tomograph resets the tomograph selection)
        var dateStr = date.ToString("dd/MM/yyyy");
        await page.Page.EvaluateAsync("""
            (date) => {
                const tSel = document.querySelector('#search_tomograph_id');
                const form = tSel?.closest('form');
                if (!form) return;
                const di = form.querySelector('#search_date') ?? form.querySelector('input[name="search[date]"]');
                if (di) {
                    di.value = date;
                    di.dispatchEvent(new Event('input',  { bubbles: true }));
                    di.dispatchEvent(new Event('change', { bubbles: true }));
                }
            }
            """, dateStr);
        await page.Page.WaitForTimeoutAsync(400);

        // Step 3: select tomograph — this onChange should trigger the results AJAX
        await SelectFirstByLabelAsync(page.Page, new[] { "#search_tomograph_id", "select[name='search[tomograph_id]']" }, tomograph.SitraName);

        // Capture form info after all selections
        var tomoOpts = await page.Page.EvaluateAsync<string>(
            "() => JSON.stringify(Array.from(document.querySelectorAll('#search_tomograph_id option')).map(o => o.textContent?.trim()))");

        var formInfo = await page.Page.EvaluateAsync<string>("""
            () => {
                const tSel = document.querySelector('#search_tomograph_id');
                const form = tSel?.closest('form');
                if (!form) return JSON.stringify({error: 'no filter form found near #search_tomograph_id'});
                const di = form.querySelector('#search_date') ?? form.querySelector('input[name="search[date]"]');
                return JSON.stringify({
                    action: form.action, method: form.method,
                    hasDateInput: !!di, dateId: di?.id, dateName: di?.name, dateType: di?.type,
                    dateValue: di?.value,
                    centerVal: document.querySelector('#search_center_id')?.value,
                    tomoVal:   document.querySelector('#search_tomograph_id')?.value,
                    iframes:   document.querySelectorAll('iframe').length,
                    hasSubmitBtn: !!form.querySelector('button[type=submit], input[type=submit]')
                });
            }
            """);

        await page.Page.WaitForTimeoutAsync(400);
        try { await page.Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = _options.TimeoutSeconds * 1000 }); } catch { }
        try { await page.Page.WaitForSelectorAsync("table tbody tr", new PageWaitForSelectorOptions { Timeout = 15000 }); } catch { }

        var title    = await page.Page.TitleAsync();
        var finalUrl = page.Page.Url;
        var html     = await page.Page.ContentAsync();

        // Collect raw rows without keyword filter
        var rawRows = new List<string>();
        foreach (var selector in new[] { "#tomographDrag tbody tr", "#machineDrag tbody tr", "table.table tbody tr", "table tbody tr" })
        {
            var loc = page.Page.Locator(selector);
            int count; try { count = await loc.CountAsync(); } catch { count = 0; }
            if (count == 0) continue;
            for (var i = 0; i < Math.Min(count, 20); i++)
            {
                var cells   = await loc.Nth(i).Locator("td").AllInnerTextsAsync();
                var trimmed = cells.Select(c => string.Join(' ', c.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim()).ToArray();
                rawRows.Add($"[{selector}] " + string.Join(" | ", trimmed));
            }
            break;
        }

        // DOM snapshot
        var domInfo = await page.Page.EvaluateAsync<string>("""
            () => {
                const frames  = Array.from(document.querySelectorAll('turbo-frame')).map(f => ({
                    id: f.id, src: f.getAttribute('src') ?? '', rows: f.querySelectorAll('tr').length,
                    html: f.innerHTML.substring(0, 400)
                }));
                const tables  = Array.from(document.querySelectorAll('table')).map(t => ({
                    id: t.id, cls: t.className, rows: t.rows.length, text: t.innerText?.substring(0, 200)
                }));
                const iframes = Array.from(document.querySelectorAll('iframe')).map(f => f.src);
                const bodyText = document.body.innerText?.substring(0, 600);
                return JSON.stringify({ frames, tables, iframes, bodyText }, null, 2);
            }
            """);

        var preview = JsonSerializer.Serialize(new
        {
            formInfo         = JsonSerializer.Deserialize<object>(formInfo       ?? "{}"),
            centerOpts       = JsonSerializer.Deserialize<object>(centerOpts     ?? "[]"),
            tomoOptsAfterCtr = JsonSerializer.Deserialize<object>(tomoOptsBefore ?? "[]"),
            tomoOptsAfterDate= JsonSerializer.Deserialize<object>(tomoOpts      ?? "[]"),
            requests         = capturedRequests.OrderBy(x => x).ToArray(),
            dom              = JsonSerializer.Deserialize<object>(domInfo        ?? "{}")
        }, new JsonSerializerOptions { WriteIndented = true });

        return new ScrapingTestResult
        {
            Success = true,
            Message = rawRows.Count > 0
                ? $"OK. {rawRows.Count} filas en el DOM (sin filtro)."
                : "OK pero 0 filas en el DOM. Ver HtmlPreview.",
            Url       = finalUrl,
            PageTitle = title,
            HtmlLength = html.Length,
            HtmlPreview = preview,
            RawRowSamples = rawRows
        };
    }

    private static readonly Regex HcFormatRegex = new(
        @"^\d{1,3}-\d{4,7}-\d{1,3}$",
        RegexOptions.Compiled);

    public async Task<IReadOnlyDictionary<string, string>> FetchHcForGuidsAsync(
        IEnumerable<string> guids,
        CancellationToken cancellationToken)
    {
        var guidList = guids.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (guidList.Count == 0 || !CanUseRemoteScraping())
            return new Dictionary<string, string>();

        await using var session = await CreateLoggedPageAsync(cancellationToken);
        var page = session.Page;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var guid in guidList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var url = $"https://sitramed.mevaterapia.com.ar/medical_histories/{guid}/overview";
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

                string? hc = null;
                try
                {
                    hc = await page.EvaluateAsync<string?>(
                        """
                        () => {
                            const re = /\b(\d{1,3}-\d{4,7}-\d{1,3})\b/;
                            // Paso 1: match exacto en elementos hoja comunes (rápido)
                            const candidates = Array.from(document.querySelectorAll(
                                'td, dd, dt, span, p, h1, h2, h3, h4, strong, b, th, li, label'));
                            for (const el of candidates) {
                                const t = (el.textContent || '').trim();
                                if (/^\d{1,3}-\d{4,7}-\d{1,3}$/.test(t)) return t;
                            }
                            // Paso 2: buscar el patrón dentro de cualquier nodo de texto (TreeWalker)
                            const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                            let node;
                            while ((node = walker.nextNode())) {
                                const m = (node.textContent || '').match(re);
                                if (m) return m[1];
                            }
                            return null;
                        }
                        """);
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(hc) && HcFormatRegex.IsMatch(hc))
                    result[guid] = hc;
            }
            catch { }
        }

        return result;
    }

    private async Task<PlaywrightSession> CreateLoggedPageAsync(CancellationToken cancellationToken)
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _options.Headless,
            Args = _options.Headless ? ["--disable-gpu", "--disable-dev-shm-usage"] : []
        });

        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(_options.TimeoutSeconds * 1000);
        page.SetDefaultNavigationTimeout(_options.TimeoutSeconds * 1000);

        await LoginAsync(page, cancellationToken);
        return new PlaywrightSession(playwright, browser, context, page);
    }

    private async Task LoginAsync(IPage page, CancellationToken cancellationToken)
    {
        await page.GotoAsync(LoginUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await FillFirstAsync(page, new[]
        {
            "#user_email",
            "input[name='email']",
            "input[name='session[email]']",
            "input[type='email']"
        }, _options.Username);

        await FillFirstAsync(page, new[]
        {
            "#user_password",
            "input[name='password']",
            "input[name='session[password]']",
            "input[type='password']"
        }, _options.Password);

        await ClickFirstAsync(page, new[]
        {
            ".is-flex > .button:nth-child(1)",
            "button[type='submit']",
            "input[type='submit']",
            "button:has-text('Ingresar')",
            "button:has-text('Iniciar sesión')"
        });

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        if (page.Url.Contains("/session/new", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("El login no avanzo de la pantalla de ingreso.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<FollowUpDownloadResult> DownloadFollowUpAsync(IPage page, RtCenter center, ProcessStageDefinition stage, CancellationToken cancellationToken)
    {
        await page.GotoAsync(FollowUpUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        string? diagnosticDirectory = null;

        await SelectFirstByLabelAsync(page, new[]
        {
            "#filters_attention_center_id",
            "select[name='filters[attention_center_id]']"
        }, center.Name);

        // Intenta seleccionar por value; si falla, intenta por label visible.
        var microStatusSelectors = new[]
        {
            "#filters_micro_status",
            "select[name='filters[micro_status]']"
        };
        var microStatusSelected = await SelectFirstSafeAsync(page, microStatusSelectors, stage.SitraMicroStatus, byValue: true);
        if (!microStatusSelected)
        {
            await SelectFirstByLabelAsync(page, microStatusSelectors, stage.DisplayName);
        }

        // Capturar HTML DESPUÉS de los filtros pero ANTES del botón, para que la
        // comparación en WaitForFollowUpSearchEffectAsync refleje realmente el efecto de la búsqueda.
        var htmlBeforeSearch = await page.ContentAsync();

        if (_options.EnableDiagnostics)
        {
            diagnosticDirectory = EnsureDiagnosticsDirectory("followup", center.Name, stage.Code);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(diagnosticDirectory, "01_before_search.png"),
                FullPage = true
            });
            await File.WriteAllTextAsync(Path.Combine(diagnosticDirectory, "01_before_search.html"), await page.ContentAsync(), cancellationToken);
        }

        var selectedCenterValue = await GetSelectedOptionValueAsync(page, new[]
        {
            "#filters_attention_center_id",
            "select[name='filters[attention_center_id]']"
        });
        var selectedMicroStatusValue = await GetSelectedOptionValueAsync(page, new[]
        {
            "#filters_micro_status",
            "select[name='filters[micro_status]']"
        });

        var searchAction = await TriggerFollowUpSearchAsync(page);

        // Dar tiempo a que la navegación/AJAX dispare antes de esperar NetworkIdle.
        // Sin este delay, WaitForLoadStateAsync puede retornar inmediatamente (si la red
        // estaba idle en el momento del click) y ContentAsync falla cuando la nav arranca.
        await page.WaitForTimeoutAsync(400);
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = _options.TimeoutSeconds * 1000 });
        }
        catch (TimeoutException) { }

        await WaitForFollowUpSearchEffectAsync(page, htmlBeforeSearch, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var domRows = await TryExtractFollowUpDomAsync(page, center, stage, cancellationToken);
        var htmlAfterSearch = await SafeGetContentAsync(page);

        if (diagnosticDirectory != null)
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(diagnosticDirectory, "02_after_search.png"),
                FullPage = true
            });
            await File.WriteAllTextAsync(Path.Combine(diagnosticDirectory, "02_after_search.html"), htmlAfterSearch, cancellationToken);
            var metadata = JsonSerializer.Serialize(new
            {
                TimestampUtc = DateTime.UtcNow,
                Center = center.Name,
                StageCode = stage.Code,
                StageMicroStatus = stage.SitraMicroStatus,
                SearchAction = searchAction,
                SelectedCenterValue = selectedCenterValue,
                SelectedMicroStatusValue = selectedMicroStatusValue,
                FinalUrl = page.Url,
                PageTitle = await page.TitleAsync()
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(diagnosticDirectory, "metadata.json"), metadata, cancellationToken);
        }

        return new FollowUpDownloadResult
        {
            Html = htmlAfterSearch ?? string.Empty,
            SearchAction = searchAction,
            CenterValueSelected = selectedCenterValue,
            MicroStatusValueSelected = selectedMicroStatusValue,
            DiagnosticDirectory = diagnosticDirectory,
            DomRows = domRows
        };
    }

    private async Task<string> DownloadAgendaHtmlAsync(IPage page, RtMachine machine, DateOnly date, CancellationToken cancellationToken)
    {
        await page.GotoAsync(MachineAgendaUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await SelectFirstByLabelAsync(page, new[]
        {
            "#search_center_id",
            "select[name='search[center_id]']"
        }, machine.CenterName);

        // Esperar que el dropdown de equipo se popule vía AJAX antes de seleccionar.
        // Sin este wait, la selección de centro dispara el request AJAX pero el dropdown
        // todavía está vacío cuando intentamos seleccionar el equipo → opción no encontrada
        // → form se submitea sin equipo → 0 pacientes.
        try
        {
            await page.WaitForFunctionAsync(
                "sel => (document.querySelector(sel)?.options?.length ?? 0) > 1",
                "#search_machine_id",
                new PageWaitForFunctionOptions { Timeout = 5000 });
        }
        catch (TimeoutException) { }

        await SelectFirstByLabelAsync(page, new[]
        {
            "#search_machine_id",
            "select[name='search[machine_id]']"
        }, machine.SitraName);

        await FillFirstAsync(page, new[]
        {
            "#search_date",
            "input[name='search[date]']"
        }, date.ToString("yyyy-MM-dd"));

        await page.Keyboard.PressAsync("Enter");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForAnyAsync(page, new[]
        {
            "#machineDrag",
            "#machine_drag",
            "#machineDrag tbody tr",
            "#machine_drag tbody tr",
            "table tbody tr",
            "body"
        });
        cancellationToken.ThrowIfCancellationRequested();
        return await page.ContentAsync();
    }

    private async Task<List<MachineAppointmentSnapshot>> TryExtractAgendaDomAsync(
        IPage page,
        RtMachine machine,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        foreach (var selector in new[]
                 {
                     "#machineDrag tbody tr",
                     "#machine_drag tbody tr",
                     "#machineDrag table tbody tr",
                     "#machine_drag table tbody tr"
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locator = page.Locator(selector);
            int count;
            try
            {
                count = await locator.CountAsync();
            }
            catch
            {
                continue;
            }

            if (count == 0)
            {
                continue;
            }

            var parsed = await ParseAgendaRowsAsync(locator, count, machine, date);
            if (parsed.Count > 0)
            {
                return parsed;
            }
        }

        return [];
    }

    private async Task<List<MachineAppointmentSnapshot>> ParseAgendaRowsAsync(
        ILocator rowLocator,
        int rowCount,
        RtMachine machine,
        DateOnly date)
    {
        var list = new List<MachineAppointmentSnapshot>();
        for (var i = 0; i < rowCount; i++)
        {
            var row = rowLocator.Nth(i);

            // Skip rows with data-type containing "Finalizado" — these are past-treatment remnants
            // rendered in grey by SitraMed (e.g. "Tratamiento Estima Finalizado").
            // SitraMed uses CSS classes for color, not inline styles, so style-attribute checks don't apply here.
            var dataType = string.Empty;
            try { dataType = await row.GetAttributeAsync("data-type") ?? string.Empty; } catch { }
            if (dataType.Contains("Finalizado", StringComparison.OrdinalIgnoreCase)) continue;

            var cells = await row.Locator("td").AllInnerTextsAsync();
            if (cells.Count == 0)
            {
                continue;
            }

            var trimmed = cells.Select(static c => string.Join(' ', c.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Select(static s => s.Trim()).ToArray();
            if (trimmed.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (LooksLikeAgendaHeader(trimmed))
            {
                continue;
            }

            // Skip rows whose estimated end date (fechaFin, offset+9) is before the requested date.
            // This catches grey rows: treatment already finished, slot still appears in agenda.
            {
                var fOff = 0;
                while (fOff < trimmed.Length - 2 && string.IsNullOrWhiteSpace(trimmed[fOff])) fOff++;
                if (trimmed.Length > fOff + 9)
                {
                    var fechaCell = trimmed[fOff + 9];
                    if (!string.IsNullOrWhiteSpace(fechaCell) &&
                        DateOnly.TryParseExact(fechaCell.Trim(),
                            new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" },
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var rowDate) &&
                        rowDate < date)
                        continue;
                }
            }

            var mapped = MapAgendaCells(trimmed);
            if (mapped == null || string.IsNullOrWhiteSpace(mapped.Value.PatientName) || mapped.Value.PatientName == "-")
            {
                continue;
            }

            var agendaGuid = string.Empty;
            try
            {
                var link = row.Locator("a[href*='overview']").First;
                if (await link.CountAsync() > 0)
                {
                    var href = await link.GetAttributeAsync("href") ?? string.Empty;
                    var gm = MedicalHistoryIdRegex.Match(href);
                    if (gm.Success) agendaGuid = gm.Groups[1].Value;
                }
            }
            catch { }

            list.Add(new MachineAppointmentSnapshot
            {
                CenterName = machine.CenterName,
                MachineName = machine.DisplayName,
                PatientName = mapped.Value.PatientName,
                AgendaDate = date,
                StartTime = mapped.Value.StartTime,
                EndTime = mapped.Value.EndTime,
                Treatment = mapped.Value.Treatment,
                SitraMedGuid = string.IsNullOrEmpty(agendaGuid) ? null : agendaGuid,
                Priority = mapped.Value.Priority
            });
        }

        return list;
    }

    private static bool LooksLikeAgendaHeader(string[] cells)
    {
        var joined = string.Join(' ', cells).ToLowerInvariant();
        return joined.Contains("paciente", StringComparison.OrdinalIgnoreCase)
               && (joined.Contains("inicio", StringComparison.OrdinalIgnoreCase)
                   || joined.Contains("hora", StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Columnas SitraMed: [signs_div?] inicio, paciente, equipo, prioridad,
    /// observaciones, institución, tipo, tratamiento, fecha inicio, fecha fin, hora fin, acciones.
    /// La primera columna (signs) tiene innerText vacío aunque contenga HTML; se saltea.
    /// </summary>
    private static (string PatientName, string StartTime, string EndTime, string Treatment, int? Priority)? MapAgendaCells(string[] cells)
    {
        if (cells.Length < 2) return null;

        // Saltear celdas iniciales vacías (p.ej. la columna signs div)
        var offset = 0;
        while (offset < cells.Length - 2 && string.IsNullOrWhiteSpace(cells[offset]))
            offset++;

        var remaining = cells.Length - offset;

        if (remaining >= 11)
        {
            var start     = cells[offset];
            var patient   = cells[offset + 1];
            var treatment = offset + 7 < cells.Length ? cells[offset + 7] : string.Empty;
            var end       = offset + 10 < cells.Length ? cells[offset + 10] : string.Empty;
            var prioStr   = offset + 3 < cells.Length ? cells[offset + 3] : string.Empty;
            int? priority = int.TryParse(prioStr, out var pv) ? pv : (int?)null;
            return (patient, start, end, treatment, priority);
        }

        if (remaining >= 4)
        {
            // Heurística: primera celda con letras que no parezca hora ni fecha
            var patientIdx = -1;
            for (var i = offset; i < cells.Length; i++)
            {
                if (LooksLikePersonName(cells[i])) { patientIdx = i; break; }
            }
            if (patientIdx < 0) patientIdx = offset + 1;

            var patient   = cells[patientIdx];
            var start     = patientIdx > offset ? cells[offset] : cells.ElementAtOrDefault(offset + 1) ?? string.Empty;
            var end       = cells.ElementAtOrDefault(cells.Length - 2) ?? string.Empty;
            var treatment = cells.ElementAtOrDefault(Math.Min(offset + 3, cells.Length - 1)) ?? string.Empty;
            return (patient, start, end, treatment, null);
        }

        return null;
    }

    private async Task<string> DownloadTomographAgendaHtmlAsync(IPage page, RtTomograph tomograph, DateOnly date, CancellationToken cancellationToken)
    {
        await page.GotoAsync(TomographAgendaUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        try { await page.WaitForSelectorAsync("#search_center_id", new PageWaitForSelectorOptions { Timeout = 10000 }); } catch { }

        // 1. Select center so the tomograph dropdown populates.
        await SelectFirstByLabelAsync(page, new[]
        {
            "#search_center_id",
            "select[name='search[center_id]']"
        }, tomograph.CenterName);

        await page.WaitForTimeoutAsync(600);

        // 2. Set the date BEFORE selecting the tomograph.
        //    SitraMed uses Phoenix LiveView with phx-change="machine_calendar" on the form and
        //    phx-debounce="blur" on the date input. LiveView maintains server-side form state:
        //    the server only updates its stored date when the input fires a blur event.
        //    Without blur, selecting the tomograph sends a phx-change with the server's cached
        //    date (today), not the DOM value — so all dates return today's patients.
        var dateStr = date.ToString("dd/MM/yyyy");  // for JS Date parsing
        var isoStr  = date.ToString("yyyy-MM-dd");  // ISO format SitraMed stores server-side

        var flatpickrSet = await page.EvaluateAsync<bool>("""
            (date) => {
                // date is "dd/MM/yyyy" — parse manually to avoid flatpickr dateFormat mismatch.
                const [dd, mm, yyyy] = date.split('/').map(Number);
                const di = document.querySelector('#search_date') ?? document.querySelector('input[name="search[date]"]');
                if (!di?._flatpickr) return false;
                // triggerChange=false: set internal state silently to avoid resetting the tomograph dropdown.
                // The blur dispatch below will inform LiveView of the new date.
                di._flatpickr.setDate(new Date(yyyy, mm - 1, dd), false);
                return true;
            }
            """, dateStr);

        if (!flatpickrSet)
        {
            // No flatpickr: fill with ISO format — SitraMed's server expects "yyyy-MM-dd"
            // (captured HTML shows value="2026-04-30").
            await FillFirstAsync(page, new[] { "#search_date", "input[name='search[date]']" }, isoStr);
        }

        // CRITICAL: fire blur so LiveView (phx-debounce="blur") syncs the new date to the server.
        // Without this the server retains today's date regardless of what the DOM shows.
        await page.EvaluateAsync("""
            () => {
                const di = document.querySelector('#search_date')
                        ?? document.querySelector('input[name="search[date]"]');
                di?.focus();
                di?.blur();
            }
            """);

        await page.WaitForTimeoutAsync(400);
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 8000 });
        }
        catch (TimeoutException) { }

        // 3. Now select the tomograph — phx-change fires with the date already stored server-side.
        await SelectFirstByLabelAsync(page, new[]
        {
            "#search_tomograph_id",
            "select[name='search[tomograph_id]']"
        }, tomograph.SitraName);

        // 4. Wait for the AJAX/NetworkIdle to finish loading results.
        await page.WaitForTimeoutAsync(400);
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = _options.TimeoutSeconds * 1000 });
        }
        catch (TimeoutException) { }

        try
        {
            await page.WaitForSelectorAsync("table tbody tr",
                new PageWaitForSelectorOptions { Timeout = 15000 });
        }
        catch (TimeoutException) { }

        cancellationToken.ThrowIfCancellationRequested();
        return await page.ContentAsync();
    }

    private async Task<List<MachineAppointmentSnapshot>> TryExtractTomographAgendaDomAsync(
        IPage page,
        RtTomograph tomograph,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        foreach (var selector in new[]
                 {
                     "#machineDrag tbody tr",
                     "#machine_drag tbody tr",
                     "#tomographDrag tbody tr",
                     "#tomograph_drag tbody tr",
                     "table.table tbody tr",
                     "table tbody tr"
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locator = page.Locator(selector);
            int count;
            try { count = await locator.CountAsync(); } catch { continue; }
            if (count == 0) continue;

            var parsed = await ParseTomographAgendaRowsAsync(locator, count, tomograph, date);
            if (parsed.Count > 0) return parsed;
        }

        return [];
    }

    private static async Task<List<MachineAppointmentSnapshot>> ParseTomographAgendaRowsAsync(
        ILocator rowLocator,
        int rowCount,
        RtTomograph tomograph,
        DateOnly date)
    {
        var list = new List<MachineAppointmentSnapshot>();
        for (var i = 0; i < rowCount; i++)
        {
            var row = rowLocator.Nth(i);
            var cells = await row.Locator("td").AllInnerTextsAsync();
            if (cells.Count < 2) continue;

            var trimmed = cells
                .Select(static c => string.Join(' ', c.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim())
                .ToArray();
            if (trimmed.All(string.IsNullOrWhiteSpace)) continue;

            // Skip header rows
            var joined = string.Join(' ', trimmed).ToLowerInvariant();
            if (joined.Contains("paciente") && (joined.Contains("inicio") || joined.Contains("hora"))) continue;

            // Skip empty leading cells (signs column)
            var offset = 0;
            while (offset < trimmed.Length - 1 && string.IsNullOrWhiteSpace(trimmed[offset])) offset++;

            var startTime = offset < trimmed.Length ? trimmed[offset] : string.Empty;
            var patient   = offset + 1 < trimmed.Length ? trimmed[offset + 1] : string.Empty;

            // Scan ALL cells (from offset+2) for a valid treatment keyword.
            // Avoids assuming a fixed column position regardless of how many columns the table has.
            var tipoTurno = string.Empty;
            for (var j = offset + 2; j < trimmed.Length; j++)
            {
                if (IsValidTomographTreatment(trimmed[j]))
                {
                    var stripped = StripSitraMedAlerts(trimmed[j]);
                    tipoTurno = TreatmentClassifier.Classify(stripped);
                    if (string.IsNullOrEmpty(tipoTurno)) tipoTurno = stripped;
                    break;
                }
            }
            if (string.IsNullOrEmpty(tipoTurno)) continue;
            if (string.IsNullOrWhiteSpace(patient) || patient == "-") continue;

            var tomoGuid = string.Empty;
            try
            {
                var link = row.Locator("a[href*='overview']").First;
                if (await link.CountAsync() > 0)
                {
                    var href = await link.GetAttributeAsync("href") ?? string.Empty;
                    var gm = MedicalHistoryIdRegex.Match(href);
                    if (gm.Success) tomoGuid = gm.Groups[1].Value;
                }
            }
            catch { }

            list.Add(new MachineAppointmentSnapshot
            {
                CenterName  = tomograph.CenterName,
                MachineName = tomograph.DisplayName,
                PatientName = patient,
                AgendaDate  = date,
                StartTime   = startTime,
                EndTime     = string.Empty,
                Treatment   = tipoTurno,
                SitraMedGuid = string.IsNullOrEmpty(tomoGuid) ? null : tomoGuid
            });
        }

        return list;
    }

    private static readonly string[] TomographTreatmentKeywords = ["3D", "IMRT", "SBRT", "RxCx", "Modulada", "Tridimensional", "Braquiterapia", "Radiocirug", "Intraoperatoria", "IORT", "IGRT", "TBI"];

    private static bool IsValidTomographTreatment(string tipoTurno)
    {
        if (string.IsNullOrWhiteSpace(tipoTurno) || tipoTurno.Trim() == "-") return false;
        if (tipoTurno.Contains("actividad", StringComparison.OrdinalIgnoreCase)) return false;
        return TomographTreatmentKeywords.Any(k => tipoTurno.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    // SitraMed appends alert text (e.g. "NO REGISTRA CONSENTIMIENTO MÉDICO FIRMADO") to the
    // Tipo de turno field. Strip everything from " NO REGISTRA" onwards.
    private static string StripSitraMedAlerts(string tipoTurno)
    {
        var idx = tipoTurno.IndexOf(" NO REGISTRA", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? tipoTurno[..idx].Trim() : tipoTurno.Trim();
    }

    private static bool LooksLikePersonName(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length < 3) return false;
        if (Regex.IsMatch(s.Trim(), @"^\d{1,2}:\d{2}")) return false;
        if (Regex.IsMatch(s.Trim(), @"^\d{2}[-/]\d{2}[-/]\d{4}")) return false;
        if (Regex.IsMatch(s.Trim(), @"^\d{4}-\d{2}-\d{2}")) return false;
        if (Regex.IsMatch(s.Trim(), @"^\d+$")) return false;
        return s.Any(char.IsLetter);
    }

    private string? SaveAgendaHtmlCapture(RtMachine machine, DateOnly date, string html)
    {
        if (!_options.SaveAgendaHtmlCapture || string.IsNullOrWhiteSpace(_options.AgendaHtmlCaptureDirectory))
        {
            return null;
        }

        Directory.CreateDirectory(_options.AgendaHtmlCaptureDirectory);
        var safeMachine = string.Concat(machine.DisplayName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var fileName = $"agenda_{date:yyyyMMdd}_{safeMachine}_{DateTime.UtcNow:HHmmss}.html";
        var fullPath = Path.Combine(_options.AgendaHtmlCaptureDirectory, fileName);
        File.WriteAllText(fullPath, html);
        return fullPath;
    }

    private string EnsureDiagnosticsDirectory(string area, string scopeA, string scopeB)
    {
        var root = _options.DiagnosticsDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, "data", "diagnostics");
        }

        Directory.CreateDirectory(root);
        var safeA = string.Concat(scopeA.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var safeB = string.Concat(scopeB.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var dir = Path.Combine(root, area, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{safeA}_{safeB}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<string?> GetSelectedOptionValueAsync(IPage page, IEnumerable<string> selectors)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                var selected = await locator.First.EvaluateAsync<string?>(
                    "el => el.options && el.selectedIndex >= 0 ? el.options[el.selectedIndex].value : null");
                return selected;
            }
            catch
            {
            }
        }

        return null;
    }

    private static async Task FillFirstAsync(IPage page, IEnumerable<string> selectors, string value)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() > 0)
            {
                await locator.First.FillAsync(string.Empty);
                await locator.First.FillAsync(value);
                return;
            }
        }
    }

    private static async Task SelectFirstAsync(IPage page, IEnumerable<string> selectors, string value)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() > 0)
            {
                await locator.First.SelectOptionAsync(new SelectOptionValue { Value = value });
                return;
            }
        }
    }

    private static async Task<bool> SelectFirstSafeAsync(IPage page, IEnumerable<string> selectors, string value, bool byValue)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                var opt = byValue
                    ? new SelectOptionValue { Value = value }
                    : new SelectOptionValue { Label = value };
                await locator.First.SelectOptionAsync(opt);
                return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private static async Task SelectFirstByLabelAsync(IPage page, IEnumerable<string> selectors, string label)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() > 0)
            {
                try
                {
                    await locator.First.SelectOptionAsync(new SelectOptionValue { Label = label });
                    return;
                }
                catch
                {
                    var options = await locator.First.Locator("option").AllInnerTextsAsync();
                    var match = options.FirstOrDefault(x => Normalize(x).Contains(Normalize(label)));
                    if (!string.IsNullOrWhiteSpace(match))
                    {
                        await locator.First.SelectOptionAsync(new SelectOptionValue { Label = match });
                        return;
                    }
                }
            }
        }
    }

    private static async Task ClickFirstAsync(IPage page, IEnumerable<string> selectors)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() > 0)
            {
                await locator.First.ClickAsync();
                return;
            }
        }
    }

    private static async Task<string> TriggerFollowUpSearchAsync(IPage page)
    {
        // 1) Esperar a que al menos un botón de búsqueda esté presente en el DOM.
        var candidateSelectors = new[]
        {
            "button:has-text('Buscar seguimientos')",
            "button:has-text('Buscar Seguimiento')",
            "button.button.is-primary[type='submit']",
            "button.button.is-info[type='submit']",
            "button.button.is-link[type='submit']",
            "button.button.is-secondary[type='submit']",
            "button.button[type='submit']",
            "button[type='submit']",
            "input[type='submit']",
            "button:has-text('Buscar')",
            "#patient_search_button",
            "a:has-text('Buscar seguimientos')",
            "a:has-text('Buscar')"
        };

        foreach (var sel in candidateSelectors)
        {
            try
            {
                await page.Locator(sel).First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 2000
                });
                break;
            }
            catch { }
        }

        // 2) Intentar click real; si falla por interactividad, dispatch event.
        var clicked = await ClickFirstSafeAsync(page, candidateSelectors);
        if (clicked.Success)
        {
            return clicked.Selector;
        }

        // 3) Fallback final: submit del formulario contenedor.
        await page.EvaluateAsync(
            """
            () => {
              const select = document.querySelector("#filters_micro_status") || document.querySelector("select[name='filters[micro_status]']");
              const form = select?.closest("form") ?? document.querySelector("form");
              if (form) {
                if (typeof form.requestSubmit === "function") {
                  form.requestSubmit();
                } else {
                  form.submit();
                }
              }
            }
            """);
        return "js_form_submit";
    }

    private static async Task<(bool Success, string Selector)> ClickFirstSafeAsync(IPage page, IEnumerable<string> selectors)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await locator.First.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                return (true, selector);
            }
            catch
            {
                try
                {
                    await locator.First.DispatchEventAsync("click");
                    return (true, selector);
                }
                catch
                {
                }
            }
        }

        return (false, string.Empty);
    }

    private async Task WaitForFollowUpSearchEffectAsync(IPage page, string htmlBeforeSearch, CancellationToken cancellationToken)
    {
        var maxMs = _options.TimeoutSeconds * 1000;
        var started = Environment.TickCount64;

        while (Environment.TickCount64 - started < maxMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Esperar a que el DOM esté disponible antes de leerlo.
            try
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions { Timeout = 5000 });
            }
            catch { }

            // Tabla de resultados — varios IDs/clases posibles en distintos tenants de SitraMed.
            var rowsCount = await page.Locator(
                "#follow-up-tables tbody tr, " +
                "table.follow-up-search tbody tr, " +
                "#follow_up_tables tbody tr, " +
                "table.table tbody tr").CountAsync();
            if (rowsCount > 0)
            {
                return;
            }

            // Mensaje de cero resultados — distintas redacciones.
            var noResultsCount = await page.Locator(
                "text=No se encontraron, " +
                "text=sin resultados, " +
                "text=no hay pacientes, " +
                "text=No hay").CountAsync();
            if (noResultsCount > 0)
            {
                return;
            }

            // Si el HTML cambió respecto al baseline post-filtros, algo sucedió.
            // ContentAsync puede fallar si la página está navegando; en ese caso
            // reintentamos en el próximo tick.
            var currentHtml = await SafeGetContentAsync(page);
            if (currentHtml != null && !string.Equals(currentHtml, htmlBeforeSearch, StringComparison.Ordinal))
            {
                // Esperar NetworkIdle para que el contenido se estabilice.
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                        new PageWaitForLoadStateOptions { Timeout = 8000 });
                }
                catch { }
                return;
            }

            await page.WaitForTimeoutAsync(250);
        }

        // Timeout expirado sin detectar cambio en el HTML. Continúa con el HTML actual
        // en lugar de lanzar — el extractor downstream manejará HTML vacío o parcial.
        Console.Error.WriteLine("[SitraMed] WaitForFollowUpSearchEffect: timeout, continuando con HTML actual.");
    }

    private static async Task<string?> SafeGetContentAsync(IPage page)
    {
        try
        {
            return await page.ContentAsync();
        }
        catch
        {
            return null;
        }
    }

    private async Task WaitForAnyAsync(IPage page, IEnumerable<string> selectors)
    {
        var timeout = _options.TimeoutSeconds * 1000f;

        foreach (var selector in selectors)
        {
            try
            {
                await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
                {
                    Timeout = timeout,
                    State = WaitForSelectorState.Attached
                });
                return;
            }
            catch
            {
            }
        }

        throw new TimeoutException("No aparecio ninguno de los selectores esperados en la pagina.");
    }

    private static readonly Regex MedicalHistoryIdRegex = new(
        @"medical_histories/([^/]+)/overview",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private async Task<List<FollowUpPatientDomRow>> TryExtractFollowUpDomAsync(
        IPage page,
        RtCenter center,
        ProcessStageDefinition stage,
        CancellationToken cancellationToken)
    {
        foreach (var selector in new[]
                 {
                     "#follow-up-tables > tbody > tr",
                     "#follow_up_tables > tbody > tr",
                     "table[id*='follow'] > tbody > tr"
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locator = page.Locator(selector);
            int count;
            try { count = await locator.CountAsync(); }
            catch { continue; }

            if (count == 0) continue;

            var rows = await ParseFollowUpRowsAsync(locator, count, center, stage);
            if (rows.Count > 0) return rows;
        }

        return [];
    }

    private static async Task<List<FollowUpPatientDomRow>> ParseFollowUpRowsAsync(
        ILocator rowLocator,
        int rowCount,
        RtCenter center,
        ProcessStageDefinition stage)
    {
        var list = new List<FollowUpPatientDomRow>();
        for (var i = 0; i < rowCount; i++)
        {
            var row = rowLocator.Nth(i);
            var cells = row.Locator(":scope > td");
            int cellCount;
            try { cellCount = await cells.CountAsync(); }
            catch { continue; }

            if (cellCount < 3) continue;

            var priority = (await cells.Nth(0).InnerTextAsync()).Trim();
            if (!int.TryParse(priority, out _)) continue;

            string? rowHtml = null;
            try { rowHtml = await row.EvaluateAsync<string>("tr => tr.outerHTML").ConfigureAwait(false); } catch { }

            var stageEntryDate = rowHtml != null
                ? FollowUpDateParser.ExtractStageEntryDate(rowHtml, stage.Code)
                : null;

            var tomographyDate = rowHtml != null
                ? FollowUpDateParser.ExtractTomographyDate(rowHtml)
                : null;

            var responsibleDoctor = rowHtml != null
                ? FollowUpDateParser.ExtractResponsibleDoctor(rowHtml)
                : null;

            var postponedUntil = rowHtml != null
                ? FollowUpDateParser.ExtractPostponedUntil(rowHtml)
                : null;

            var firstConsultDate = stageEntryDate.HasValue
                ? stageEntryDate.Value.ToString("dd-MM-yyyy")
                : (await cells.Nth(1).InnerTextAsync()).Trim();

            var patientName = (await cells.Nth(2).InnerTextAsync()).Trim();
            var institution = cellCount > 3 ? (await cells.Nth(3).InnerTextAsync()).Trim() : string.Empty;
            var doctorHc = cellCount > 4 ? (await cells.Nth(4).InnerTextAsync()).Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(patientName)) continue;

            // Busca el Nro. HC en cualquier celda directa del <tr> cuyo texto tenga formato HC.
            // Formato: 1-3 dígitos – 4-7 dígitos – 1-3 dígitos (ej: 1-117505-0).
            // Usa textContent (no innerText) para no depender de CSS visibility.
            // Si no lo encuentra (paciente F3 sin HC asignado aún), cae al GUID del href.
            var sitraMedId = string.Empty;
            try
            {
                sitraMedId = await row.EvaluateAsync<string>(
                    """
                    tr => {
                        const cells = Array.from(tr.querySelectorAll(':scope > td'));
                        for (const td of cells) {
                            const t = (td.textContent || '').trim();
                            if (/^\d{1,3}-\d{4,7}-\d{1,3}$/.test(t)) return t;
                        }
                        return '';
                    }
                    """).ConfigureAwait(false) ?? string.Empty;
            }
            catch { }

            var sitraMedGuid = string.Empty;
            try
            {
                var link = cells.Nth(2).Locator("a[href*='overview']").First;
                if (await link.CountAsync() > 0)
                {
                    var href = await link.GetAttributeAsync("href") ?? string.Empty;
                    var m = MedicalHistoryIdRegex.Match(href);
                    if (m.Success)
                    {
                        sitraMedGuid = m.Groups[1].Value;
                        if (string.IsNullOrWhiteSpace(sitraMedId))
                            sitraMedId = sitraMedGuid;
                    }
                }
            }
            catch { }

            var assignedPhysicist = string.Empty;
            try
            {
                assignedPhysicist = await row.EvaluateAsync<string>(
                    """
                    tr => {
                        const sel = tr.querySelector('select[id*="physicist"]');
                        if (!sel) return '';
                        const idx = sel.selectedIndex;
                        return idx >= 0 ? (sel.options[idx].text || '').trim() : '';
                    }
                    """).ConfigureAwait(false) ?? string.Empty;
            }
            catch { }

            var treatmentZone = string.Empty;
            try
            {
                treatmentZone = await row.EvaluateAsync<string>(
                    """
                    tr => {
                        const conductLink = tr.querySelector('td a[href*="conduct_definitions"]');
                        if (conductLink) {
                            const text = (conductLink.textContent || '').trim();
                            if (text.length > 2) return text;
                        }
                        const keywords = ['tridimensional', '3d', 'modulada', 'imrt', 'sbrt',
                                          'igrt', 'tbi', 'irradiaci', 'radiocirug', 'vmat', 'arco',
                                          'braquiterapia', 'intraoperatoria', 'iort', 'rxcx'];
                        const secondary = ['braquiterapia', 'intraoperatoria', 'iort'];
                        const cells = Array.from(tr.querySelectorAll(':scope > td'));
                        let fallback = '';
                        for (let i = 2; i < cells.length; i++) {
                            if (cells[i].querySelector('.modal, button.modal-button')) continue;
                            const raw = (cells[i].textContent || '').trim();
                            const tl = raw.replace(/[\s ]+/g, ' ').toLowerCase();
                            if (raw.length > 2 && keywords.some(kw => tl.includes(kw))) {
                                if (secondary.some(kw => tl.includes(kw))) {
                                    if (!fallback) fallback = raw;
                                } else {
                                    return raw;
                                }
                            }
                        }
                        return fallback;
                    }
                    """).ConfigureAwait(false) ?? string.Empty;
            }
            catch { }

            list.Add(new FollowUpPatientDomRow
            {
                PatientName = patientName,
                SitraMedId = sitraMedId,
                SitraMedGuid = sitraMedGuid,
                AssignedPhysicist = assignedPhysicist,
                TreatmentZone = treatmentZone,
                FirstConsultDate = firstConsultDate,
                Institution = institution,
                DoctorHc = doctorHc,
                CenterId = center.Id,
                CenterName = center.Name,
                StageCode = stage.Code,
                TomographyDate = tomographyDate,
                ResponsibleDoctor = responsibleDoctor,
                PostponedUntil = postponedUntil,
                Priority = int.TryParse(priority, out var pInt) ? pInt : (int?)null
            });
        }

        return list;
    }

    private static string Normalize(string value)
    {
        return new string(value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .ToArray());
    }
}

public sealed class FollowUpHtmlSnapshot
{
    public string CenterId { get; set; } = string.Empty;
    public string CenterName { get; set; } = string.Empty;
    public string StageCode { get; set; } = string.Empty;
    public string StageMicroStatus { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
    public List<FollowUpPatientDomRow>? DomRows { get; set; }
}

public sealed class FollowUpPatientDomRow
{
    public string PatientName { get; set; } = string.Empty;
    public string SitraMedId { get; set; } = string.Empty;
    public string SitraMedGuid { get; set; } = string.Empty;
    public string AssignedPhysicist { get; set; } = string.Empty;
    public string TreatmentZone { get; set; } = string.Empty;
    public string FirstConsultDate { get; set; } = string.Empty;
    public string Institution { get; set; } = string.Empty;
    public string DoctorHc { get; set; } = string.Empty;
    public string CenterId { get; set; } = string.Empty;
    public string CenterName { get; set; } = string.Empty;
    public string StageCode { get; set; } = string.Empty;
    public DateOnly? TomographyDate { get; set; }
    public string? ResponsibleDoctor { get; set; }
    public DateOnly? PostponedUntil { get; set; }
    public int? Priority { get; set; }
}

public sealed class ScrapingTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string PageTitle { get; set; } = string.Empty;
    public int HtmlLength { get; set; }
    public string HtmlPreview { get; set; } = string.Empty;
    public int AgendaDomRows { get; set; }
    public string? CapturedFilePath { get; set; }
    public string? FollowUpSearchAction { get; set; }
    public string? SelectedCenterValue { get; set; }
    public string? SelectedMicroStatusValue { get; set; }
    // Raw cell values for each TR row (without any keyword filter), up to 20 rows
    public List<string> RawRowSamples { get; set; } = [];
}

public sealed class AgendaHtmlSnapshot
{
    public string CenterName { get; set; } = string.Empty;
    public string MachineDisplayName { get; set; } = string.Empty;
    public DateOnly AgendaDate { get; set; }
    public string Html { get; set; } = string.Empty;
    public bool HasScrapingError { get; set; }

    /// <summary>Filas parseadas desde el DOM con Playwright; si hay datos se prefieren al regex sobre HTML.</summary>
    public List<MachineAppointmentSnapshot>? DomSnapshots { get; set; }
}

public sealed class TomographAgendaHtmlSnapshot
{
    public string CenterName            { get; set; } = string.Empty;
    public string TomographDisplayName  { get; set; } = string.Empty;
    public DateOnly AgendaDate          { get; set; }
    public string Html                  { get; set; } = string.Empty;
    public List<MachineAppointmentSnapshot>? DomSnapshots { get; set; }
}

internal sealed class PlaywrightSession : IAsyncDisposable
{
    public PlaywrightSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page)
    {
        Playwright = playwright;
        Browser = browser;
        Context = context;
        Page = page;
    }

    public IPlaywright Playwright { get; }
    public IBrowser Browser { get; }
    public IBrowserContext Context { get; }
    public IPage Page { get; }

    public async ValueTask DisposeAsync()
    {
        await Context.CloseAsync();
        await Browser.CloseAsync();
        Playwright.Dispose();
    }
}

internal sealed class FollowUpDownloadResult
{
    public string Html { get; set; } = string.Empty;
    public string SearchAction { get; set; } = string.Empty;
    public string? CenterValueSelected { get; set; }
    public string? MicroStatusValueSelected { get; set; }
    public string? DiagnosticDirectory { get; set; }
    public List<FollowUpPatientDomRow> DomRows { get; set; } = [];
}
