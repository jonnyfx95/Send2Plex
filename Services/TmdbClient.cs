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

/// <summary>Un titolo da un rail "scoperta" (per genere o servizio di streaming) in home — a
/// differenza di <see cref="TmdbTrendingItem"/> porta il TmdbId, per aprire la pagina di dettaglio
/// direttamente al click invece di rifare una ricerca testuale per nome.</summary>
public class TmdbDiscoverItem
{
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = ""; // "tv" o "movie"
    public string Title { get; set; } = "";
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

/// <summary>Dettaglio completo di un titolo (film o serie), per la pagina di dettaglio dell'app
/// WebOS: sinossi, backdrop, generi, voto — oltre alle stagioni se è una serie. A differenza di
/// <see cref="TmdbTvDetails"/> (usata da ShowDetail.razor, solo serie) copre anche i film e in più
/// espone generi/voto, non ancora presenti nell'altra.</summary>
public class TmdbTitleDetails
{
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = ""; // "tv" o "movie"
    public string Title { get; set; } = "";
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? Year { get; set; }
    public double? VoteAverage { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<TmdbSeasonInfo> Seasons { get; set; } = new(); // vuoto per i film
    public int? RuntimeMinutes { get; set; } // durata film, o durata episodio per le serie
    public List<TmdbCastMember> Cast { get; set; } = new(); // primi ~6 attori (credits.cast)
}

/// <summary>Un membro del cast (piano-webos-skip-marker-plex.md, Fascia 3, punto 7) — prima solo il
/// nome; <see cref="PhotoUrl"/> aggiunto per il pannello informazioni episodio, stesso
/// append_to_response=credits già scaricato, nessuna nuova chiamata HTTP. Null se TMDB non ha una
/// foto per quell'attore (capita spesso, non un errore).</summary>
public class TmdbCastMember
{
    public string Name { get; set; } = "";
    public string? PhotoUrl { get; set; }
}

/// <summary>Un candidato tra i risultati di ricerca TMDB, prima che l'utente scelga quello giusto —
/// serve quando più titoli condividono lo stesso nome (es. "Supergirl" serie TV 2015 e film 2026).</summary>
public class TmdbCandidate
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public string? Year { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string MediaType { get; set; } = ""; // "tv" o "movie"

    // Euristica, non un dato certo: TMDB non ha un media_type "anime" a sé — si stima dal genere
    // "Animation" (id 16) combinato con lingua originale giapponese.
    public bool IsAnime { get; set; }
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

    // Righe "scoperta" per la home (Fase 4): stesso endpoint /discover di TMDB usato sia per i rail
    // per genere (with_genres, ID stabili e noti — niente endpoint /genre/list, un round-trip in
    // meno) sia per i rail per servizio di streaming (with_watch_providers + watch_region, ID
    // provider altrettanto stabili — Netflix=8, Prime Video=119, Disney+=337, Apple TV+=350).
    // Riusa il modello di risposta privato di GetTrendingAsync: le forme JSON di /discover e
    // /trending sono identiche per i campi che servono qui.
    public async Task<List<TmdbDiscoverItem>> DiscoverAsync(string mediaType, int? genreId, int? watchProviderId, CancellationToken ct)
    {
        if (!IsConfigured) return new List<TmdbDiscoverItem>();

        var kind = mediaType == "tv" ? "tv" : "movie";
        var url = $"https://api.themoviedb.org/3/discover/{kind}?api_key={Uri.EscapeDataString(_cfg.ApiKey)}&language=it-IT&sort_by=popularity.desc&include_adult=false";
        if (genreId is { } g) url += $"&with_genres={g}";
        if (watchProviderId is { } p) url += $"&with_watch_providers={p}&watch_region=IT";

        return await FetchDiscoverListAsync(mediaType, url, $"discover/{kind}", ct);
    }

    // Liste editoriali dedicate di TMDB (Fase 6, richiesta utente: le pagine Film/Serie TV avevano
    // solo la riga "tendenza", troppo spazio vuoto rispetto a Generi/In streaming) — stessa forma di
    // risposta JSON di /discover e /trending, qui senza filtro genere/provider. "list" è validato
    // contro un elenco fisso lato endpoint (TvApiEndpoints.cs) prima di arrivare qui, non è mai
    // testo libero dal client.
    public async Task<List<TmdbDiscoverItem>> GetListAsync(string mediaType, string list, CancellationToken ct)
    {
        if (!IsConfigured) return new List<TmdbDiscoverItem>();

        var kind = mediaType == "tv" ? "tv" : "movie";
        var url = $"https://api.themoviedb.org/3/{kind}/{list}?api_key={Uri.EscapeDataString(_cfg.ApiKey)}&language=it-IT";

        return await FetchDiscoverListAsync(mediaType, url, $"{kind}/{list}", ct);
    }

    // Corpo condiviso da DiscoverAsync e GetListAsync: stessa richiesta HTTP, stesso modello di
    // risposta (TmdbTrendingResponse), stessa mappatura verso TmdbDiscoverItem — cambia solo l'URL.
    private async Task<List<TmdbDiscoverItem>> FetchDiscoverListAsync(string mediaType, string url, string logLabel, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TmdbTrendingResponse>(cancellationToken: ct);
            if (payload?.Results is null) return new List<TmdbDiscoverItem>();

            return payload.Results
                .Where(r => r.Id != 0)
                .Select(r => new TmdbDiscoverItem
                {
                    TmdbId = r.Id,
                    MediaType = mediaType,
                    Title = (mediaType == "tv" ? r.Name : r.Title) ?? r.OriginalName ?? r.OriginalTitle ?? "?",
                    Year = (mediaType == "tv" ? r.FirstAirDate : r.ReleaseDate) is { Length: >= 4 } d ? d[..4] : null,
                    PosterUrl = r.PosterPath is not null ? $"https://image.tmdb.org/t/p/w342{r.PosterPath}" : null,
                    BackdropUrl = r.BackdropPath is not null ? $"https://image.tmdb.org/t/p/w1280{r.BackdropPath}" : null,
                    Overview = r.Overview
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore chiamando TMDB {Label}", logLabel);
            return new List<TmdbDiscoverItem>();
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

    // Come SearchPosterAsync, ma torna TUTTI i candidati (film + serie TV) invece del solo primo
    // risultato: quando più titoli si chiamano allo stesso modo (es. "Supergirl" serie 2015 e film
    // 2026), il primo risultato di TMDB (in genere il più popolare) nascondeva completamente
    // l'altro. Qui è l'utente a scegliere quale intende, dalla pagina Cerca.
    public async Task<List<TmdbCandidate>> SearchCandidatesAsync(string query, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query))
            return new List<TmdbCandidate>();

        var url = $"https://api.themoviedb.org/3/search/multi?api_key={Uri.EscapeDataString(_cfg.ApiKey)}&language=it-IT&query={Uri.EscapeDataString(query)}";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TmdbTrendingResponse>(cancellationToken: ct);
            return payload?.Results?
                .Where(r => (r.MediaType == "tv" || r.MediaType == "movie") && !string.IsNullOrWhiteSpace(r.PosterPath))
                .Select(r =>
                {
                    var title = r.MediaType == "tv" ? r.Name : r.Title;
                    var date = r.MediaType == "tv" ? r.FirstAirDate : r.ReleaseDate;
                    return new TmdbCandidate
                    {
                        TmdbId = r.Id,
                        Title = title ?? query,
                        Year = date is { Length: >= 4 } d ? d[..4] : null,
                        Overview = r.Overview,
                        // w342 (non w200): la stessa risposta alimenta anche i poster grandi
                        // dell'app WebOS (fino a ~350px di larghezza reale) — w200 vi appariva
                        // visibilmente sgranato. Un sorgente più grande va bene anche per la
                        // card 140px di Search.razor (si riduce, non si ingrandisce mai).
                        PosterUrl = $"https://image.tmdb.org/t/p/w342{r.PosterPath}",
                        MediaType = r.MediaType!,
                        IsAnime = r.OriginalLanguage == "ja" && (r.GenreIds?.Contains(16) ?? false)
                    };
                })
                .ToList() ?? new List<TmdbCandidate>();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore chiamando TMDB search/multi per \"{Query}\"", query);
            return new List<TmdbCandidate>();
        }
    }

    // Come ResolveImdbIdAsync, ma per un TMDB id/media type già noti (l'utente ha già scelto il
    // candidato giusto da SearchCandidatesAsync) invece di rifare una ricerca testuale fuzzy che
    // potrebbe risolvere un titolo diverso da quello effettivamente selezionato.
    public async Task<string?> GetImdbIdAsync(int tmdbId, string mediaType, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        var url = $"https://api.themoviedb.org/3/{mediaType}/{tmdbId}/external_ids?api_key={Uri.EscapeDataString(_cfg.ApiKey)}";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var payload = await response.Content.ReadFromJsonAsync<TmdbExternalIdsResponse>(cancellationToken: ct);
            return string.IsNullOrWhiteSpace(payload?.ImdbId) ? null : payload.ImdbId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore risolvendo IMDb ID per TMDB id {TmdbId} ({MediaType})", tmdbId, mediaType);
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

    // Dettaglio unificato film/serie (sinossi, backdrop, generi, voto, stagioni se serie) — per la
    // pagina di dettaglio titolo dell'app WebOS (docs/piano-restyle-webos-ux.md, Fase 2). Una sola
    // chiamata a /movie/{id} o /tv/{id}: TMDB include già generi e voto nella stessa risposta usata
    // altrove solo per sinossi/copertine, non serve una chiamata separata.
    public async Task<TmdbTitleDetails?> GetTitleDetailsAsync(int tmdbId, string mediaType, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        var kind = mediaType == "movie" ? "movie" : "tv";
        // append_to_response=credits: cast nella stessa risposta, niente seconda chiamata HTTP.
        var url = $"https://api.themoviedb.org/3/{kind}/{tmdbId}?api_key={Uri.EscapeDataString(_cfg.ApiKey)}&language=it-IT&append_to_response=credits";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var payload = await response.Content.ReadFromJsonAsync<TmdbTitleDetailsResponse>(cancellationToken: ct);
            if (payload is null) return null;

            var title = kind == "tv" ? payload.Name : payload.Title;
            var date = kind == "tv" ? payload.FirstAirDate : payload.ReleaseDate;

            return new TmdbTitleDetails
            {
                TmdbId = tmdbId,
                MediaType = kind,
                Title = title ?? "?",
                Overview = payload.Overview,
                PosterUrl = payload.PosterPath is not null ? $"https://image.tmdb.org/t/p/w500{payload.PosterPath}" : null,
                BackdropUrl = payload.BackdropPath is not null ? $"https://image.tmdb.org/t/p/w1280{payload.BackdropPath}" : null,
                Year = date is { Length: >= 4 } d ? d[..4] : null,
                VoteAverage = payload.VoteAverage,
                Genres = payload.Genres?.Where(g => g.Name is not null).Select(g => g.Name!).ToList() ?? new(),
                Seasons = kind == "tv"
                    ? payload.Seasons?
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
                    : new List<TmdbSeasonInfo>(),
                RuntimeMinutes = kind == "movie" ? payload.Runtime : payload.EpisodeRunTime?.FirstOrDefault(r => r > 0),
                Cast = payload.Credits?.Cast?
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Take(6)
                    .Select(c => new TmdbCastMember
                    {
                        Name = c.Name!,
                        PhotoUrl = c.ProfilePath is not null ? $"https://image.tmdb.org/t/p/w185{c.ProfilePath}" : null
                    })
                    .ToList() ?? new List<TmdbCastMember>()
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore leggendo dettagli {Kind} per TMDB id {TmdbId}", kind, tmdbId);
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

    private class TmdbTitleDetailsResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; } // tv

        [JsonPropertyName("title")]
        public string? Title { get; set; } // movie

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("backdrop_path")]
        public string? BackdropPath { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; } // tv

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; } // movie

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }

        [JsonPropertyName("genres")]
        public List<TmdbGenreRaw>? Genres { get; set; }

        [JsonPropertyName("seasons")]
        public List<TmdbSeasonRaw>? Seasons { get; set; } // solo tv, assente per i film

        [JsonPropertyName("runtime")]
        public int? Runtime { get; set; } // solo film, minuti

        [JsonPropertyName("episode_run_time")]
        public List<int>? EpisodeRunTime { get; set; } // solo tv, minuti per episodio

        [JsonPropertyName("credits")]
        public TmdbCreditsRaw? Credits { get; set; }
    }

    private class TmdbCreditsRaw
    {
        [JsonPropertyName("cast")]
        public List<TmdbCastRaw>? Cast { get; set; }
    }

    private class TmdbCastRaw
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("profile_path")]
        public string? ProfilePath { get; set; }
    }

    private class TmdbGenreRaw
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
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

        [JsonPropertyName("genre_ids")]
        public List<int>? GenreIds { get; set; }

        [JsonPropertyName("original_language")]
        public string? OriginalLanguage { get; set; }
    }
}
