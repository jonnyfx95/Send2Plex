using AngleSharp;
using Microsoft.Extensions.Logging;
using SendToPlex.Bot.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SendToPlex.Bot.Services;

public class TorrentSearchService
{
    private readonly HttpClient _http;
    private readonly BrowserFetcher _browser;
    private readonly NordVpnController _vpn;
    private readonly ILogger<TorrentSearchService> _log;

    // Cache dei link estratti dalle pagine "indice" (liste alfabetiche): cambiano raramente,
    // evitiamo di riscaricarle a ogni ricerca (sono lente su siti protetti da Cloudflare).
    // Si mette in cache solo l'esito di un fetch riuscito (vedi GetIndexPageEntriesAsync):
    // un fetch fallito/vuoto non deve restare "congelato" in cache per ore.
    private readonly Dictionary<string, (DateTime FetchedAt, List<(string Title, string Url)> Entries)> _indexPageCache = new();
    private readonly SemaphoreSlim _indexCacheLock = new(1, 1);
    private static readonly TimeSpan IndexCacheTtl = TimeSpan.FromHours(6);

    public TorrentSearchService(HttpClient http, BrowserFetcher browser, NordVpnController vpn, ILogger<TorrentSearchService> log)
    {
        _http = http;
        _browser = browser;
        _vpn = vpn;
        _log = log;

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        }
    }

    /// <summary>
    /// Elenca tutte le pagine indice (liste alfabetiche) disponibili sui siti abilitati,
    /// per costruire un menù di categorie da sfogliare (es. comando /lists su Telegram).
    /// </summary>
    public static List<(TorrentSiteConfig Site, TorrentIndexPage Page)> GetAvailableIndexPages(TorrentSearchSettings settings)
    {
        return settings.Sites
            .Where(s => s.Enabled)
            .SelectMany(site => site.IndexPages.Select(page => (Site: site, Page: page)))
            .ToList();
    }

    /// <summary>
    /// Interroga in parallelo tutti i siti abilitati e restituisce i risultati aggregati,
    /// ordinati per numero di seeder decrescente. Un sito che fallisce non blocca gli altri.
    /// </summary>
    public async Task<List<TorrentSearchResult>> SearchAsync(string query, TorrentSearchSettings settings, CancellationToken ct)
    {
        var sites = settings.Sites.Where(s => s.Enabled).ToList();

        var tasks = sites.Select(site =>
            SearchSiteSafeAsync(site, query, settings.TimeoutSeconds, settings.MaxResultsPerSite, ct));

        var resultsPerSite = await Task.WhenAll(tasks);

        return resultsPerSite
            .SelectMany(r => r)
            .OrderByDescending(r => r.SeedsNumeric)
            .ToList();
    }

    private async Task<List<TorrentSearchResult>> SearchSiteSafeAsync(
        TorrentSiteConfig site, string query, int timeoutSeconds, int maxResults, CancellationToken ct)
    {
        try
        {
            return await SearchSiteAsync(site, query, timeoutSeconds, maxResults, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Ricerca torrent fallita sul sito {Site}", site.Name);
            return new List<TorrentSearchResult>();
        }
    }

    /// <summary>
    /// Interroga un singolo sito. Pubblico ed esposto separatamente così può essere usato
    /// anche dal pulsante "Testa selettori" nella UI, senza dover avviare l'intero bot.
    /// </summary>
    public async Task<List<TorrentSearchResult>> SearchSiteAsync(
        TorrentSiteConfig site, string query, int timeoutSeconds, int maxResults, CancellationToken ct)
    {
        if (site.RequiresVpn)
            await _vpn.ConnectAsync(ct);

        if (site.UseThePirateBayApi)
            return await SearchThePirateBayApiAsync(site, query, timeoutSeconds, maxResults, ct);

        if (site.IndexPages.Count > 0)
        {
            using var indexTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            indexTimeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds * site.IndexPages.Count)));
            return await SearchViaIndexPagesAsync(site, query, timeoutSeconds, maxResults, indexTimeoutCts.Token);
        }

        var results = new List<TorrentSearchResult>();

        if (string.IsNullOrWhiteSpace(site.SearchUrlTemplate) || string.IsNullOrWhiteSpace(site.ResultSelector))
            return results;

        // La paginazione è attiva solo se il template la dichiara esplicitamente con {page}: i siti
        // esistenti (senza {page} nel loro URL) continuano a fare una sola richiesta come prima.
        var supportsPaging = site.SearchUrlTemplate.Contains("{page}", StringComparison.Ordinal);
        var maxPages = supportsPaging ? Math.Max(1, site.MaxPages) : 1;
        var linkAttr = string.IsNullOrWhiteSpace(site.LinkAttribute) ? "href" : site.LinkAttribute;

        for (int page = 1; page <= maxPages && results.Count < maxResults; page++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

            var searchUrl = site.SearchUrlTemplate
                .Replace("{query}", Uri.EscapeDataString(query))
                .Replace("{page}", page.ToString());
            SearchTrace.Write($"[SearchSite] sito={site.Name} query=\"{query}\" pagina={page}/{maxPages} useBrowser={site.UseBrowser} formField={site.SearchFormFieldName ?? "-"} searchUrl={searchUrl}");

            var html = site.UseBrowser
                ? await FetchViaBrowserAsync(site, searchUrl, query, timeoutSeconds, timeoutCts.Token)
                : await GetHtmlAsync(searchUrl, site.Cookie, timeoutCts.Token);

            SearchTrace.Write($"[SearchSite] sito={site.Name} html ricevuto: {html.Length} caratteri");

            var browsingContext = BrowsingContext.New(Configuration.Default);
            using var document = await browsingContext.OpenAsync(req => req.Content(html).Address(searchUrl), timeoutCts.Token);

            var rows = document.QuerySelectorAll(site.ResultSelector);
            SearchTrace.Write($"[SearchSite] sito={site.Name} righe trovate con selettore \"{site.ResultSelector}\": {rows.Length}");

            var foundOnThisPage = 0;

            foreach (var row in rows)
            {
                if (results.Count >= maxResults) break;

                var title = string.IsNullOrWhiteSpace(site.TitleSelector)
                    ? row.TextContent.Trim()
                    : row.QuerySelector(site.TitleSelector)?.TextContent.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(title)) continue;

                var rawLink = row.QuerySelector(site.LinkSelector)?.GetAttribute(linkAttr);
                if (string.IsNullOrWhiteSpace(rawLink)) continue;

                var absoluteLink = ResolveUrl(searchUrl, rawLink);

                var sizeText = string.IsNullOrWhiteSpace(site.SizeSelector)
                    ? null
                    : row.QuerySelector(site.SizeSelector)?.TextContent.Trim();

                var seedsText = string.IsNullOrWhiteSpace(site.SeedsSelector)
                    ? null
                    : row.QuerySelector(site.SeedsSelector)?.TextContent.Trim();

                var result = new TorrentSearchResult
                {
                    SiteName = site.Name,
                    Title = title,
                    SizeText = sizeText,
                    SeedsText = seedsText,
                    SeedsNumeric = ParseSeeds(seedsText),
                    Cookie = site.Cookie,
                    UseBrowser = site.UseBrowser,
                    RevealClickSelector = site.RevealClickSelector
                };

                if (absoluteLink.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    result.Magnet = absoluteLink;
                }
                else if (site.NeedsDetailPageForMagnet)
                {
                    result.DetailUrl = absoluteLink;
                    result.MagnetSelectorOnDetailPage = site.MagnetSelectorOnDetailPage;
                }
                else
                {
                    // Non è un magnet e il sito non dichiara una pagina di dettaglio:
                    // lo teniamo come link di fallback (potrebbe essere un download diretto).
                    result.DetailUrl = absoluteLink;
                }

                results.Add(result);
                foundOnThisPage++;
            }

            SearchTrace.Write($"[SearchSite] sito={site.Name} pagina={page}/{maxPages} risultati estratti: {foundOnThisPage} (totale finora: {results.Count})");

            // Pagina oltre la prima senza nessun risultato nuovo: probabilmente non ci sono altre
            // pagine reali, inutile continuare a scaricarne altre.
            if (page > 1 && foundOnThisPage == 0) break;
        }

        // Risolvi subito il magnet dalla pagina di dettaglio SOLO per il percorso HTTP leggero
        // (veloce, nessun effetto collaterale). In modalità browser evitiamo di farlo per ogni
        // risultato della lista: è lento e, se il sito richiede un click con effetti collaterali
        // (es. pulsante "Ringrazia"), farlo su tutti i risultati di una ricerca rischia di
        // sembrare un comportamento da bot e far scattare le protezioni anti-abuso del sito.
        // Si risolve quindi solo quando l'utente sceglie un risultato specifico
        // (vedi TelegramWorker.HandleSearchSelectionAsync).
        if (!site.UseBrowser)
        {
            using var detailTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            detailTimeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

            foreach (var result in results.Where(r =>
                         r.Magnet is null && r.DetailUrl is not null && !string.IsNullOrWhiteSpace(r.MagnetSelectorOnDetailPage)))
            {
                try
                {
                    var magnets = await ResolveMagnetsFromDetailPageAsync(
                        result.DetailUrl!, result.MagnetSelectorOnDetailPage!, site.Cookie, site.UseBrowser, site.RevealClickSelector, timeoutSeconds, detailTimeoutCts.Token);
                    result.Magnet = magnets.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "⚠️ Impossibile risolvere il magnet dalla pagina di dettaglio {Url}", result.DetailUrl);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Ricerca "a scansione": invece di una query live, scorre un elenco fisso di pagine indice
    /// (es. liste alfabetiche) e filtra i titoli il cui testo contiene la query. Le pagine indice
    /// sono messe in cache (<see cref="IndexCacheTtl"/>) perché cambiano raramente e sono lente
    /// da recuperare su siti protetti da Cloudflare.
    /// </summary>
    private async Task<List<TorrentSearchResult>> SearchViaIndexPagesAsync(
        TorrentSiteConfig site, string query, int timeoutSeconds, int maxResults, CancellationToken ct)
    {
        var results = new List<TorrentSearchResult>();

        foreach (var indexPage in site.IndexPages)
        {
            if (results.Count >= maxResults) break;
            ct.ThrowIfCancellationRequested();

            var entries = await GetIndexPageEntriesAsync(site, indexPage, timeoutSeconds, ct);

            var matched = 0;
            foreach (var entry in entries)
            {
                if (results.Count >= maxResults) break;
                if (entry.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                matched++;
                results.Add(BuildIndexResult(site, indexPage, entry));
            }

            SearchTrace.Write($"[SearchIndex] sito={site.Name} pagina=\"{indexPage.Name}\" match per \"{query}\": {matched}");
        }

        return results;
    }

    /// <summary>
    /// Restituisce tutti i titoli di UNA specifica pagina indice (nessun filtro testuale),
    /// per quando l'utente vuole sfogliare per intero una categoria (es. "tutti i film in HEVC/H.265").
    /// </summary>
    public async Task<List<TorrentSearchResult>> BrowseIndexPageAsync(
        TorrentSiteConfig site, TorrentIndexPage indexPage, int timeoutSeconds, int maxResults, CancellationToken ct, string? query = null)
    {
        var entries = await GetIndexPageEntriesAsync(site, indexPage, timeoutSeconds, ct);

        if (!string.IsNullOrWhiteSpace(query))
            entries = entries.Where(e => e.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        return entries.Take(maxResults).Select(e => BuildIndexResult(site, indexPage, e)).ToList();
    }

    private static TorrentSearchResult BuildIndexResult(TorrentSiteConfig site, TorrentIndexPage indexPage, (string Title, string Url) entry) => new()
    {
        SiteName = site.Name,
        Title = entry.Title,
        DetailUrl = entry.Url,
        MagnetSelectorOnDetailPage = site.MagnetSelectorOnDetailPage,
        Cookie = site.Cookie,
        UseBrowser = site.UseBrowser,
        RevealClickSelector = site.RevealClickSelector,
        SourceListName = indexPage.Name
    };

    private async Task<List<(string Title, string Url)>> GetIndexPageEntriesAsync(
        TorrentSiteConfig site, TorrentIndexPage indexPage, int timeoutSeconds, CancellationToken ct)
    {
        await _indexCacheLock.WaitAsync(ct);
        try
        {
            if (_indexPageCache.TryGetValue(indexPage.Url, out var cached) && DateTime.UtcNow - cached.FetchedAt < IndexCacheTtl)
            {
                SearchTrace.Write($"[SearchIndex] pagina=\"{indexPage.Name}\" servita dalla cache ({cached.Entries.Count} voci, età: {(DateTime.UtcNow - cached.FetchedAt).TotalMinutes:n0} min)");
                return cached.Entries;
            }
        }
        finally
        {
            _indexCacheLock.Release();
        }

        var entries = new List<(string Title, string Url)>();
        var linkSelector = string.IsNullOrWhiteSpace(site.IndexLinkSelector) ? "a" : site.IndexLinkSelector;

        string html;
        try
        {
            html = site.UseBrowser
                ? await _browser.GetRenderedHtmlAsync(indexPage.Url, timeoutSeconds, ct)
                : await GetHtmlAsync(indexPage.Url, site.Cookie, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Impossibile recuperare la pagina indice {Url}", indexPage.Url);
            SearchTrace.Write($"[SearchIndex] sito={site.Name} pagina=\"{indexPage.Name}\" ERRORE: {ex.GetType().Name}: {ex.Message}");
            return entries;
        }

        SearchTrace.Write($"[SearchIndex] sito={site.Name} pagina=\"{indexPage.Name}\" html={html.Length} caratteri");

        var browsingContext = BrowsingContext.New(Configuration.Default);
        using var document = await browsingContext.OpenAsync(req => req.Content(html).Address(indexPage.Url), ct);

        var links = document.QuerySelectorAll(linkSelector);
        SearchTrace.Write($"[SearchIndex] sito={site.Name} pagina=\"{indexPage.Name}\" link totali: {links.Length}");

        foreach (var link in links)
        {
            var title = link.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            var href = link.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            entries.Add((title, ResolveUrl(indexPage.Url, href)));
        }

        // Mette in cache SOLO se ha trovato davvero qualcosa: un fetch fallito o vuoto (VPN giù,
        // sessione scaduta, sito temporaneamente irraggiungibile) non deve "avvelenare" la cache
        // per 6 ore — al prossimo giro si ritenta un fetch vero invece di ripetere il fallimento.
        if (entries.Count > 0)
        {
            await _indexCacheLock.WaitAsync(ct);
            try
            {
                _indexPageCache[indexPage.Url] = (DateTime.UtcNow, entries);
            }
            finally
            {
                _indexCacheLock.Release();
            }
        }

        return entries;
    }

    /// <summary>
    /// Recupera l'HTML della pagina di ricerca via browser integrato: se il sito ha un form
    /// di ricerca POST configurato (<see cref="TorrentSiteConfig.SearchFormFieldName"/>), lo
    /// compila e invia; altrimenti naviga direttamente all'URL (ricerca via GET).
    /// </summary>
    private Task<string> FetchViaBrowserAsync(TorrentSiteConfig site, string searchUrl, string query, int timeoutSeconds, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(site.SearchFormFieldName))
        {
            var formPageUrl = string.IsNullOrWhiteSpace(site.SearchFormPageUrl) ? searchUrl : site.SearchFormPageUrl;
            return _browser.SubmitSearchFormAsync(formPageUrl, site.SearchFormFieldName, query, timeoutSeconds, ct);
        }

        return _browser.GetRenderedHtmlAsync(searchUrl, timeoutSeconds, ct);
    }

    /// <summary>
    /// Restituisce TUTTI i magnet trovati sulla pagina di dettaglio (un topic può contenere più
    /// file/versioni: episodi separati, qualità diverse, ecc. — non solo il primo).
    /// </summary>
    public async Task<List<string>> ResolveMagnetsFromDetailPageAsync(
        string detailUrl, string magnetSelector, string? cookie, bool useBrowser, string? revealClickSelector, int timeoutSeconds, CancellationToken ct)
    {
        SearchTrace.Write($"[ResolveMagnet] url={detailUrl} useBrowser={useBrowser} magnetSelector=\"{magnetSelector}\" revealClick=\"{revealClickSelector ?? "-"}\"");

        if (!useBrowser)
        {
            var html = await GetHtmlAsync(detailUrl, cookie, ct);
            var m = await ExtractMagnetsAsync(html, detailUrl, magnetSelector, ct);
            SearchTrace.Write($"[ResolveMagnet] (HTTP) trovati: {m.Count}");
            return m;
        }

        // Prima un fetch "passivo" (senza click): se il contenuto è già sbloccato — es. "Ringrazia"
        // già cliccato in passato su questo post — evitiamo del tutto il click: più veloce e non
        // rischia di somigliare a un comportamento da bot agli occhi del sito. Se questo primo
        // tentativo fallisce/va in timeout, NON deve bloccare il tentativo con click: proviamo comunque.
        var magnets = new List<string>();
        try
        {
            var passiveHtml = await _browser.GetRenderedHtmlAsync(detailUrl, Math.Max(timeoutSeconds, 20), ct);
            SearchTrace.Write($"[ResolveMagnet] fetch passivo: {passiveHtml.Length} caratteri");
            magnets = await ExtractMagnetsAsync(passiveHtml, detailUrl, magnetSelector, ct);
        }
        catch (Exception ex)
        {
            SearchTrace.Write($"[ResolveMagnet] fetch passivo fallito ({ex.GetType().Name}: {ex.Message}), tento comunque il click di reveal");
        }

        if (magnets.Count > 0)
        {
            SearchTrace.Write($"[ResolveMagnet] {magnets.Count} magnet già presenti senza click");
            return magnets;
        }

        if (string.IsNullOrWhiteSpace(revealClickSelector))
        {
            SearchTrace.Write("[ResolveMagnet] nessun magnet e nessun revealClickSelector configurato: mi fermo");
            return magnets;
        }

        SearchTrace.Write("[ResolveMagnet] nessun magnet al primo giro, tento il click di reveal");
        // Naviga di nuovo + clicca + attende un eventuale reload: più lento, serve più margine.
        var afterClickHtml = await _browser.GetRenderedHtmlAfterClickAsync(detailUrl, revealClickSelector, Math.Max(timeoutSeconds * 2, 30), ct);
        SearchTrace.Write($"[ResolveMagnet] fetch dopo click: {afterClickHtml.Length} caratteri");
        var magnetsAfterClick = await ExtractMagnetsAsync(afterClickHtml, detailUrl, magnetSelector, ct);
        SearchTrace.Write($"[ResolveMagnet] trovati dopo click: {magnetsAfterClick.Count}");
        return magnetsAfterClick;
    }

    private static async Task<List<string>> ExtractMagnetsAsync(string html, string baseUrl, string magnetSelector, CancellationToken ct)
    {
        var results = new List<string>();

        var browsingContext = BrowsingContext.New(Configuration.Default);
        using var document = await browsingContext.OpenAsync(req => req.Content(html).Address(baseUrl), ct);

        foreach (var el in document.QuerySelectorAll(magnetSelector))
        {
            var href = el.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            results.Add(href.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? href : ResolveUrl(baseUrl, href));
        }

        return results;
    }

    /// <summary>
    /// GET con supporto opzionale a un cookie di sessione, per siti (es. forum privati) che richiedono login.
    /// </summary>
    private async Task<string> GetHtmlAsync(string url, string? cookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static string ResolveUrl(string baseUrl, string relativeOrAbsolute)
    {
        if (relativeOrAbsolute.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return relativeOrAbsolute;

        if (Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var abs))
            return abs.ToString();

        if (Uri.TryCreate(new Uri(baseUrl), relativeOrAbsolute, out var resolved))
            return resolved.ToString();

        return relativeOrAbsolute;
    }

    private static long ParseSeeds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var digits = new string(text.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var value) ? value : 0;
    }

    // Tracker pubblici standard aggiunti ai magnet costruiti dall'info_hash di apibay.org
    // (l'API non fornisce un magnet già pronto, solo l'hash — il magnet va composto a mano).
    private static readonly string[] ThePirateBayTrackers =
    {
        "udp://tracker.coppersurfer.tk:6969/announce",
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://tracker.internetwarriors.net:1337/announce",
        "udp://tracker.leechers-paradise.org:6969/announce",
        "udp://tracker.pirateparty.gr:6969/announce",
        "udp://tracker.cyberia.is:6969/announce"
    };

    /// <summary>
    /// Caso speciale: ThePirateBay non si scrapea via HTML, si interroga la sua API JSON
    /// pubblica non ufficiale (apibay.org) — più semplice e affidabile di un parsing CSS.
    /// </summary>
    private async Task<List<TorrentSearchResult>> SearchThePirateBayApiAsync(
        TorrentSiteConfig site, string query, int timeoutSeconds, int maxResults, CancellationToken ct)
    {
        var results = new List<TorrentSearchResult>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

        var url = $"https://apibay.org/q.php?q={Uri.EscapeDataString(query)}&cat=";
        string json;
        try
        {
            json = await _http.GetStringAsync(url, cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore interrogando l'API di {Site}", site.Name);
            SearchTrace.Write($"[SearchApi] sito={site.Name} ERRORE: {ex.GetType().Name}: {ex.Message}");
            return results;
        }

        SearchTrace.Write($"[SearchApi] sito={site.Name} risposta: {json.Length} caratteri");

        List<ThePirateBayApiItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<ThePirateBayApiItem>>(json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Risposta JSON non valida da {Site}", site.Name);
            return results;
        }

        if (items is null) return results;

        var trackerQs = string.Concat(ThePirateBayTrackers.Select(t => $"&tr={Uri.EscapeDataString(t)}"));

        foreach (var item in items)
        {
            if (results.Count >= maxResults) break;

            // apibay ritorna una singola riga "vuota" (hash tutto zero) quando non trova nulla.
            if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.InfoHash) ||
                item.InfoHash.TrimStart('0').Length == 0)
                continue;

            var magnet = $"magnet:?xt=urn:btih:{item.InfoHash}&dn={Uri.EscapeDataString(item.Name)}{trackerQs}";
            var sizeBytes = long.TryParse(item.Size, out var sz) ? sz : 0;

            results.Add(new TorrentSearchResult
            {
                SiteName = site.Name,
                Title = item.Name,
                Magnet = magnet,
                SizeText = FormatBytes(sizeBytes),
                SeedsText = item.Seeders,
                SeedsNumeric = ParseSeeds(item.Seeders)
            });
        }

        SearchTrace.Write($"[SearchApi] sito={site.Name} risultati: {results.Count}");
        return results;
    }

    // Torrentio (torrentio.strem.fun): API pubblica dell'addon Stremio omonimo, nessuna API key
    // richiesta. A differenza degli altri siti non accetta una query testuale: vuole un IMDb ID
    // e, per le serie, stagione+episodio già decisi (risolti/chiesti a monte in TelegramWorker).
    public async Task<List<TorrentSearchResult>> SearchTorrentioApiAsync(
        TorrentSiteConfig site, string imdbId, string mediaType, int? season, int? episode, int maxResults, CancellationToken ct)
    {
        var results = new List<TorrentSearchResult>();

        // Torrentio usa "series" nel path, non "tv" (che è invece il media_type usato da TMDB).
        var torrentioType = mediaType == "tv" ? "series" : "movie";
        var idPart = torrentioType == "series" && season.HasValue && episode.HasValue
            ? $"{imdbId}:{season}:{episode}"
            : imdbId;
        var url = $"https://torrentio.strem.fun/stream/{torrentioType}/{idPart}.json";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        string json;
        try
        {
            json = await _http.GetStringAsync(url, cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore interrogando Torrentio ({Url})", url);
            SearchTrace.Write($"[SearchApi] sito={site.Name} (Torrentio) ERRORE: {ex.GetType().Name}: {ex.Message}");
            return results;
        }

        SearchTrace.Write($"[SearchApi] sito={site.Name} (Torrentio) url={url} risposta: {json.Length} caratteri");

        TorrentioResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TorrentioResponse>(json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Risposta JSON non valida da Torrentio");
            return results;
        }

        if (payload?.Streams is null) return results;

        foreach (var stream in payload.Streams)
        {
            if (results.Count >= maxResults) break;
            if (string.IsNullOrWhiteSpace(stream.InfoHash)) continue;

            var filename = stream.BehaviorHints?.Filename;
            var titleRaw = stream.Title ?? "";
            var displayTitle = !string.IsNullOrWhiteSpace(filename) ? filename : titleRaw.Split('\n')[0];

            var seedsMatch = Regex.Match(titleRaw, "👤\\s*(\\d+)");
            var sizeMatch = Regex.Match(titleRaw, "💾\\s*([\\d.]+\\s*[KMGT]?B)");
            var sourceMatch = Regex.Match(titleRaw, "⚙️\\s*(\\S+)");

            var dn = filename is not null ? $"&dn={Uri.EscapeDataString(filename)}" : "";
            var magnet = $"magnet:?xt=urn:btih:{stream.InfoHash}{dn}";

            results.Add(new TorrentSearchResult
            {
                SiteName = site.Name,
                Title = displayTitle,
                Magnet = magnet,
                SizeText = sizeMatch.Success ? sizeMatch.Groups[1].Value : null,
                SeedsText = seedsMatch.Success ? seedsMatch.Groups[1].Value : null,
                SeedsNumeric = seedsMatch.Success && int.TryParse(seedsMatch.Groups[1].Value, out var sn) ? sn : 0,
                SourceListName = sourceMatch.Success ? sourceMatch.Groups[1].Value : null
            });
        }

        SearchTrace.Write($"[SearchApi] sito={site.Name} (Torrentio) risultati: {results.Count}");
        return results;
    }

    private class TorrentioResponse
    {
        [JsonPropertyName("streams")]
        public List<TorrentioStream>? Streams { get; set; }
    }

    private class TorrentioStream
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("infoHash")]
        public string? InfoHash { get; set; }

        [JsonPropertyName("behaviorHints")]
        public TorrentioBehaviorHints? BehaviorHints { get; set; }
    }

    private class TorrentioBehaviorHints
    {
        [JsonPropertyName("filename")]
        public string? Filename { get; set; }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:F1} {units[unit]}";
    }

    private class ThePirateBayApiItem
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("info_hash")] public string? InfoHash { get; set; }
        [JsonPropertyName("seeders")] public string? Seeders { get; set; }
        [JsonPropertyName("size")] public string? Size { get; set; }
    }
}
