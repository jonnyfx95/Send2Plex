using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Recupera pagine HTML renderizzate da un vero motore browser (WebView2/Chromium),
/// per siti protetti da Cloudflare o che richiedono una sessione di login che un
/// semplice HttpClient non riesce a superare. Gira su un thread STA dedicato con il
/// proprio message loop, con un profilo persistente su disco cosicché il login fatto
/// una volta (tramite <see cref="OpenLoginWindowAsync"/>) resti valido tra i riavvii.
/// </summary>
public class BrowserFetcher : IDisposable
{
    private readonly ILogger<BrowserFetcher> _log;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread? _thread;
    private Form? _hostForm;
    private WebView2? _fetchView;
    private CoreWebView2Environment? _environment;
    private bool _disposed;

    public BrowserFetcher(ILogger<BrowserFetcher> log)
    {
        _log = log;
    }

    private Task EnsureStartedAsync()
    {
        if (_thread is null)
        {
            _thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "BrowserFetcherThread" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        return _readyTcs.Task;
    }

    private void RunMessageLoop()
    {
        try
        {
            _hostForm = new Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-3000, -3000),
                Size = new Size(1200, 900)
            };

            _fetchView = new WebView2 { Dock = DockStyle.Fill };
            _hostForm.Controls.Add(_fetchView);

            _hostForm.Load += async (_, _) =>
            {
                try
                {
                    var profileDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2Profile");
                    _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDir);
                    await _fetchView.EnsureCoreWebView2Async(_environment);
                    _readyTcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "❌ Errore inizializzando WebView2 (runtime non installato?)");
                    _readyTcs.TrySetException(ex);
                }
            };

            Application.Run(_hostForm);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore avviando il browser integrato (WebView2)");
            _readyTcs.TrySetException(ex);
        }
    }

    /// <summary>
    /// Naviga alla pagina, attende che un'eventuale sfida Cloudflare/JS si risolva
    /// e restituisce l'HTML renderizzato del DOM.
    /// </summary>
    public async Task<string> GetRenderedHtmlAsync(string url, int timeoutSeconds, CancellationToken ct)
    {
        await EnsureStartedAsync();
        await _fetchLock.WaitAsync(ct);

        try
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            _hostForm!.BeginInvoke(new Action(async () =>
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

                    SearchTrace.Write($"[BrowserFetcher] GetRenderedHtml: navigo a {url} (timeout {timeoutSeconds}s)");
                    await NavigateAndWaitAsync(_fetchView!, url, cts.Token);
                    var html = await WaitForChallengeToResolveAsync(_fetchView!, cts.Token);
                    SearchTrace.Write($"[BrowserFetcher] GetRenderedHtml: OK, {html.Length} caratteri ricevuti da {url}");
                    tcs.TrySetResult(html);
                }
                catch (Exception ex)
                {
                    SearchTrace.Write($"[BrowserFetcher] GetRenderedHtml: ERRORE su {url} — {ex.GetType().Name}: {ex.Message}");
                    tcs.TrySetException(ex);
                }
            }));

            return await tcs.Task;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// Compila e invia (submit) un form di ricerca vero e proprio nel browser integrato — per siti
    /// (come i forum SMF) la cui ricerca è un POST e non un semplice link con parametri in URL.
    /// </summary>
    public async Task<string> SubmitSearchFormAsync(string formPageUrl, string fieldName, string query, int timeoutSeconds, CancellationToken ct)
    {
        await EnsureStartedAsync();
        await _fetchLock.WaitAsync(ct);

        try
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            _hostForm!.BeginInvoke(new Action(async () =>
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

                    SearchTrace.Write($"[BrowserFetcher] SubmitSearchForm: navigo a {formPageUrl}, campo=\"{fieldName}\" query=\"{query}\"");
                    await NavigateAndWaitAsync(_fetchView!, formPageUrl, cts.Token);

                    var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnSubmitNavCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => navTcs.TrySetResult(e.IsSuccess);

                    _fetchView!.NavigationCompleted += OnSubmitNavCompleted;
                    try
                    {
                        using var reg = cts.Token.Register(() => navTcs.TrySetCanceled());

                        var fieldNameJson = JsonSerializer.Serialize(fieldName);
                        var queryJson = JsonSerializer.Serialize(query);
                        var script = $$"""
                            (function() {
                                var el = document.querySelector('[name={{fieldNameJson}}]');
                                if (!el) return 'NOFIELD';
                                el.value = {{queryJson}};
                                if (!el.form) return 'NOFORM';
                                var btn = el.form.querySelector('input[type=submit], button[type=submit]');
                                if (btn) { btn.click(); return 'OK'; }
                                el.form.submit();
                                return 'OK';
                            })()
                            """;
                        var resultJson = await _fetchView.ExecuteScriptAsync(script);
                        var result = JsonSerializer.Deserialize<string>(resultJson) ?? "";
                        SearchTrace.Write($"[BrowserFetcher] SubmitSearchForm: script esito={result}");
                        if (result != "OK")
                            throw new InvalidOperationException($"Impossibile inviare il form di ricerca (campo '{fieldName}': {result}).");

                        await navTcs.Task;
                    }
                    finally
                    {
                        _fetchView.NavigationCompleted -= OnSubmitNavCompleted;
                    }

                    var html = await WaitForChallengeToResolveAsync(_fetchView!, cts.Token);
                    SearchTrace.Write($"[BrowserFetcher] SubmitSearchForm: OK, {html.Length} caratteri ricevuti");
                    tcs.TrySetResult(html);
                }
                catch (Exception ex)
                {
                    SearchTrace.Write($"[BrowserFetcher] SubmitSearchForm: ERRORE — {ex.GetType().Name}: {ex.Message}");
                    tcs.TrySetException(ex);
                }
            }));

            return await tcs.Task;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// Naviga alla pagina, clicca un elemento (es. un pulsante "Ringrazia" che sblocca un link
    /// nascosto) e restituisce l'HTML risultante — gestendo sia il caso in cui il click ricarichi
    /// la pagina sia il caso in cui aggiorni il DOM via AJAX senza navigazione.
    /// </summary>
    public async Task<string> GetRenderedHtmlAfterClickAsync(string url, string clickSelector, int timeoutSeconds, CancellationToken ct)
    {
        await EnsureStartedAsync();
        await _fetchLock.WaitAsync(ct);

        try
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            _hostForm!.BeginInvoke(new Action(async () =>
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

                    SearchTrace.Write($"[BrowserFetcher] GetRenderedHtmlAfterClick: navigo a {url}, selettore click=\"{clickSelector}\" (timeout {timeoutSeconds}s)");
                    await NavigateAndWaitAsync(_fetchView!, url, cts.Token);
                    await WaitForChallengeToResolveAsync(_fetchView!, cts.Token);

                    var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => navTcs.TrySetResult(true);
                    _fetchView!.NavigationCompleted += OnNav;

                    try
                    {
                        var selectorJson = JsonSerializer.Serialize(clickSelector);
                        var clickScript = $$"""
                            (function() {
                                var el = document.querySelector({{selectorJson}});
                                if (!el) return 'NOTFOUND';
                                el.click();
                                return 'OK';
                            })()
                            """;
                        var clickResultJson = await _fetchView.ExecuteScriptAsync(clickScript);
                        var clickResult = JsonSerializer.Deserialize<string>(clickResultJson) ?? "";
                        SearchTrace.Write($"[BrowserFetcher] GetRenderedHtmlAfterClick: click esito={clickResult}");

                        if (clickResult == "OK")
                        {
                            // Aspetta o una navigazione (reload) o un breve intervallo per un eventuale update AJAX
                            var completed = await Task.WhenAny(navTcs.Task, Task.Delay(2500, cts.Token));
                            SearchTrace.Write(completed == navTcs.Task
                                ? "[BrowserFetcher] GetRenderedHtmlAfterClick: navigazione rilevata dopo il click"
                                : "[BrowserFetcher] GetRenderedHtmlAfterClick: nessuna navigazione, presumo update via AJAX");
                            if (completed == navTcs.Task)
                                await Task.Delay(500, cts.Token);
                        }
                    }
                    finally
                    {
                        _fetchView.NavigationCompleted -= OnNav;
                    }

                    var html = await WaitForChallengeToResolveAsync(_fetchView, cts.Token);
                    SearchTrace.Write($"[BrowserFetcher] GetRenderedHtmlAfterClick: OK, {html.Length} caratteri ricevuti");
                    tcs.TrySetResult(html);
                }
                catch (Exception ex)
                {
                    SearchTrace.Write($"[BrowserFetcher] GetRenderedHtmlAfterClick: ERRORE su {url} — {ex.GetType().Name}: {ex.Message}");
                    tcs.TrySetException(ex);
                }
            }));

            return await tcs.Task;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private static async Task NavigateAndWaitAsync(WebView2 view, string url, CancellationToken ct)
    {
        var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => navTcs.TrySetResult(e.IsSuccess);

        view.NavigationCompleted += OnNavCompleted;
        try
        {
            using var reg = ct.Register(() => navTcs.TrySetCanceled());
            view.CoreWebView2.Navigate(url);
            await navTcs.Task;
        }
        finally
        {
            view.NavigationCompleted -= OnNavCompleted;
        }
    }

    private static async Task<string> WaitForChallengeToResolveAsync(WebView2 view, CancellationToken ct)
    {
        var html = "";

        for (var attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var htmlJson = await view.ExecuteScriptAsync("document.documentElement.outerHTML");
            html = JsonSerializer.Deserialize<string>(htmlJson) ?? "";

            var titleJson = await view.ExecuteScriptAsync("document.title");
            var title = JsonSerializer.Deserialize<string>(titleJson) ?? "";

            var looksLikeChallenge =
                title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Attendere", StringComparison.OrdinalIgnoreCase);

            if (!looksLikeChallenge)
            {
                if (attempt > 0)
                    SearchTrace.Write($"[BrowserFetcher] sfida risolta dopo {attempt} tentativo/i (titolo: \"{title}\")");
                break;
            }

            SearchTrace.Write($"[BrowserFetcher] tentativo {attempt + 1}/8: sembra ancora una sfida Cloudflare/JS (titolo: \"{title}\"), attendo 1s");
            await Task.Delay(1000, ct);
        }

        try
        {
            var debugDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2Profile");
            Directory.CreateDirectory(debugDir);
            await File.WriteAllTextAsync(Path.Combine(debugDir, "last_fetch_debug.html"), html, ct);
        }
        catch
        {
            // solo diagnostica: un fallimento qui non deve interrompere il fetch
        }

        return html;
    }

    /// <summary>
    /// Apre una finestra browser visibile (stesso profilo/cookie condivisi con il fetch headless)
    /// dove l'utente può fare login manualmente una tantum.
    /// </summary>
    public async Task OpenLoginWindowAsync(string url)
    {
        await EnsureStartedAsync();

        _hostForm!.BeginInvoke(new Action(async () =>
        {
            try
            {
                var loginForm = new Form
                {
                    Text = "Login — chiudi questa finestra al termine",
                    Width = 1000,
                    Height = 800,
                    StartPosition = FormStartPosition.CenterScreen
                };

                var toolbar = new Panel { Dock = DockStyle.Top, Height = 34 };
                var lblUrl = new Label { Location = new Point(8, 9), AutoSize = true, Text = "Caricamento…" };
                var btnCapture = new Button { Text = "💾 Salva pagina corrente per Claude", Location = new Point(600, 4), Width = 280, Height = 26 };
                toolbar.Controls.Add(lblUrl);
                toolbar.Controls.Add(btnCapture);

                var loginView = new WebView2 { Dock = DockStyle.Fill };
                loginForm.Controls.Add(loginView);
                loginForm.Controls.Add(toolbar);
                loginForm.Show();

                await loginView.EnsureCoreWebView2Async(_environment);
                loginView.SourceChanged += (_, _) => lblUrl.Text = loginView.Source?.ToString() ?? "";
                loginView.CoreWebView2.Navigate(url);

                btnCapture.Click += async (_, _) =>
                {
                    try
                    {
                        var htmlJson = await loginView.ExecuteScriptAsync("document.documentElement.outerHTML");
                        var html = JsonSerializer.Deserialize<string>(htmlJson) ?? "";
                        var currentUrl = loginView.Source?.ToString() ?? "";

                        var captureDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2Profile", "html");
                        Directory.CreateDirectory(captureDir);

                        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                        var htmlPath = Path.Combine(captureDir, $"{stamp}.html");
                        var urlPath = Path.Combine(captureDir, $"{stamp}.url.txt");

                        await File.WriteAllTextAsync(htmlPath, html);
                        await File.WriteAllTextAsync(urlPath, currentUrl);

                        MessageBox.Show($"Salvato ({stamp}).\n\nURL: {currentUrl}\n\nFile: {htmlPath}",
                            "Pagina salvata", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Errore salvando la pagina: {ex.Message}", "Errore",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "❌ Errore aprendo la finestra di login");
                MessageBox.Show($"Errore aprendo la finestra di login: {ex.Message}", "Errore",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_hostForm is { IsDisposed: false })
                _hostForm.BeginInvoke(new Action(Application.ExitThread));
        }
        catch
        {
            // best effort: il thread potrebbe essere già terminato
        }

        _fetchLock.Dispose();
    }
}
