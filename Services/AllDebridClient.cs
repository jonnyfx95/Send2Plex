using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

public class AllDebridClient : IDebridClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ConfigStore _configStore;
    private readonly ILogger<AllDebridClient> _log;

    // Letta ad ogni chiamata: la API Key può cambiare a caldo da Impostazioni (Punto 2).
    private AllDebridSettings _cfg => _configStore.Current.AllDebrid;

    private const string Base = "https://api.alldebrid.com";

    public AllDebridClient(
        IHttpClientFactory httpFactory,
        ConfigStore configStore,
        ILogger<AllDebridClient> log)
    {
        _httpFactory = httpFactory;
        _configStore = configStore;
        _log = log;
    }

    private HttpClient CreateClient()
    {
        var http = _httpFactory.CreateClient("AD");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private async Task<JsonDocument> PostFormAsync(HttpClient http, string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        var redactedPayload = string.Join("&", form.Select(kv =>
        {
            var isSensitive = kv.Key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                               kv.Key.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                               kv.Key.Contains("apikey", StringComparison.OrdinalIgnoreCase);
            return $"{kv.Key}={(isSensitive ? "***REDACTED***" : kv.Value)}";
        }));
        _log.LogInformation("POST {Url} con payload: {Payload}", url, redactedPayload);

        using var content = new FormUrlEncodedContent(form);
        using var resp = await http.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        _log.LogDebug("Risposta HTTP {Status}: {Body}", resp.StatusCode, body);

        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(body);
    }

    private static void EnsureOk(JsonElement root)
    {
        if (root.TryGetProperty("status", out var st) &&
            string.Equals(st.GetString(), "success", StringComparison.OrdinalIgnoreCase))
            return;

        string code = root.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
        string msg = root.TryGetProperty("error", out err) && err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        throw new Exception(string.IsNullOrWhiteSpace(code + msg)
            ? "AllDebrid API error."
            : $"AllDebrid API error: {code} {msg}".Trim());
    }

    private static string GetAsString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => el.ToString() ?? string.Empty
    };

    private static JsonElement GetFirstMagnet(JsonElement root)
    {
        var data = root.GetProperty("data");
        if (data.TryGetProperty("magnets", out var magnetsEl))
        {
            if (magnetsEl.ValueKind == JsonValueKind.Array && magnetsEl.GetArrayLength() > 0)
                return magnetsEl[0];
            if (magnetsEl.ValueKind == JsonValueKind.Object)
                return magnetsEl;
        }
        if (data.TryGetProperty("magnet", out var magnetEl) && magnetEl.ValueKind == JsonValueKind.Object)
            return magnetEl;

        throw new Exception("AllDebrid: nessun magnet trovato nella risposta.");
    }

    private static bool IsInvalidIdError(Exception ex)
        => ex.Message.Contains("MAGNET_INVALID_ID", StringComparison.OrdinalIgnoreCase);


    // ----------------------------------------------------
    // LINK: unlock (POST /v4/link/unlock)
    // ----------------------------------------------------
    public async Task<string> UnlockLinkAsync(string url, CancellationToken ct)
    {
        using var http = CreateClient();

        const int maxAttempts = 3;
        string? lastLink = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var doc = await PostFormAsync(
                http,
                $"{Base}/v4/link/unlock",
                new[] { new KeyValuePair<string, string>("link", url) },
                ct
            );

            EnsureOk(doc.RootElement);
            var data = doc.RootElement.GetProperty("data");

            if (data.TryGetProperty("link", out var linkEl))
            {
                var direct = GetAsString(linkEl);
                lastLink = direct;

                var host = new Uri(direct).Host;
                _log.LogInformation("🔓 Unlock tentativo {Attempt}/{Max}: {Link}", attempt, maxAttempts, direct);

                // Check se host è problematico
                if (host.Contains("c1uw74") || host.Contains("o7p8q9"))
                {
                    _log.LogWarning("⚠️ Link sbloccato su host problematico: {Host}", host);

                    if (attempt < maxAttempts)
                    {
                        _log.LogInformation("↻ Riprovo a generare un link alternativo...");
                        await Task.Delay(1500, ct); // breve pausa
                        continue;
                    }
                }

                return direct; // link accettabile → ritorno
            }
        }

        if (lastLink != null)
        {
            _log.LogWarning("⚠️ Restituisco link problematico dopo {Max} tentativi: {Link}", maxAttempts, lastLink);
            return lastLink;
        }

        throw new Exception("AllDebrid: unlock non ha restituito un link valido.");
    }


    // ----------------------------------------------------
    // MAGNET: upload (POST /v4/magnet/upload con magnets[])
    // ----------------------------------------------------
    public async Task<string> UploadMagnetAsync(string magnet, CancellationToken ct)
    {
        using var http = CreateClient();

        using var doc = await PostFormAsync(
            http,
            $"{Base}/v4/magnet/upload",
            new[] { new KeyValuePair<string, string>("magnets[]", magnet) },
            ct
        );

        EnsureOk(doc.RootElement);
        var first = GetFirstMagnet(doc.RootElement);

        // La chiamata può rispondere "success" a livello globale ma segnalare un errore sul
        // singolo magnet (es. nessun peer disponibile in quel momento): in quel caso non c'è
        // nessun campo "id" da leggere, va segnalato con l'errore vero invece di un generico
        // "chiave non trovata".
        if (first.TryGetProperty("error", out var magnetErr))
        {
            var code = magnetErr.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var msg = magnetErr.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            throw new Exception($"AllDebrid: magnet non processabile ({code} {msg})".Trim());
        }

        if (!first.TryGetProperty("id", out var idEl))
            throw new Exception("AllDebrid: risposta magnet senza id.");

        var id = GetAsString(idEl);

        if (string.IsNullOrWhiteSpace(id))
            throw new Exception("AllDebrid: id magnet vuoto.");

        _log.LogInformation("Magnet caricato, id={Id}", id);
        return id;
    }

    // ----------------------------------------------------
    // MAGNET: status (POST /v4.1/magnet/status)
    // ----------------------------------------------------
    private async Task<string> GetMagnetStatusValueAsync(string id, CancellationToken ct)
    {
        using var http = CreateClient();

        using var doc = await PostFormAsync(
            http,
            $"{Base}/v4.1/magnet/status",
            new[] { new KeyValuePair<string, string>("id", id) },
            ct
        );

        EnsureOk(doc.RootElement);
        var first = GetFirstMagnet(doc.RootElement);
        var status = first.TryGetProperty("status", out var stEl) ? GetAsString(stEl) : "";
        _log.LogInformation("Magnet {Id} -> status={Status}", id, status);
        return status;
    }

    public async Task<List<MagnetInfo>> GetMagnetsAsync(CancellationToken ct)
    {
        using var http = CreateClient();

        using var doc = await PostFormAsync(
            http,
            $"{Base}/v4.1/magnet/status",
            Array.Empty<KeyValuePair<string, string>>(),
            ct
        );

        EnsureOk(doc.RootElement);
        var data = doc.RootElement.GetProperty("data");
        var magnets = new List<MagnetInfo>();

        if (data.TryGetProperty("magnets", out var magnetsEl) && magnetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in magnetsEl.EnumerateArray())
            {
                var info = new MagnetInfo
                {
                    Id = m.TryGetProperty("id", out var idEl) ? GetAsString(idEl) : string.Empty,
                    Filename = m.TryGetProperty("filename", out var fEl) ? GetAsString(fEl) : string.Empty,
                    Size = m.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out var s) ? s : 0,
                    Status = m.TryGetProperty("status", out var stEl) ? GetAsString(stEl) : string.Empty,
                    UploadDate = m.TryGetProperty("uploadDate", out var uEl) && uEl.TryGetInt64(out var u) ? u : 0
                };
                magnets.Add(info);
            }
        }

        return magnets;
    }

    public async Task<bool> WaitReadyAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var first = true;

        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                if (first) { first = false; await Task.Delay(2500, ct); }
                var status = await GetMagnetStatusValueAsync(id, ct);
                if (string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase))
                    return true;

                // BUG REALE (attesa di un'ora per un magnet chiaramente morto): senza questo
                // controllo si aspettava l'intero timeout anche per un torrent segnalato in
                // errore da AllDebrid (nessun peer, hoster non raggiungibile, ecc.) invece di
                // fallire subito e notificare l'utente. Stessa stringa già usata per il badge
                // rosso in AllDebrid.razor (BadgeClass), quindi affidabile: gli stati di errore
                // di AllDebrid contengono sempre "error" nel testo.
                if (status.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogError("AllDebrid: magnet {Id} in stato di errore ({Status}), interrotto in anticipo", id, status);
                    return false;
                }
            }
            catch (Exception ex) when (IsInvalidIdError(ex))
            {
                _log.LogWarning("MAGNET_INVALID_ID per id={Id}, retry tra 2s…", id);
                await Task.Delay(2000, ct);
                continue;
            }

            await Task.Delay(5000, ct);
        }
        _log.LogError("Timeout: magnet {Id} non è diventato ready", id);
        return false;
    }

    // ----------------------------------------------------
    // MAGNET: files (POST /v4/magnet/files con id[])
    // ----------------------------------------------------
    public async Task<List<string>> GetMagnetLinksAsync(string id, CancellationToken ct)
    {
        using var http = CreateClient();

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var doc = await PostFormAsync(
                    http,
                    $"{Base}/v4/magnet/files",
                    new[] { new KeyValuePair<string, string>("id[]", id) },
                    ct
                );

                EnsureOk(doc.RootElement);

                var data = doc.RootElement.GetProperty("data").GetProperty("magnets");
                JsonElement magnetObj;

                if (data.ValueKind == JsonValueKind.Array)
                {
                    magnetObj = data.EnumerateArray().FirstOrDefault(m =>
                        m.TryGetProperty("id", out var idEl) && GetAsString(idEl) == id);
                }
                else
                {
                    magnetObj = data;
                }

                if (magnetObj.ValueKind == JsonValueKind.Undefined)
                    throw new Exception("AllDebrid: magnet non trovato in /files.");

                var links = new List<string>();
                if (magnetObj.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                    CollectLinksRecursive(filesEl, links);

                _log.LogInformation("Raccolti {Count} link per magnet {Id}", links.Count, id);
                return links;
            }
            catch (Exception ex) when (IsInvalidIdError(ex) && attempt < 2)
            {
                _log.LogWarning("MAGNET_INVALID_ID su /files per id={Id}, retry…", id);
                await Task.Delay(2000, ct);
                continue;
            }
        }

        throw new Exception($"AllDebrid: impossibile ottenere file per magnet {id} (MAGNET_INVALID_ID persistente).");
    }

    private static void CollectLinksRecursive(JsonElement filesArray, List<string> output)
    {
        foreach (var item in filesArray.EnumerateArray())
        {
            if (item.TryGetProperty("l", out var linkEl) && linkEl.ValueKind == JsonValueKind.String)
            {
                var url = linkEl.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                    output.Add(url!);
            }

            if (item.TryGetProperty("e", out var entries) && entries.ValueKind == JsonValueKind.Array)
                CollectLinksRecursive(entries, output);
        }
    }

    public async Task<List<MagnetFile>> GetMagnetFilesDetailedAsync(string id, CancellationToken ct)
    {
        using var http = CreateClient();

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var doc = await PostFormAsync(
                    http,
                    $"{Base}/v4/magnet/files",
                    new[] { new KeyValuePair<string, string>("id[]", id) },
                    ct
                );

                EnsureOk(doc.RootElement);

                var data = doc.RootElement.GetProperty("data").GetProperty("magnets");
                JsonElement magnetObj;

                if (data.ValueKind == JsonValueKind.Array)
                {
                    magnetObj = data.EnumerateArray().FirstOrDefault(m =>
                        m.TryGetProperty("id", out var idEl) && GetAsString(idEl) == id);
                }
                else
                {
                    magnetObj = data;
                }

                if (magnetObj.ValueKind == JsonValueKind.Undefined)
                    throw new Exception("AllDebrid: magnet non trovato in /files.");

                var files = new List<MagnetFile>();
                if (magnetObj.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                    CollectDetailedFilesRecursive(filesEl, files);

                _log.LogInformation("Raccolti {Count} file dettagliati per magnet {Id}", files.Count, id);
                return files;
            }
            catch (Exception ex) when (IsInvalidIdError(ex) && attempt < 2)
            {
                _log.LogWarning("MAGNET_INVALID_ID su /files per id={Id}, retry…", id);
                await Task.Delay(2000, ct);
                continue;
            }
        }

        throw new Exception($"AllDebrid: impossibile ottenere file dettagliati per magnet {id} (MAGNET_INVALID_ID persistente).");
    }

    private static void CollectDetailedFilesRecursive(JsonElement filesArray, List<MagnetFile> output)
    {
        foreach (var item in filesArray.EnumerateArray())
        {
            if (item.TryGetProperty("l", out var linkEl) && linkEl.ValueKind == JsonValueKind.String)
            {
                var url = linkEl.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    output.Add(new MagnetFile
                    {
                        Name = item.TryGetProperty("n", out var nEl) ? GetAsString(nEl) : "File sconosciuto",
                        Size = item.TryGetProperty("s", out var sEl) && sEl.TryGetInt64(out var s) ? s : 0,
                        Link = url!
                    });
                }
            }

            if (item.TryGetProperty("e", out var entries) && entries.ValueKind == JsonValueKind.Array)
                CollectDetailedFilesRecursive(entries, output);
        }
    }

    // ----------------------------------------------------
    // MAGNET: delete (POST /v4/magnet/delete)
    // ----------------------------------------------------
    public async Task DeleteMagnetAsync(string id, CancellationToken ct)
    {
        using var http = CreateClient();

        using var _ = await PostFormAsync(
            http,
            $"{Base}/v4/magnet/delete",
            new[] { new KeyValuePair<string, string>("id", id) },
            ct
        );

        _log.LogInformation("Magnet {Id} cancellato", id);
    }

    /// <summary>
    /// Sblocca più link in batch con gestione errori migliorata per multi-file
    /// </summary>
    public async Task<List<string>> UnlockLinksAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        var results = new List<string>();
        var urlList = urls.ToList();

        _log.LogInformation("🔓 Sbloccando {Count} link in batch", urlList.Count);

        foreach (var (url, index) in urlList.Select((url, i) => (url, i)))
        {
            try
            {
                _log.LogInformation("🔓 Sbloccando link {Index}/{Total}: {Url}",
                    index + 1, urlList.Count, url);

                // 👉 Usa la nuova logica di UnlockLinkAsync (con retry sugli host problematici)
                var directUrl = await UnlockLinkAsync(url, ct);
                results.Add(directUrl);

                _log.LogInformation("✅ Link {Index}/{Total} sbloccato correttamente: {DirectUrl}",
                    index + 1, urlList.Count, directUrl);

                // Piccola pausa tra le richieste per evitare rate limiting
                if (index < urlList.Count - 1)
                    await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "❌ Errore sbloccando link {Index}/{Total}: {Url}",
                    index + 1, urlList.Count, url);

                // Per torrent multi-file, continuiamo anche se un link fallisce
                continue;
            }
        }

        _log.LogInformation("✅ Sbloccati {Success}/{Total} link", results.Count, urlList.Count);
        return results;
    }
}