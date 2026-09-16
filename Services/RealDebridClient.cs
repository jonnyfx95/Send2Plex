using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Secondo provider debrid (docs/piano-multi-provider-debrid.md), scelto manualmente per singolo
/// file accanto ad AllDebrid — nessun fallback automatico. Implementa <see cref="IDebridClient"/>
/// contro l'API REST di Real-Debrid (api.real-debrid.com/rest/1.0), documentata nel piano.
/// Configurazione opzionale (pattern <c>IsConfigured</c> di <see cref="TmdbClient"/>/
/// <see cref="OmdbClient"/>): l'app non richiede questa API key all'avvio, a differenza di AllDebrid.
/// </summary>
public class RealDebridClient : IDebridClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ConfigStore _configStore;
    private readonly ILogger<RealDebridClient> _log;

    private RealDebridSettings _cfg => _configStore.Current.RealDebrid;

    private const string Base = "https://api.real-debrid.com/rest/1.0";

    public RealDebridClient(
        IHttpClientFactory httpFactory,
        ConfigStore configStore,
        ILogger<RealDebridClient> log)
    {
        _httpFactory = httpFactory;
        _configStore = configStore;
        _log = log;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_cfg.ApiKey);

    private HttpClient CreateClient()
    {
        var http = _httpFactory.CreateClient("RD");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new Exception(BuildErrorMessage((int)resp.StatusCode, body));
    }

    // Traduce i codici errore di Real-Debrid più comuni in un messaggio leggibile in italiano,
    // mostrato così com'è all'utente (Search.razor/AllDebrid.razor/webOS) — senza questa mappatura
    // arrivava il JSON grezzo dell'API ("infringing_file", "bad_token", ...), incomprensibile senza
    // conoscere la documentazione di Real-Debrid. Fallback sul messaggio grezzo per i codici non
    // ancora mappati, così resta comunque diagnosticabile.
    private static string BuildErrorMessage(int statusCode, string body)
    {
        string? errorCode = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            errorCode = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch
        {
            // Corpo non JSON (es. errore di rete/gateway a monte di Real-Debrid): ignorato, si
            // ricade sul messaggio grezzo sotto.
        }

        return errorCode switch
        {
            "infringing_file" => "Real-Debrid ha bloccato questo file per violazione di copyright (release segnalata) — prova un'altra fonte/release dello stesso titolo, oppure usa AllDebrid per questo file.",
            "bad_token" => "Chiave API Real-Debrid non valida o scaduta — controlla la chiave in Impostazioni sul PC.",
            "permission_denied" => "Real-Debrid ha negato l'operazione (permessi insufficienti sull'account, es. account gratuito/scaduto).",
            "magnet_error" => "Real-Debrid non è riuscito a leggere questo magnet (torrent non valido o non raggiungibile).",
            _ => $"Real-Debrid API error ({statusCode}): {body}"
        };
    }

    // ----------------------------------------------------
    // LINK: unrestrict (POST /unrestrict/link)
    // ----------------------------------------------------
    public async Task<string> UnlockLinkAsync(string url, CancellationToken ct)
    {
        using var http = CreateClient();
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("link", url) });
        using var resp = await http.PostAsync($"{Base}/unrestrict/link", content, ct);
        await EnsureSuccessAsync(resp, ct);

        var payload = await resp.Content.ReadFromJsonAsync<RdUnrestrictResponse>(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(payload?.Download))
            throw new Exception("Real-Debrid: unrestrict non ha restituito un link valido.");

        return payload.Download;
    }

    // ----------------------------------------------------
    // MAGNET: upload (addMagnet + selectFiles, due chiamate nascoste dietro un'unica firma)
    // ----------------------------------------------------
    public async Task<string> UploadMagnetAsync(string magnet, CancellationToken ct)
    {
        using var http = CreateClient();

        // DOMANDA APERTA (vedi piano): se "host" risultasse davvero obbligatorio per un magnet (a
        // differenza di un link diretto), qui servirebbe prima GET /torrents/availableHosts. Non
        // richiesto nei test iniziali: se l'account fallisce con un errore relativo a "host",
        // aggiungere quella chiamata preliminare.
        using var addContent = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("magnet", magnet) });
        using var addResp = await http.PostAsync($"{Base}/torrents/addMagnet", addContent, ct);
        await EnsureSuccessAsync(addResp, ct);

        var added = await addResp.Content.ReadFromJsonAsync<RdAddMagnetResponse>(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(added?.Id))
            throw new Exception("Real-Debrid: risposta addMagnet senza id.");

        using var selectContent = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("files", "all") });
        using var selectResp = await http.PostAsync($"{Base}/torrents/selectFiles/{added.Id}", selectContent, ct);
        await EnsureSuccessAsync(selectResp, ct);

        _log.LogInformation("Magnet caricato su Real-Debrid, id={Id}", added.Id);
        return added.Id;
    }

    private async Task<RdTorrentInfo?> GetTorrentInfoAsync(string id, CancellationToken ct)
    {
        using var http = CreateClient();
        using var resp = await http.GetAsync($"{Base}/torrents/info/{id}", ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<RdTorrentInfo>(cancellationToken: ct);
    }

    public async Task<bool> WaitReadyAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var first = true;

        while (DateTime.UtcNow - start < timeout)
        {
            if (first) { first = false; await Task.Delay(2500, ct); }

            RdTorrentInfo? info;
            try
            {
                info = await GetTorrentInfoAsync(id, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Real-Debrid: errore leggendo lo stato del torrent {Id}, retry tra 5s…", id);
                await Task.Delay(5000, ct);
                continue;
            }

            var status = info?.Status ?? "";
            if (string.Equals(status, "downloaded", StringComparison.OrdinalIgnoreCase))
                return true;

            if (status is "magnet_error" or "error" or "virus" or "dead")
            {
                _log.LogError("Real-Debrid: torrent {Id} in stato terminale di errore ({Status})", id, status);
                return false;
            }

            await Task.Delay(5000, ct);
        }

        _log.LogError("Timeout: torrent Real-Debrid {Id} non è diventato ready", id);
        return false;
    }

    public async Task<List<MagnetInfo>> GetMagnetsAsync(CancellationToken ct)
    {
        using var http = CreateClient();
        using var resp = await http.GetAsync($"{Base}/torrents", ct);
        await EnsureSuccessAsync(resp, ct);

        var list = await resp.Content.ReadFromJsonAsync<List<RdTorrentListItem>>(cancellationToken: ct) ?? new();
        return list.Select(t => new MagnetInfo
        {
            Id = t.Id ?? "",
            Filename = t.Filename ?? "",
            Size = t.Bytes,
            Status = NormalizeStatus(t.Status),
            UploadDate = ParseUnixSeconds(t.Added)
        }).ToList();
    }

    // AllDebrid restituisce "Ready" per un magnet pronto (controllato case-insensitive da
    // MagnetInfo.IsReady); Real-Debrid usa "downloaded" — normalizzato qui così l'UI condivisa
    // (AllDebrid.razor/webOS) riconosce "pronto" allo stesso modo per entrambi i provider.
    private static string NormalizeStatus(string? rdStatus) =>
        string.Equals(rdStatus, "downloaded", StringComparison.OrdinalIgnoreCase) ? "Ready" : (rdStatus ?? "");

    private static long ParseUnixSeconds(string? isoDate) =>
        DateTimeOffset.TryParse(isoDate, out var dt) ? dt.ToUnixTimeSeconds() : 0;

    public async Task<List<string>> GetMagnetLinksAsync(string id, CancellationToken ct)
    {
        var info = await GetTorrentInfoAsync(id, ct);
        return info?.Links ?? new List<string>();
    }

    public async Task<List<MagnetFile>> GetMagnetFilesDetailedAsync(string id, CancellationToken ct)
    {
        var info = await GetTorrentInfoAsync(id, ct);
        if (info is null) return new List<MagnetFile>();

        var files = (info.Files ?? new()).Where(f => f.Selected == 1).OrderBy(f => f.Id).ToList();
        var links = info.Links ?? new List<string>();

        // I file selezionati sono restituiti da Real-Debrid nello stesso ordine dei link sbloccabili
        // in "links[]" (un link per file selezionato) — non c'è un id esplicito che li leghi, quindi
        // si abbina per posizione. Selezioniamo sempre "all" in UploadMagnetAsync, quindi i conteggi
        // dovrebbero sempre coincidere; se non coincidessero (risposta inattesa), i file in eccesso
        // restano senza link e vengono scartati invece di disallinearsi.
        var result = new List<MagnetFile>();
        for (int i = 0; i < files.Count && i < links.Count; i++)
        {
            var path = files[i].Path ?? "";
            result.Add(new MagnetFile
            {
                Name = string.IsNullOrEmpty(path) ? "File sconosciuto" : path.TrimStart('/').Split('/').Last(),
                Size = files[i].Bytes,
                Link = links[i]
            });
        }

        return result;
    }

    public async Task DeleteMagnetAsync(string id, CancellationToken ct)
    {
        using var http = CreateClient();
        using var resp = await http.DeleteAsync($"{Base}/torrents/delete/{id}", ct);
        await EnsureSuccessAsync(resp, ct);
        _log.LogInformation("Torrent Real-Debrid {Id} cancellato", id);
    }

    private class RdUnrestrictResponse
    {
        [JsonPropertyName("download")]
        public string? Download { get; set; }
    }

    private class RdAddMagnetResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private class RdTorrentListItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("bytes")]
        public long Bytes { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("added")]
        public string? Added { get; set; }
    }

    private class RdTorrentInfo
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("files")]
        public List<RdTorrentFile>? Files { get; set; }

        [JsonPropertyName("links")]
        public List<string>? Links { get; set; }
    }

    private class RdTorrentFile
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("bytes")]
        public long Bytes { get; set; }

        [JsonPropertyName("selected")]
        public int Selected { get; set; }
    }
}
