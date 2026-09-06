using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

public class TmdbTrendingItem
{
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string? Year { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? Overview { get; set; }
}

public class TmdbPosterInfo
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public string? Year { get; set; }
    public string? Overview { get; set; }
    public string PosterUrl { get; set; } = "";
    public string MediaType { get; set; } = ""; // "tv" o "movie" — usato per capire in automatico Movies/TV a fine download
}

public class TmdbSeasonInfo
{
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = "";
    public int EpisodeCount { get; set; }
    public string? PosterUrl { get; set; }
}

public class TmdbTvDetails
{
    public int TmdbId { get; set; }
    public string Name { get; set; } = "";
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? Year { get; set; }
    public List<TmdbSeasonInfo> Seasons { get; set; } = new();
}

public class TmdbEpisodeInfo
{
    public int EpisodeNumber { get; set; }
    public string Name { get; set; } = "";
    public string? AirDate { get; set; }
    public string? StillUrl { get; set; }
}

public class TmdbImdbMatch
{
    public int TmdbId { get; set; }
    public string ImdbId { get; set; } = "";
    public string MediaType { get; set; } = ""; // "tv" o "movie"
    public string Title { get; set; } = "";
}

// Client minimale per TMDB (The Movie Database) v3: usato solo per proporre le serie/film di
// tendenza della settimana come spunto di ricerca (vedi TelegramWorker, comando /trending).
public class TmdbClient
{
    private readonly HttpClient _http;
    private readonly ConfigStore _configStore;
    private readonly ILogger<TmdbClient> _log;

    // Letta ad ogni chiamata: la API Key può cambiare a caldo da Impostazioni (Punto 2).
    private TmdbSettings _cfg => _configStore.Current.Tmdb;

    public TmdbClient(HttpClient http, ConfigStore configStore, ILogger<TmdbClient> log)
    {
        _http = http;
        _configStore = configStore;
        _log = log;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_cfg.ApiKey);

    // mediaType: "tv" oppure "movie"
    public async Task<List<TmdbTrendingItem>> GetTrendingAsync(string mediaType, CancellationToken ct)
    {
        if (!IsConfigured)
            return new List<TmdbTrendingItem>();

        var url = $"https://api.themoviedb.org/3/trending/{mediaType}/week?api_key={Uri.EscapeDataString(_cfg.ApiKey)}&language=it-IT";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TmdbTrendingResponse>(cancellationToken: ct);
            if (payload?.Results is null) return new List<TmdbTrendingItem>();

            return payload.Results
                .Select(r => new TmdbTrendingItem
                {
                    Title = (mediaType == "tv" ? r.Name : r.Title) ?? r.OriginalName ?? r.OriginalTitle ?? "?",
                    OriginalTitle = mediaType == "tv" ? r.OriginalName : r.OriginalTitle,
                    Year = (mediaType == "tv" ? r.FirstAirDate : r.ReleaseDate) is { Length: >= 4 } d ? d[..4] : null,
                    PosterUrl = r.PosterPath is not null ? $"https://image.tmdb.org/t/p/w342{r.PosterPath}" : null,
                    BackdropUrl = r.BackdropPath is not null ? $"https://image.tmdb.org/t/p/w1280{r.BackdropPath}" : null,
                    Overview = r.Overview
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore chiamando TMDB trending/{MediaType}", mediaType);
            return new List<TmdbTrendingItem>();
        }
    }

    // Cerca il poster del titolo su TMDB (film o serie TV, endpoint "multi" che copre entrambi).
    // Usato subito dopo che l'utente scrive la query di ricerca, per mostrare l'immagine su Telegram
    // prima ancora di scegliere il sito. Torna null se non trova nulla o non c'è un poster disponibile.
    public async Task<TmdbPosterInfo?> SearchPosterAsync(string query, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query))
            return null;

        var url = $"https://api.themoviedb.org/3/search/multi?api_key={Uri.EscapeDataString(_cfg.ApiKey)}&language=it-IT&query={Uri.EscapeDataString(query)}";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TmdbTrendingResponse>(cancellationToken: ct);
            var match = payload?.Results?.FirstOrDefault(r =>
                (r.MediaType == "tv" || r.MediaType == "movie") && !string.IsNullOrWhiteSpace(r.PosterPath));

            if (match is null) return null;

            var title = match.MediaType == "tv" ? match.Name : match.Title;
            var date = match.MediaType == "tv" ? match.FirstAirDate : match.ReleaseDate;

            return new TmdbPosterInfo
            {
                TmdbId = match.Id,
                Title = title ?? query,
                Year = date is { Length: >= 4 } d ? d[..4] : null,
                Overview = match.Overview,
                PosterUrl = $"https://image.tmdb.org/t/p/w500{match.PosterPath}",
                MediaType = match.MediaType ?? ""
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore chiamando TMDB search/multi per \"{Query}\"", query);
            return null;
        }
    }

    // Risolve un titolo testuale nel suo IMDb ID (serve a Torrentio, che vuole "tt1234567"
    // invece di una ricerca testuale libera). Fa due chiamate: search/multi per trovare il
    // titolo TMDB più rilevante, poi external_ids per ottenere l'IMDb ID collegato.
    public async Task<TmdbImdbMatch?> ResolveImdbIdAsync(string query, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query))
            return null;

        var searchUrl = $"https://api.themoviedb.org/3/search/multi?api_key={Uri.EscapeDataString(_cfg.ApiKey)}&language=it-IT&query={Uri.EscapeDataString(query)}";

        try
        {
            using var response = await _http.GetAsync(searchUrl, ct);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TmdbTrendingResponse>(cancellationToken: ct);
            var match = payload?.Results?.FirstOrDefault(r => r.MediaType == "tv" || r.MediaType == "movie");
            if (match is null) return null;

            var externalUrl = $"https://api.themoviedb.org/3/{match.MediaType}/{match.Id}/external_ids?api_key={Uri.EscapeDataString(_cfg.ApiKey)}";
            using var extResponse = await _http.GetAsync(externalUrl, ct);
            extResponse.EnsureSuccessStatusCode();

            var extPayload = await extResponse.Content.ReadFromJsonAsync<TmdbExternalIdsResponse>(cancellationToken: ct);
            if (string.IsNullOrWhiteSpace(extPayload?.ImdbId)) return null;

            var title = match.MediaType == "tv" ? match.Name : match.Title;

            return new TmdbImdbMatch
            {
                TmdbId = match.Id,
                ImdbId = extPayload.ImdbId,
                MediaType = match.MediaType!,
                Title = title ?? query
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore risolvendo IMDb ID per \"{Query}\"", query);
            return null;
        }
    }

    // Elenco delle stagioni di una serie TV (numero, nome, numero episodi, poster), per farle
    // scegliere all'utente invece di dover digitare a mano un numero di stagione.
    public async Task<List<TmdbSeasonInfo>> GetSeasonsAsync(int tmdbId, CancellationToken ct)
    {
        if (!IsConfigured) return new List<TmdbSeasonInfo>();

        var url = $"https://api.themoviedb.org/3/tv/{tmdbId}?api_key={Uri.EscapeDataString(_cfg.ApiKey)}&language=it-IT";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return new List<TmdbSeasonInfo>();

            var payload = await response.Content.ReadFromJsonAsync<TmdbTvDetailsResponse>(cancellationToken: ct);
            return payload?.Seasons?
                .Where(s => s.SeasonNumber > 0) // esclude gli "speciali" (stagione 0)
                .OrderBy(s => s.SeasonNumber)
                .Select(s => new TmdbSeasonInfo
                {
                    SeasonNumber = s.SeasonNumber,
                    Name = s.Name ?? $"Stagione {s.SeasonNumber}",
                    EpisodeCount = s.EpisodeCount,
                    PosterUrl = s.PosterPath is not null ? $"https://image.tmdb.org/t/p/w200{s.PosterPath}" : null
                })
                .ToList() ?? new List<TmdbSeasonInfo>();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore leggendo le stagioni per TMDB id {TmdbId}", tmdbId);
            return new List<TmdbSeasonInfo>();
        }
    }

    // Dettagli completi di una serie TV (titolo, sinossi, copertine, stagioni) — usati dalla
    // pagina di dettaglio serie della Libreria, per mostrare le info principali in un colpo solo.
    public async Task<TmdbTvDetails?> GetTvDetailsAsync(int tmdbId, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        var url = $"https://api.themoviedb.org/3/tv/{tmdbId}?api_key={Uri.EscapeDataString(_cfg.ApiKey)}&language=it-IT";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var payload = await response.Content.ReadFromJsonAsync<TmdbTvDetailsResponse>(cancellationToken: ct);
            if (payload is null) return null;

            return new TmdbTvDetails
            {
                TmdbId = tmdbId,
                Name = payload.Name ?? "?",
                Overview = payload.Overview,
                PosterUrl = payload.PosterPath is not null ? $"https://image.tmdb.org/t/p/w500{payload.PosterPath}" : null,
                BackdropUrl = payload.BackdropPath is not null ? $"https://image.tmdb.org/t/p/w1280{payload.BackdropPath}" : null,
                Year = payload.FirstAirDate is { Length: >= 4 } d ? d[..4] : null,
                Seasons = payload.Seasons?
                    .Where(s => s.SeasonNumber > 0)
                    .OrderBy(s => s.SeasonNumber)
                    .Select(s => new TmdbSeasonInfo
                    {
                        SeasonNumber = s.SeasonNumber,
                        Name = s.Name ?? $"Stagione {s.SeasonNumber}",
                        EpisodeCount = s.EpisodeCount,
                        PosterUrl = s.PosterPath is not null ? $"https://image.tmdb.org/t/p/w200{s.PosterPath}" : null
                    })
                    .ToList() ?? new List<TmdbSeasonInfo>()
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore leggendo dettagli TV per TMDB id {TmdbId}", tmdbId);
            return null;
        }
    }

    // Elenco episodi di una singola stagione (titolo, data di uscita, still) — usato per
    // confrontare con quanto già presente su Plex e capire quali episodi mancano.
    public async Task<List<TmdbEpisodeInfo>> GetSeasonEpisodesAsync(int tmdbId, int seasonNumber, CancellationToken ct)
    {
        if (!IsConfigured) return new List<TmdbEpisodeInfo>();

        var url = $"https://api.themoviedb.org/3/tv/{tmdbId}/season/{seasonNumber}?api_key={Uri.EscapeDataString(_cfg.ApiKey)}&language=it-IT";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return new List<TmdbEpisodeInfo>();

            var payload = await response.Content.ReadFromJsonAsync<TmdbSeasonEpisodesResponse>(cancellationToken: ct);
            return payload?.Episodes?
                .OrderBy(e => e.EpisodeNumber)
                .Select(e => new TmdbEpisodeInfo
                {
                    EpisodeNumber = e.EpisodeNumber,
                    Name = e.Name ?? $"Episodio {e.EpisodeNumber}",
                    AirDate = e.AirDate,
                    StillUrl = e.StillPath is not null ? $"https://image.tmdb.org/t/p/w300{e.StillPath}" : null
                })
                .ToList() ?? new List<TmdbEpisodeInfo>();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore leggendo episodi stagione {Season} per TMDB id {TmdbId}", seasonNumber, tmdbId);
            return new List<TmdbEpisodeInfo>();
        }
    }

    // Chiave YouTube del trailer principale (film o serie) — usata per l'embed nella pagina di
    // ricerca e nel dettaglio serie. Torna null se TMDB non ha un trailer YouTube per il titolo.
    public async Task<string?> GetTrailerAsync(int tmdbId, bool isTv, CancellationToken ct)
    {
        if (!IsConfigured || tmdbId <= 0) return null;

        var kind = isTv ? "tv" : "movie";
        var url = $"https://api.themoviedb.org/3/{kind}/{tmdbId}/videos?api_key={Uri.EscapeDataString(_cfg.ApiKey)}&language=it-IT";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var payload = await response.Content.ReadFromJsonAsync<TmdbVideosResponse>(cancellationToken: ct);
            var videos = payload?.Results ?? new List<TmdbVideoRaw>();

            // Se non c'è nulla in italiano, ripete la stessa richiesta senza vincolo di lingua
            // (i trailer originali in inglese sono meglio di nessun trailer).
            if (videos.Count == 0)
            {
                var fallbackUrl = $"https://api.themoviedb.org/3/{kind}/{tmdbId}/videos?api_key={Uri.EscapeDataString(_cfg.ApiKey)}";
                using var fallbackResponse = await _http.GetAsync(fallbackUrl, ct);
                if (fallbackResponse.IsSuccessStatusCode)
                {
                    var fallbackPayload = await fallbackResponse.Content.ReadFromJsonAsync<TmdbVideosResponse>(cancellationToken: ct);
                    videos = fallbackPayload?.Results ?? new List<TmdbVideoRaw>();
                }
            }

            var trailer = videos.FirstOrDefault(v => v.Site == "YouTube" && v.Type == "Trailer")
                       ?? videos.FirstOrDefault(v => v.Site == "YouTube");

            return trailer?.Key;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore leggendo il trailer per TMDB id {TmdbId} ({Kind})", tmdbId, kind);
            return null;
        }
    }

    private class TmdbVideosResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbVideoRaw>? Results { get; set; }
    }

    private class TmdbVideoRaw
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("site")]
        public string? Site { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    private class TmdbSeasonEpisodesResponse
    {
        [JsonPropertyName("episodes")]
        public List<TmdbEpisodeRaw>? Episodes { get; set; }
    }

    private class TmdbEpisodeRaw
    {
        [JsonPropertyName("episode_number")]
        public int EpisodeNumber { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("air_date")]
        public string? AirDate { get; set; }

        [JsonPropertyName("still_path")]
        public string? StillPath { get; set; }
    }

    private class TmdbTvDetailsResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("backdrop_path")]
        public string? BackdropPath { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("seasons")]
        public List<TmdbSeasonRaw>? Seasons { get; set; }
    }

    private class TmdbSeasonRaw
    {
        [JsonPropertyName("season_number")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("episode_count")]
        public int EpisodeCount { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }
    }

    private class TmdbExternalIdsResponse
    {
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }
    }

    private class TmdbTrendingResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbTrendingResult>? Results { get; set; }
    }

    private class TmdbTrendingResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("media_type")]
        public string? MediaType { get; set; } // "tv", "movie" o "person"

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("backdrop_path")]
        public string? BackdropPath { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; } // serie TV

        [JsonPropertyName("title")]
        public string? Title { get; set; } // film

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; set; }

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }
    }
}
