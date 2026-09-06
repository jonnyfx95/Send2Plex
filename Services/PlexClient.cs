using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

public class PlexLibraryItem
{
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? ThumbUrl { get; set; }
    public bool IsTv { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public string? RatingKey { get; set; }
}

public class PlexEpisodeItem
{
    public int Season { get; set; }
    public int Episode { get; set; }
    public string Title { get; set; } = "";
}

public class PlexSectionInfo
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Type { get; set; } = ""; // "movie" o "show"
}

public class PlexClient
{
    private readonly ILogger<PlexClient> _log;
    private readonly ConfigStore _configStore;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TmdbClient _tmdb;

    // Letta dal ConfigStore ad ogni chiamata (non nel costruttore): Impostazioni può cambiare
    // URL/Token/librerie Plex a caldo, senza richiedere un riavvio dell'app (Punto 2).
    private PlexSettings _cfg => _configStore.Current.Plex;

    public PlexClient(
        IHttpClientFactory httpFactory,
        ConfigStore configStore,
        TmdbClient tmdb,
        ILogger<PlexClient> log)
    {
        _httpFactory = httpFactory;
        _configStore = configStore;
        _tmdb = tmdb;
        _log = log;
    }

    private bool IsConfigured => !string.IsNullOrWhiteSpace(_cfg.BaseUrl) && !string.IsNullOrWhiteSpace(_cfg.Token);

    private List<int> SectionIdsFor(bool toTv) => toTv ? _cfg.TvSectionIds : _cfg.MovieSectionIds;

    /// <summary>
    /// Richiede a Plex di aggiornare le sezioni Movies o TV configurate. Se <paramref name="sectionId"/>
    /// è specificato (l'utente ha scaricato in una libreria precisa, es. "MCU"), aggiorna solo
    /// quella; altrimenti aggiorna tutte le sezioni di quel tipo. Torna true/false invece di
    /// propagare l'eccezione: un refresh fallito (es. token temporaneamente rifiutato) non deve
    /// far apparire come fallito un download che in realtà è andato a buon fine.
    /// </summary>
    public async Task<bool> RefreshAsync(bool toTv, CancellationToken ct, int? sectionId = null)
    {
        var sectionIds = sectionId is { } id && SectionIdsFor(toTv).Contains(id)
            ? new List<int> { id }
            : SectionIdsFor(toTv);
        if (sectionIds.Count == 0) return true;

        var results = await Task.WhenAll(sectionIds.Select(sid => RefreshSectionAsync(sid, toTv, ct)));
        return results.All(ok => ok);
    }

    private async Task<bool> RefreshSectionAsync(int sectionId, bool toTv, CancellationToken ct)
    {
        var url = $"{_cfg.BaseUrl}/library/sections/{sectionId}/refresh?X-Plex-Token={_cfg.Token}";
        var kind = toTv ? "TV" : "Movies";

        try
        {
            _log.LogInformation("🔄 Richiesta refresh Plex sezione {Kind} (id={Id}) → {Url}", kind, sectionId, url);

            using var http = _httpFactory.CreateClient("PLEX"); // ← CAMBIATO da "DEFAULT" a "PLEX"
            var resp = await http.GetAsync(url, ct);

            if (resp.IsSuccessStatusCode)
            {
                _log.LogInformation("✅ Plex refresh avviato per {Kind} (id={Id}, HTTP {Status})", kind, sectionId, resp.StatusCode);
                return true;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("❌ Plex refresh fallito per {Kind} (id={Id}) — Status {Status}, Body={Body}",
                kind, sectionId, resp.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore chiamando Plex refresh per sezione {Kind} (id={Id})", kind, sectionId);
            return false;
        }
    }

    /// <summary>
    /// Le sole sezioni Movies/TV che l'utente ha effettivamente selezionato in Impostazioni,
    /// separate per tipo — usato per far scegliere la libreria/cartella di destinazione al
    /// momento di un download (Search, AllDebrid, coda), quando ce n'è più di una per tipo.
    /// </summary>
    public async Task<(List<PlexSectionInfo> Movies, List<PlexSectionInfo> Tv)> GetConfiguredSectionsAsync(CancellationToken ct)
    {
        var all = await GetSectionsAsync(ct);
        var movies = all.Where(s => _cfg.MovieSectionIds.Contains(s.Id)).ToList();
        var tv = all.Where(s => _cfg.TvSectionIds.Contains(s.Id)).ToList();
        return (movies, tv);
    }

    /// <summary>
    /// Elenca tutte le sezioni della libreria Plex (film, serie, musica, ecc.) — usato dalla
    /// pagina Impostazioni per far scegliere all'utente quali sezioni includere per Movies/TV.
    /// </summary>
    public async Task<List<PlexSectionInfo>> GetSectionsAsync(CancellationToken ct)
    {
        if (!IsConfigured) return new List<PlexSectionInfo>();

        var url = $"{_cfg.BaseUrl}/library/sections?X-Plex-Token={_cfg.Token}";
        try
        {
            using var http = _httpFactory.CreateClient("PLEX");
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new List<PlexSectionInfo>();

            var payload = await resp.Content.ReadFromJsonAsync<PlexSectionsResponse>(cancellationToken: ct);
            var directories = payload?.MediaContainer?.Directory ?? new List<PlexDirectoryItem>();

            return directories
                .Where(d => int.TryParse(d.Key, out _))
                .Select(d => new PlexSectionInfo { Id = int.Parse(d.Key!), Title = d.Title ?? "?", Type = d.Type ?? "" })
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore leggendo l'elenco delle sezioni Plex");
            return new List<PlexSectionInfo>();
        }
    }

    /// <summary>
    /// Ultimi elementi aggiunti a una sezione Plex (Movies o TV) — usato per la riga "La tua
    /// libreria" della Home (preview veloce).
    /// </summary>
    public async Task<List<PlexLibraryItem>> GetRecentlyAddedAsync(bool toTv, int limit, CancellationToken ct)
    {
        if (!IsConfigured) return new List<PlexLibraryItem>();

        var metadata = await FetchMetadataAcrossSectionsAsync(toTv, "recentlyAdded", ct);
        if (metadata.Count == 0) return new List<PlexLibraryItem>();

        var items = metadata
            .OrderByDescending(m => m.AddedAt ?? 0)
            .Take(limit)
            .Select(m => ToItem(m, toTv))
            .ToList();

        await EnrichMissingPostersAsync(items, ct);
        return items;
    }

    /// <summary>
    /// Tutti gli elementi di una sezione Plex (Movies o TV), in ordine alfabetico — usato dalla
    /// pagina "Libreria" a griglia completa.
    /// </summary>
    public async Task<List<PlexLibraryItem>> GetAllAsync(bool toTv, CancellationToken ct)
    {
        if (!IsConfigured) return new List<PlexLibraryItem>();

        var metadata = await FetchMetadataAcrossSectionsAsync(toTv, "all", ct);
        if (metadata.Count == 0) return new List<PlexLibraryItem>();

        var items = metadata
            .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .Select(m => ToItem(m, toTv))
            .ToList();

        await EnrichMissingPostersAsync(items, ct);
        return items;
    }

    /// <summary>
    /// Interroga in parallelo tutte le sezioni Movies/TV configurate per lo stesso endpoint
    /// (es. "all" o "recentlyAdded") e ne unisce i risultati — più sezioni dello stesso tipo
    /// (es. Film + MCU + Star Wars) contano come un'unica libreria logica per l'app.
    /// </summary>
    private async Task<List<PlexMetadataItem>> FetchMetadataAcrossSectionsAsync(bool toTv, string endpoint, CancellationToken ct)
    {
        var sectionIds = SectionIdsFor(toTv);
        if (sectionIds.Count == 0) return new List<PlexMetadataItem>();

        var perSection = await Task.WhenAll(sectionIds.Select(id =>
        {
            var url = $"{_cfg.BaseUrl}/library/sections/{id}/{endpoint}?X-Plex-Token={_cfg.Token}";
            return FetchMetadataAsync(url, id, ct);
        }));

        return perSection.Where(m => m is not null).SelectMany(m => m!).ToList();
    }

    /// <summary>
    /// URL della sigla della serie, se Plex l'ha già scaricata e cachata tramite i suoi agenti
    /// metadata (campo "theme" nella risposta di /library/metadata/{ratingKey}). Torna null se
    /// la serie non ha una sigla cachata — capita spesso, non tutte le serie ce l'hanno.
    /// </summary>
    public async Task<string?> GetShowThemeUrlAsync(string showRatingKey, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(showRatingKey)) return null;

        var url = $"{_cfg.BaseUrl}/library/metadata/{showRatingKey}?X-Plex-Token={_cfg.Token}";
        var metadata = await FetchMetadataAsync(url, 0, ct);
        var theme = metadata?.FirstOrDefault()?.Theme;
        return theme is not null ? $"{_cfg.BaseUrl}{theme}?X-Plex-Token={_cfg.Token}" : null;
    }

    /// <summary>
    /// Tutti gli episodi effettivamente presenti su Plex per una serie (per stagione/numero
    /// episodio) — usato dalla pagina di dettaglio serie per capire quali episodi mancano.
    /// </summary>
    public async Task<List<PlexEpisodeItem>> GetOwnedEpisodesAsync(string showRatingKey, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(showRatingKey)) return new List<PlexEpisodeItem>();

        var url = $"{_cfg.BaseUrl}/library/metadata/{showRatingKey}/allLeaves?X-Plex-Token={_cfg.Token}";
        var metadata = await FetchMetadataAsync(url, 0, ct);
        if (metadata is null) return new List<PlexEpisodeItem>();

        return metadata
            .Where(m => m.ParentIndex is not null && m.Index is not null)
            .Select(m => new PlexEpisodeItem { Season = m.ParentIndex!.Value, Episode = m.Index!.Value, Title = m.Title ?? "" })
            .ToList();
    }

    private async Task<List<PlexMetadataItem>?> FetchMetadataAsync(string url, int sectionId, CancellationToken ct)
    {
        try
        {
            using var http = _httpFactory.CreateClient("PLEX");
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var payload = await resp.Content.ReadFromJsonAsync<PlexRecentlyAddedResponse>(cancellationToken: ct);
            return payload?.MediaContainer?.Metadata;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore leggendo la libreria Plex (sezione {Id})", sectionId);
            return null;
        }
    }

    private PlexLibraryItem ToItem(PlexMetadataItem m, bool toTv) => new()
    {
        Title = m.Title ?? "?",
        Year = m.Year,
        ThumbUrl = m.Thumb is not null ? $"{_cfg.BaseUrl}{m.Thumb}?X-Plex-Token={_cfg.Token}" : null,
        IsTv = toTv,
        AddedAt = m.AddedAt is { } epoch ? DateTimeOffset.FromUnixTimeSeconds(epoch) : DateTimeOffset.MinValue,
        RatingKey = m.RatingKey
    };

    // Per i titoli che Plex non ha ancora abbinato a un poster (appena scaricati, nome file
    // grezzo): prova a recuperare una copertina vera cercando su TMDB il titolo ripulito.
    private async Task EnrichMissingPostersAsync(List<PlexLibraryItem> items, CancellationToken ct)
    {
        if (!_tmdb.IsConfigured) return;

        var missing = items.Where(i => i.ThumbUrl is null).ToList();
        if (missing.Count == 0) return;

        using var throttle = new SemaphoreSlim(4);
        await Task.WhenAll(missing.Select(async item =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var guess = TitleCleaner.Clean(item.Title);
                if (string.IsNullOrWhiteSpace(guess)) return;

                var match = await _tmdb.SearchPosterAsync(guess, ct);
                if (match is null) return;

                item.ThumbUrl = match.PosterUrl;
                item.Title = match.Title;
                if (match.Year is { Length: >= 4 } y && int.TryParse(y, out var yr))
                    item.Year = yr;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "⚠️ Errore recuperando copertina TMDB per \"{Title}\"", item.Title);
            }
            finally
            {
                throttle.Release();
            }
        }));
    }

    private class PlexRecentlyAddedResponse
    {
        [JsonPropertyName("MediaContainer")]
        public PlexMediaContainer? MediaContainer { get; set; }
    }

    private class PlexMediaContainer
    {
        [JsonPropertyName("Metadata")]
        public List<PlexMetadataItem>? Metadata { get; set; }
    }

    private class PlexSectionsResponse
    {
        [JsonPropertyName("MediaContainer")]
        public PlexSectionsContainer? MediaContainer { get; set; }
    }

    private class PlexSectionsContainer
    {
        [JsonPropertyName("Directory")]
        public List<PlexDirectoryItem>? Directory { get; set; }
    }

    private class PlexDirectoryItem
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    private class PlexMetadataItem
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }

        [JsonPropertyName("thumb")]
        public string? Thumb { get; set; }

        [JsonPropertyName("addedAt")]
        public long? AddedAt { get; set; }

        [JsonPropertyName("ratingKey")]
        public string? RatingKey { get; set; }

        [JsonPropertyName("parentIndex")]
        public int? ParentIndex { get; set; } // numero stagione (endpoint allLeaves)

        [JsonPropertyName("index")]
        public int? Index { get; set; } // numero episodio (endpoint allLeaves)

        [JsonPropertyName("theme")]
        public string? Theme { get; set; } // sigla cachata da Plex, se presente
    }
}
