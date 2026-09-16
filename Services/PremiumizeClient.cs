using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Terzo provider debrid (docs/piano-premiumize-libreria.md), scelto manualmente accanto ad
/// AllDebrid/Real-Debrid — stesso identico pattern di <see cref="RealDebridClient"/> (nessun
/// fallback automatico, configurazione opzionale via <see cref="IsConfigured"/>, non richiesta
/// all'avvio). Implementa <see cref="IDebridClient"/> contro l'API REST di Premiumize.me
/// (www.premiumize.me/api).
///
/// Schema verificato con l'API key reale dell'utente (2026-09-13): transfer/list (campi
/// file_id/folder_id, quest'ultimo null per un transfer a file singolo), item/details e
/// folder/list (compreso l'attraversamento di sottocartelle per lo sfoglio manuale delle cartelle
/// utente, <see cref="ListFolderAsync"/>). Non ancora testato: transfer/create con un pacco
/// multi-file (season pack) — vedi piano-premiumize-libreria.md per il dettaglio.
/// </summary>
public class PremiumizeClient : IDebridClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ConfigStore _configStore;
    private readonly ILogger<PremiumizeClient> _log;

    private PremiumizeSettings _cfg => _configStore.Current.Premiumize;

    private const string Base = "https://www.premiumize.me/api";

    public PremiumizeClient(
        IHttpClientFactory httpFactory,
        ConfigStore configStore,
        ILogger<PremiumizeClient> log)
    {
        _httpFactory = httpFactory;
        _configStore = configStore;
        _log = log;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_cfg.ApiKey);

    private HttpClient CreateClient() => _httpFactory.CreateClient("PM");

    // Premiumize accetta l'API key come parametro di query/form "apikey" (non un header Bearer
    // come Real-Debrid) — aggiunta ad ogni chiamata, GET o POST, invece di un header comune.
    private string WithKey(string url) => url.Contains('?') ? $"{url}&apikey={_cfg.ApiKey}" : $"{url}?apikey={_cfg.ApiKey}";

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);

        // Premiumize torna HTTP 200 anche per molti errori applicativi, con {"status":"error",
        // "message":"..."} nel corpo — controllato esplicitamente, non solo IsSuccessStatusCode.
        string? status = null, message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch
        {
            // Corpo non JSON: si ricade sul solo status code HTTP sotto.
        }

        if (resp.IsSuccessStatusCode && !string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            return;

        throw new Exception(string.IsNullOrWhiteSpace(message)
            ? $"Premiumize API error ({(int)resp.StatusCode}): {body}"
            : $"Premiumize: {message}");
    }

    // ----------------------------------------------------
    // LINK: sblocco diretto (POST /transfer/directdl) — usato dal pulsante "Sblocca un link" di
    // AllDebrid.razor. DA VERIFICARE: se accetta solo link diretti da hoster o anche magnet (vedi
    // domanda aperta nel piano) — se non accettasse magnet, il flusso "guarda subito" per un
    // risultato di ricerca dovrebbe comunque passare da UploadMagnetAsync+WaitReadyAsync come gli
    // altri due provider, non da qui.
    // ----------------------------------------------------
    public async Task<string> UnlockLinkAsync(string url, CancellationToken ct)
    {
        using var http = CreateClient();
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("src", url) });
        using var resp = await http.PostAsync(WithKey($"{Base}/transfer/directdl"), content, ct);
        await EnsureSuccessAsync(resp, ct);

        var payload = await resp.Content.ReadFromJsonAsync<PmDirectDlResponse>(cancellationToken: ct);
        var first = payload?.Content?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first?.Link))
            throw new Exception("Premiumize: directdl non ha restituito un link valido.");

        return first.Link;
    }

    // ----------------------------------------------------
    // MAGNET: upload (POST /transfer/create) — a differenza di AllDebrid/RD non scarica un file
    // temporaneo ma avvia un transfer nel cloud Premiumize (stesso meccanismo dietro la feature
    // "Acquisisci in libreria", non ancora implementata). Per il flusso classico
    // "scarica su Plex"/"guarda in streaming" qui serve comunque solo l'id per interrogare lo stato
    // e poi i link dei file via folder/list, esattamente come gli altri provider.
    // ----------------------------------------------------
    public async Task<string> UploadMagnetAsync(string magnet, CancellationToken ct)
    {
        using var http = CreateClient();
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("src", magnet) });
        using var resp = await http.PostAsync(WithKey($"{Base}/transfer/create"), content, ct);
        await EnsureSuccessAsync(resp, ct);

        var payload = await resp.Content.ReadFromJsonAsync<PmCreateTransferResponse>(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(payload?.Id))
            throw new Exception("Premiumize: risposta transfer/create senza id.");

        _log.LogInformation("Magnet caricato su Premiumize, id={Id}", payload.Id);
        return payload.Id;
    }

    private async Task<PmTransfer?> GetTransferAsync(string id, CancellationToken ct)
    {
        using var http = CreateClient();
        using var resp = await http.GetAsync(WithKey($"{Base}/transfer/list"), ct);
        await EnsureSuccessAsync(resp, ct);

        var payload = await resp.Content.ReadFromJsonAsync<PmTransferListResponse>(cancellationToken: ct);
        return payload?.Transfers?.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> WaitReadyAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var first = true;

        while (DateTime.UtcNow - start < timeout)
        {
            if (first) { first = false; await Task.Delay(2500, ct); }

            PmTransfer? transfer;
            try
            {
                transfer = await GetTransferAsync(id, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Premiumize: errore leggendo lo stato del transfer {Id}, retry tra 5s…", id);
                await Task.Delay(5000, ct);
                continue;
            }

            var status = transfer?.Status ?? "";
            if (string.Equals(status, "finished", StringComparison.OrdinalIgnoreCase))
                return true;

            if (status is "error" or "timeout" or "banned" or "deleted")
            {
                _log.LogError("Premiumize: transfer {Id} in stato terminale di errore ({Status})", id, status);
                return false;
            }

            await Task.Delay(5000, ct);
        }

        _log.LogError("Timeout: transfer Premiumize {Id} non è diventato ready", id);
        return false;
    }

    // Quante chiamate item/details o folder/list tenere in volo insieme per riempire le dimensioni
    // (vedi GetMagnetsAsync sotto) — un limite basso per restare gentili con l'API di Premiumize
    // anche quando la lista magnet è lunga (decine di transfer), invece di sparare tutte le
    // richieste in un colpo solo.
    private const int SizeLookupConcurrency = 4;

    public async Task<List<MagnetInfo>> GetMagnetsAsync(CancellationToken ct)
    {
        using var http = CreateClient();
        using var resp = await http.GetAsync(WithKey($"{Base}/transfer/list"), ct);
        await EnsureSuccessAsync(resp, ct);

        var payload = await resp.Content.ReadFromJsonAsync<PmTransferListResponse>(cancellationToken: ct);
        var list = payload?.Transfers ?? new();

        // BUG REALE (screenshot utente, 2026-09-14): transfer/list non riporta la dimensione totale
        // del file — mostrava sempre "0 B" per ogni riga. Verificato dal vivo (docs/idee-
        // miglioramento-webos.md) che sia item/details (transfer a file singolo) sia folder/list
        // (transfer multi-file) riportano invece la size reale per file — la stessa chiamata già
        // usata da GetMagnetFilesDetailedAsync per lo sfoglio dei file di un magnet, qui invocata
        // una volta per riga della lista con concorrenza limitata (SizeLookupConcurrency) invece che
        // in sequenza. Cercato anche un endpoint che desse le size in blocco per tutti i transfer
        // insieme (evitando N chiamate extra): non trovato un riscontro affidabile nella
        // documentazione né in altri client (es. rclone), quindi si è scelta questa strada, già
        // verificata su dati reali dell'account.
        using var sizeGate = new SemaphoreSlim(SizeLookupConcurrency);
        var sizes = await Task.WhenAll(list.Select(async t =>
        {
            await sizeGate.WaitAsync(ct);
            try { return await GetTransferSizeAsync(t, ct); }
            finally { sizeGate.Release(); }
        }));

        return list.Zip(sizes, (t, size) => new MagnetInfo
        {
            Id = t.Id ?? "",
            Filename = t.Name ?? "",
            Size = size,
            Status = NormalizeStatus(t.Status),
            UploadDate = 0 // transfer/list non riporta una data di creazione (da confermare).
        }).ToList();
    }

    // Stessa logica di GetMagnetFilesDetailedAsync (file singolo -> item/details, pacco multi-file
    // -> folder/list) ma senza rifare la chiamata a transfer/list per ogni riga: il PmTransfer è
    // già quello ottenuto dalla lista completa in GetMagnetsAsync. "Best effort": un errore su
    // UNA riga (rate-limit, transfer in stato strano) torna 0 invece di far fallire l'intera lista.
    private async Task<long> GetTransferSizeAsync(PmTransfer t, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(t.FileId))
            {
                var file = await GetItemDetailsAsync(t.FileId, ct);
                return file?.Size ?? 0;
            }

            if (!string.IsNullOrWhiteSpace(t.FolderId))
            {
                using var http = CreateClient();
                using var resp = await http.GetAsync(WithKey($"{Base}/folder/list?id={t.FolderId}"), ct);
                await EnsureSuccessAsync(resp, ct);
                var payload = await resp.Content.ReadFromJsonAsync<PmFolderListResponse>(cancellationToken: ct);
                return (payload?.Content ?? new())
                    .Where(f => string.Equals(f.Type, "file", StringComparison.OrdinalIgnoreCase))
                    .Sum(f => f.Size);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Premiumize: impossibile leggere la dimensione del transfer {Id}, mostro 0 B", t.Id);
        }

        return 0;
    }

    // AllDebrid usa "Ready", Real-Debrid "downloaded", Premiumize "finished" — normalizzato qui
    // così l'UI condivisa (AllDebrid.razor/webOS) riconosce "pronto" allo stesso modo per tutti.
    private static string NormalizeStatus(string? pmStatus) =>
        string.Equals(pmStatus, "finished", StringComparison.OrdinalIgnoreCase) ? "Ready" : (pmStatus ?? "");

    public async Task<List<string>> GetMagnetLinksAsync(string id, CancellationToken ct)
    {
        var files = await GetMagnetFilesDetailedAsync(id, ct);
        return files.Select(f => f.Link).ToList();
    }

    public async Task<List<MagnetFile>> GetMagnetFilesDetailedAsync(string id, CancellationToken ct)
    {
        var transfer = await GetTransferAsync(id, ct);
        if (transfer is null) return new List<MagnetFile>();

        // VERIFICATO con un test reale (2026-09-13, un singolo episodio): un transfer con un solo
        // file valorizza "file_id" e lascia "folder_id" null — serve item/details, non folder/list
        // (che a sua volta darebbe "Nessun file trovato" perché non esiste alcuna cartella).
        if (!string.IsNullOrWhiteSpace(transfer.FileId))
        {
            var file = await GetItemDetailsAsync(transfer.FileId, ct);
            return file is null ? new List<MagnetFile>() : new List<MagnetFile> { file };
        }

        if (string.IsNullOrWhiteSpace(transfer.FolderId))
            return new List<MagnetFile>();

        using var http = CreateClient();
        using var resp = await http.GetAsync(WithKey($"{Base}/folder/list?id={transfer.FolderId}"), ct);
        await EnsureSuccessAsync(resp, ct);

        var payload = await resp.Content.ReadFromJsonAsync<PmFolderListResponse>(cancellationToken: ct);
        var content = payload?.Content ?? new();

        // Solo i file (type "file") con un link diretto valorizzato — le sottocartelle (type
        // "folder") non sono attraversate qui: per un pacco multi-file con sottocartelle andrebbe
        // fatta una seconda chiamata ricorsiva su ListFolderAsync per ciascuna (vedi quel metodo,
        // che invece le espone entrambe per lo sfoglio manuale delle cartelle utente).
        return content
            .Where(f => string.Equals(f.Type, "file", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(f.Link))
            .Select(f => new MagnetFile
            {
                Name = f.Name ?? "File sconosciuto",
                Size = f.Size,
                Link = f.Link!
            })
            .ToList();
    }

    /// <summary>
    /// Sfoglio diretto di una cartella Premiumize (docs/piano-premiumize-libreria.md) — a
    /// differenza di <see cref="GetMagnetFilesDetailedAsync"/> (usato per i file di UN transfer,
    /// filtra via le sottocartelle) qui servono ANCHE le sottocartelle, per poter navigare dentro
    /// una cartella organizzata a mano dall'utente su Premiumize (es. "Serie TV" → una sottocartella
    /// per show → un'altra per stagione).
    /// </summary>
    public async Task<List<PremiumizeFolderEntry>> ListFolderAsync(string folderId, CancellationToken ct)
    {
        using var http = CreateClient();
        using var resp = await http.GetAsync(WithKey($"{Base}/folder/list?id={folderId}"), ct);
        await EnsureSuccessAsync(resp, ct);

        var payload = await resp.Content.ReadFromJsonAsync<PmFolderListResponse>(cancellationToken: ct);
        var content = payload?.Content ?? new();

        return content
            .Where(f => !string.IsNullOrWhiteSpace(f.Id))
            .Select(f => new PremiumizeFolderEntry
            {
                Id = f.Id!,
                Name = f.Name ?? "?",
                IsFolder = string.Equals(f.Type, "folder", StringComparison.OrdinalIgnoreCase),
                Size = f.Size,
                Link = f.Link
            })
            .ToList();
    }

    private async Task<MagnetFile?> GetItemDetailsAsync(string fileId, CancellationToken ct)
    {
        using var http = CreateClient();
        using var resp = await http.GetAsync(WithKey($"{Base}/item/details?id={fileId}"), ct);
        await EnsureSuccessAsync(resp, ct);

        var payload = await resp.Content.ReadFromJsonAsync<PmItemDetails>(cancellationToken: ct);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Link)) return null;

        return new MagnetFile { Name = payload.Name ?? "File sconosciuto", Size = payload.Size, Link = payload.Link };
    }

    public async Task DeleteMagnetAsync(string id, CancellationToken ct)
    {
        using var http = CreateClient();
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("id", id) });
        using var resp = await http.PostAsync(WithKey($"{Base}/transfer/delete"), content, ct);
        await EnsureSuccessAsync(resp, ct);
        _log.LogInformation("Transfer Premiumize {Id} cancellato", id);
    }

    private class PmDirectDlResponse
    {
        [JsonPropertyName("content")]
        public List<PmDirectDlItem>? Content { get; set; }
    }

    private class PmDirectDlItem
    {
        [JsonPropertyName("link")]
        public string? Link { get; set; }
    }

    private class PmCreateTransferResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private class PmTransferListResponse
    {
        [JsonPropertyName("transfers")]
        public List<PmTransfer>? Transfers { get; set; }
    }

    private class PmTransfer
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("folder_id")]
        public string? FolderId { get; set; }

        // Valorizzato quando il transfer produce UN SOLO file (es. un singolo episodio) — in quel
        // caso "folder_id" è null: verificato con un test reale, vedi commento in
        // GetMagnetFilesDetailedAsync. Un pacco multi-file (es. stagione intera) valorizza invece
        // folder_id (da confermare con un test reale su un pacco, non ancora fatto).
        [JsonPropertyName("file_id")]
        public string? FileId { get; set; }
    }

    private class PmFolderListResponse
    {
        [JsonPropertyName("content")]
        public List<PmFolderItem>? Content { get; set; }
    }

    private class PmFolderItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }
    }

    private class PmItemDetails
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }
    }
}
