using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

/// <summary>
/// API JSON minima per l'app nativa WebOS (docs/piano-webos-app.md). Espone in sola lettura ciò
/// che serve per navigare i magnet già pronti su AllDebrid, cercare nuovi titoli (TMDB + Torrentio,
/// stesso motore di default di Search.razor) e guardarli in streaming (endpoint `/stream`,
/// invariato) — nessuna nuova logica di business, solo proiezioni JSON degli stessi servizi già
/// usati da Search.razor/AllDebrid.razor.
/// </summary>
public static class TvApiEndpoints
{
    // Elenco chiuso di liste editoriali TMDB richiedibili da /tmdb-list — "list" arriva dal client
    // e finisce interpolato direttamente nell'URL di TMDB (TmdbClient.GetListAsync), quindi va
    // validato qui contro un insieme fisso invece di essere testo libero.
    private static readonly HashSet<string> AllowedTmdbLists = new(StringComparer.OrdinalIgnoreCase)
    {
        "popular", "top_rated", "now_playing", "upcoming", "on_the_air", "airing_today"
    };

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/tv");

        // Usato dalla schermata di configurazione dell'app WebOS per verificare che l'IP/porta
        // inseriti raggiungano davvero questo backend, prima di salvarli.
        group.MapGet("/ping", () => Results.Json(new { ok = true, name = "Send2Plex" }));

        // Interrogato dal player PRIMA di avviare lo streaming vero: dice se il video verrà
        // ricodificato (nel qual caso il player userà Media Source Extensions, con lo string
        // codec fisso restituito qui) oppure copiato così com'è (nel qual caso resta un <video
        // src> semplice, perché il codec originale non è prevedibile in anticipo — vedi
        // StreamingService.GetStreamInfoAsync).
        group.MapGet("/stream-info", async (string link, string? mode, string? quality, bool forceEncode, string? provider, int audioIndex, StreamingService streaming, CancellationToken ct) =>
        {
            var isLocal = string.Equals(provider, "local", StringComparison.OrdinalIgnoreCase);
            var info = await streaming.GetStreamInfoAsync(link, mode ?? "copy", quality ?? "alta", forceEncode, ParseProvider(provider), isLocal, audioIndex, ct);
            if (info.Error is not null)
                return Results.Json(new { error = info.Error }, statusCode: 502);
            return Results.Json(new { videoEncoded = info.VideoEncoded, mimeCodecs = info.MimeCodecs, durationSeconds = info.DurationSeconds, directFile = info.DirectFile });
        });

        // Elenco tracce audio del file (restyle player 2026-09-14, selettore traccia audio) —
        // chiamato dal player IN PARALLELO all'avvio della riproduzione, mai a bloccarla: sonda il
        // file con ffprobe (stesso probe che /stream farà comunque per la sessione vera), la
        // riproduzione parte già sulla traccia 0 di default mentre questa risposta arriva. Un
        // fallimento (link irraggiungibile, probe scaduto) torna un elenco vuoto, mai un errore
        // fatale: il player si limita a non mostrare il selettore.
        group.MapGet("/audio-tracks", async (string link, string? provider, StreamingService streaming, CancellationToken ct) =>
        {
            var isLocal = string.Equals(provider, "local", StringComparison.OrdinalIgnoreCase);
            var tracks = await streaming.GetAudioTracksAsync(link, ParseProvider(provider), isLocal, ct);
            return Results.Json(tracks.Select(t => new { index = t.Index, language = t.Language, codecName = t.CodecName, channels = t.Channels, title = t.Title }));
        });

        group.MapGet("/magnets", async (string? provider, DebridProviderFactory debridFactory, CancellationToken ct) =>
        {
            // Un errore del provider (chiave non valida, manutenzione, ecc.) non deve mai arrivare
            // al client come pagina HTML dell'exception handler di produzione — il client (webOS
            // compreso) fa sempre res.json() sulla risposta, che altrimenti fallisce con un errore
            // di parsing invece di mostrare il vero motivo.
            try
            {
                var magnets = await debridFactory.Get(ParseProvider(provider)).GetMagnetsAsync(ct);
                return Results.Json(magnets
                    .OrderByDescending(m => m.UploadDate)
                    .Select(m => new
                    {
                        id = m.Id,
                        filename = m.Filename,
                        size = m.Size,
                        status = m.Status,
                        isReady = m.IsReady,
                        uploadDate = m.UploadDate
                    }));
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        group.MapGet("/magnets/{id}/files", async (string id, string? provider, DebridProviderFactory debridFactory, CancellationToken ct) =>
        {
            try
            {
                var files = await debridFactory.Get(ParseProvider(provider)).GetMagnetFilesDetailedAsync(id, ct);
                return Results.Json(files.Select(f => new
                {
                    name = f.Name,
                    size = f.Size,
                    link = f.Link
                }));
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        // ------------------------------------------------------------
        // Ricerca (TMDB per il titolo/poster, Torrentio per i torrent) — stesso motore di default
        // già usato da Search.razor, qui senza il selettore multi-sito: Torrentio non richiede
        // digitare altro dopo il titolo (nessuna VPN, nessuna pagina di dettaglio da risolvere),
        // il che lo rende l'unico ragionevole da telecomando.
        // ------------------------------------------------------------

        // Home: due "scaffali" di tendenza (stessa fonte di Home.razor). TmdbTrendingItem non porta
        // un tmdbId, quindi il click su un poster in home riusa il flusso di ricerca testuale
        // (stessa scelta già fatta da Home.razor con "Cerca questo titolo"), non un accesso diretto.
        group.MapGet("/trending", async (string mediaType, TmdbClient tmdb, CancellationToken ct) =>
        {
            var items = await tmdb.GetTrendingAsync(mediaType, ct);
            return Results.Json(items.Take(12).Select(i => new
            {
                title = i.Title,
                year = i.Year,
                posterUrl = i.PosterUrl,
                overview = i.Overview
            }));
        });

        // Rail "scoperta" per la nuova home (Fase 4, piano-restyle-webos-ux.md): per genere o per
        // servizio di streaming, stessa forma di risposta di /trending ma CON tmdbId (qui il click
        // apre subito la pagina di dettaglio, niente ricerca testuale come per i trending).
        group.MapGet("/discover", async (string mediaType, int? genreId, int? providerId, TmdbClient tmdb, CancellationToken ct) =>
        {
            var items = await tmdb.DiscoverAsync(mediaType, genreId, providerId, ct);
            return Results.Json(items.Take(15).Select(i => new
            {
                tmdbId = i.TmdbId,
                mediaType = i.MediaType,
                title = i.Title,
                year = i.Year,
                posterUrl = i.PosterUrl,
                backdropUrl = i.BackdropUrl,
                overview = i.Overview
            }));
        });

        // Liste editoriali dedicate di TMDB (Fase 6, piano-restyle-webos-ux.md): le pagine
        // Categoria Film/Serie TV avevano solo la riga "tendenza", troppo spazio vuoto rispetto a
        // Generi/In streaming. "list" è validato contro un elenco fisso qui — mai passato come
        // testo libero a TmdbClient, che lo interpola direttamente nell'URL di TMDB.
        group.MapGet("/tmdb-list", async (string mediaType, string list, TmdbClient tmdb, CancellationToken ct) =>
        {
            if (!AllowedTmdbLists.Contains(list))
                return Results.Json(new { error = "Lista non valida." }, statusCode: 400);

            var items = await tmdb.GetListAsync(mediaType, list, ct);
            return Results.Json(items.Take(15).Select(i => new
            {
                tmdbId = i.TmdbId,
                mediaType = i.MediaType,
                title = i.Title,
                year = i.Year,
                posterUrl = i.PosterUrl,
                backdropUrl = i.BackdropUrl,
                overview = i.Overview
            }));
        });

        group.MapGet("/search/candidates", async (string q, TmdbClient tmdb, CancellationToken ct) =>
        {
            if (!tmdb.IsConfigured)
                return Results.Json(new { error = "TMDB non configurato (Impostazioni sul PC)." }, statusCode: 400);

            var candidates = await tmdb.SearchCandidatesAsync(q, ct);
            return Results.Json(candidates.Select(c => new
            {
                tmdbId = c.TmdbId,
                title = c.Title,
                year = c.Year,
                overview = c.Overview,
                posterUrl = c.PosterUrl,
                mediaType = c.MediaType,
                isAnime = c.IsAnime
            }));
        });

        // Pagina di dettaglio titolo (Fase 2, docs/piano-restyle-webos-ux.md): sinossi, backdrop,
        // generi, voto, più le stagioni se è una serie — sostituisce il vecchio /search/seasons
        // (la scelta stagione è ora integrata in questa stessa schermata, non più una view a sé).
        group.MapGet("/search/detail", async (int tmdbId, string mediaType, TmdbClient tmdb, OmdbClient omdb, CancellationToken ct) =>
        {
            var detail = await tmdb.GetTitleDetailsAsync(tmdbId, mediaType, ct);
            if (detail is null)
                return Results.Json(new { error = "Impossibile leggere i dettagli da TMDB." }, statusCode: 502);

            // Voto IMDb vero (OMDb), distinto dal vote_average di TMDB già in "detail" — solo se
            // l'utente ha configurato una API key OMDb; altrimenti si mostra solo il voto TMDB,
            // nessun errore per l'utente (funzionalità opzionale, non blocca il resto della pagina).
            double? imdbRating = null;
            int? imdbVotes = null;
            if (omdb.IsConfigured)
            {
                var imdbId = await tmdb.GetImdbIdAsync(tmdbId, mediaType, ct);
                if (!string.IsNullOrWhiteSpace(imdbId))
                {
                    var rating = await omdb.GetRatingAsync(imdbId, ct);
                    imdbRating = rating?.ImdbRating;
                    imdbVotes = rating?.ImdbVotes;
                }
            }

            return Results.Json(new
            {
                tmdbId = detail.TmdbId,
                mediaType = detail.MediaType,
                title = detail.Title,
                overview = detail.Overview,
                posterUrl = detail.PosterUrl,
                backdropUrl = detail.BackdropUrl,
                year = detail.Year,
                voteAverage = detail.VoteAverage,
                imdbRating,
                imdbVotes,
                genres = detail.Genres,
                runtimeMinutes = detail.RuntimeMinutes,
                cast = detail.Cast,
                seasons = detail.Seasons.Select(s => new
                {
                    seasonNumber = s.SeasonNumber,
                    name = s.Name,
                    episodeCount = s.EpisodeCount
                })
            });
        });

        group.MapGet("/search/results", async (
            int tmdbId, string mediaType, int? season, int? episodeCount, int? episode,
            TmdbClient tmdb, TorrentSearchService torrentSearch, ConfigStore configStore, CancellationToken ct) =>
        {
            var site = configStore.Current.TorrentSearch.Sites
                .FirstOrDefault(s => s.Enabled && s.UseTorrentioApi);
            if (site is null)
                return Results.Json(new { error = "Nessun sito Torrentio abilitato nelle Impostazioni sul PC." }, statusCode: 400);

            var imdbId = await tmdb.GetImdbIdAsync(tmdbId, mediaType, ct);
            if (string.IsNullOrWhiteSpace(imdbId))
                return Results.Json(new { error = "Impossibile risolvere l'IMDb ID per questo titolo." }, statusCode: 404);

            var maxResults = configStore.Current.TorrentSearch.MaxResultsPerSite;
            List<TorrentSearchResult> results;

            if (mediaType == "tv" && season is { } s2 && episode is { } singleEpisode)
            {
                // Un solo episodio mirato (es. "prossimo episodio" dalla pagina di dettaglio): non
                // ha senso cercare 1..N quando serve solo l'episodio N.
                results = await torrentSearch.SearchTorrentioApiAsync(site, imdbId, mediaType, s2, singleEpisode, maxResults, ct);
            }
            else if (mediaType == "tv" && season is { } s3)
            {
                // Torrentio non filtra per stagione da sola (serve stagione+episodio insieme):
                // stesso schema di Search.razor, un episodio alla volta con un piccolo throttle
                // (Torrentio blocca le richieste che arrivano tutte insieme come raffica).
                var count = Math.Max(1, episodeCount ?? 1);
                using var throttle = new SemaphoreSlim(3);
                var perEpisode = await Task.WhenAll(Enumerable.Range(1, count).Select(async ep =>
                {
                    await throttle.WaitAsync(ct);
                    try { return await torrentSearch.SearchTorrentioApiAsync(site, imdbId, mediaType, s3, ep, maxResults, ct); }
                    finally { throttle.Release(); }
                }));
                results = perEpisode.SelectMany(r => r).ToList();
            }
            else
            {
                results = await torrentSearch.SearchTorrentioApiAsync(site, imdbId, mediaType, null, null, maxResults, ct);
            }

            return Results.Json(results.Select(r => new
            {
                title = r.Title,
                sizeText = r.SizeText,
                sizeBytes = ParseSizeBytes(r.SizeText),
                seedsText = r.SeedsText,
                episode = ParseEpisode(r.Title),
                resolution = ParseResolution(r.Title),
                language = ParseLanguage(r.Title),
                magnet = r.Magnet,
                siteName = r.SiteName
            }));
        });

        group.MapPost("/prepare", async (PrepareRequest req, DownloadOrchestrator orchestrator, DebridProviderFactory debridFactory, ConfigStore configStore, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Magnet))
                return Results.Json(new { error = "Magnet mancante." }, statusCode: 400);

            var provider = ParseProvider(req.Provider);
            var result = new TorrentSearchResult { Title = req.Title, Magnet = req.Magnet, SiteName = req.SiteName ?? "Torrentio" };
            var timeoutSeconds = configStore.Current.TorrentSearch.TimeoutSeconds;

            try
            {
                var prep = await orchestrator.PrepareSearchResultAsync(result, requiresVpn: false, toTv: false, timeoutSeconds, ct, provider: provider);
                if (!prep.Success)
                    return Results.Json(new { error = prep.Message }, statusCode: 502);

                var files = await debridFactory.Get(provider).GetMagnetFilesDetailedAsync(prep.MagnetId!, ct);
                return Results.Json(new
                {
                    magnetId = prep.MagnetId,
                    files = files.Select(f => new { name = f.Name, size = f.Size, link = f.Link })
                });
            }
            catch (Exception ex)
            {
                // Es. Real-Debrid rifiuta alcuni magnet con "infringing_file" (filtro contenuti,
                // diverso da AllDebrid che non lo applica) — un errore legittimo del provider, non
                // un crash: va mostrato come messaggio leggibile, non come pagina HTML che rompe il
                // res.json() lato client (webOS compreso).
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        // "📚 Acquisisci in libreria" (docs/piano-premiumize-libreria.md, solo Premiumize): il
        // client ha già chiamato /prepare (magnet caricato e pronto su Premiumize) e passa qui il
        // magnetId ottenuto da lì — a differenza di /download, non scarica nulla e non passa dalla
        // coda: registrare il tracking + chiedere il refresh Plex è un'operazione rapida, niente
        // fire-and-forget necessario.
        group.MapPost("/acquire", async (AcquireRequest req, DownloadOrchestrator orchestrator, ConfigStore configStore, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.MagnetId) || req.TmdbId <= 0)
                return Results.Json(new { error = "magnetId/tmdbId mancanti." }, statusCode: 400);

            // webOS non manda mai un sectionId esplicito (nessun selettore libreria a telecomando,
            // vedi LibrarySettings): usa il default configurato una volta sola sul PC per il tipo
            // (film/serie) richiesto.
            var lib = configStore.Current.Library;
            var sectionId = req.SectionId ?? (req.ToTv ? lib.DefaultTvSectionId : lib.DefaultMovieSectionId);
            if (sectionId is null)
                return Results.Json(new { error = "Nessuna sezione libreria Premiumize di default configurata nelle Impostazioni sul PC." }, statusCode: 400);

            try
            {
                var result = await orchestrator.AcquireReadyMagnetToLibraryAsync(
                    req.MagnetId, req.Title ?? "file", req.ToTv, req.TmdbId, sectionId.Value, ct);

                return result.Success
                    ? Results.Json(new { ok = true, message = result.Message })
                    : Results.Json(new { error = result.Message }, statusCode: 502);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        // "Scarica su Plex" dal pulsante sulla card episodio/film webOS (richiesta utente: MAI dal
        // player) — stesso identico shape di un elemento accodato da Search.razor ("➕ Coda" su un
        // risultato torrent, non ancora caricato su un provider): qui il client ha solo
        // title/magnet/siteName (il miglior risultato Torrentio per quel film/episodio, scelto
        // automaticamente con la stessa logica di pickPreferredResult usata per l'autoplay),
        // MAI un magnetId già risolto. DownloadOrchestrator.DownloadFromSearchResultAsync fa da sé
        // tutta la pipeline (upload magnet → wait-ready → sblocco → salvataggio in Paths.Movies/Tv
        // → refresh Plex → cronologia), tramite la coda esistente.
        //
        // Accodato e avviato subito ma SENZA attendere il trasferimento in questa richiesta (fire-
        // and-forget su CancellationToken.None, non sul token della richiesta HTTP): un file da
        // decine di GB richiederebbe una fetch() aperta per minuti sulla TV, e soprattutto il
        // download deve proseguire indipendentemente da cosa fa l'utente dopo — naviga altrove,
        // chiude l'app, spegne la TV — esattamente come già succede lato web (Queue.razor). Il
        // progresso si segue da /api/tv/downloads (nuova sezione dedicata webOS), non da qui.
        group.MapPost("/download", (DownloadRequest req, DownloadQueueService queue) =>
        {
            if (string.IsNullOrWhiteSpace(req.Magnet))
                return Results.Json(new { error = "Magnet mancante." }, statusCode: 400);

            // BUG REALE (file corrotti, vedi Downloader.SaveOneAsync): un doppio tap sul pulsante
            // "Scarica" (mancava un riscontro visivo immediato) accodava due volte lo stesso
            // magnet — reso innocuo dal lock per-percorso in Downloader, ma restava comunque uno
            // spreco di banda/tempo. Se lo stesso magnet è già in coda o in corso, non se ne
            // accoda un secondo.
            if (queue.Items.Any(i => i.SearchResult?.Magnet == req.Magnet &&
                (i.Status == QueuedDownloadStatus.Queued || i.Status == QueuedDownloadStatus.Downloading)))
            {
                return Results.Json(new { ok = true, alreadyQueued = true });
            }

            var provider = ParseProvider(req.Provider);
            queue.Enqueue(new QueuedDownloadItem
            {
                Title = req.Title ?? "file",
                SourceName = req.SiteName ?? provider.ToDisplayName(),
                ToTv = req.ToTv,
                Provider = provider,
                SearchResult = new TorrentSearchResult { Title = req.Title ?? "file", Magnet = req.Magnet, SiteName = req.SiteName ?? "Torrentio" },
                TmdbId = req.TmdbId,
                Season = req.Season,
                Episode = req.Episode
            });

            _ = queue.StartAllAsync(CancellationToken.None);

            return Results.Json(new { ok = true });
        });

        // Sezione "Download" dedicata webOS (richiesta utente): stato della stessa coda usata dalla
        // pagina Queue.razor sul PC, in sola lettura + azioni minime (rimuovi/ferma) — nessuna
        // logica nuova, solo una proiezione JSON di DownloadQueueService.Items.
        group.MapGet("/downloads", (int? tmdbId, DownloadQueueService queue) =>
        {
            var items = queue.Items.AsEnumerable();
            if (tmdbId is { } id) items = items.Where(i => i.TmdbId == id);

            return Results.Json(items.Select(i => new
            {
                id = i.Id,
                title = i.Title,
                sourceName = i.SourceName,
                toTv = i.ToTv,
                status = i.Status.ToString(),
                message = i.Message,
                percent = i.Percent,
                speed = i.Speed,
                eta = i.Eta,
                currentFileName = i.CurrentFileName,
                currentFileIndex = i.CurrentFileIndex,
                totalFiles = i.TotalFiles,
                tmdbId = i.TmdbId,
                season = i.Season,
                episode = i.Episode
            }));
        });

        group.MapDelete("/downloads/{id:guid}", (Guid id, DownloadQueueService queue) =>
        {
            queue.Remove(id);
            return Results.Json(new { ok = true });
        });

        group.MapPost("/downloads/{id:guid}/stop", (Guid id, DownloadQueueService queue) =>
        {
            queue.Stop(id);
            return Results.Json(new { ok = true });
        });

        // Badge "già presente" (stessa logica di CheckOwnershipAsync in Search.razor: match per
        // titolo normalizzato contro la libreria Plex, niente TMDB id salvato lato Plex) + verifica
        // sul FILE LOCALE (cartelle Movies/TV già note a Downloader) per sapere se offrire anche lo
        // streaming diretto da disco invece che dal provider debrid.
        group.MapGet("/library/status", async (string mediaType, string title, int? season, int? episode, PlexClient plex, Downloader downloader, CancellationToken ct) =>
        {
            bool inPlexLibrary;
            string? localFilePath;

            if (string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) && season is { } s && episode is { } e)
            {
                var libShows = await plex.GetAllAsync(toTv: true, ct);
                var show = libShows.FirstOrDefault(sh => TitlesMatch(sh.Title, title));
                inPlexLibrary = !string.IsNullOrWhiteSpace(show?.RatingKey) &&
                    (await plex.GetOwnedEpisodesAsync(show!.RatingKey!, ct)).Any(o => o.Season == s && o.Episode == e);
                localFilePath = downloader.FindLocalEpisodeFile(title, s, e);
            }
            else
            {
                var libMovies = await plex.GetAllAsync(toTv: false, ct);
                inPlexLibrary = libMovies.Any(m => TitlesMatch(m.Title, title));
                localFilePath = downloader.FindLocalMovieFile(title);
            }

            return Results.Json(new { inPlexLibrary, hasLocalFile = localFilePath is not null, localFilePath });
        });

        // Marker sigla/titoli di coda rilevati da Plex Pass (piano-webos-skip-marker-plex.md,
        // Passo 0) — STESSA identica logica di risoluzione titolo→ratingKey di /library/status
        // sopra (fino a trovare il ratingKey giusto), poi GetMarkersAsync sul ratingKey trovato.
        // null per intro/credits (non l'assenza del campo) quando Plex non ha marker per questo
        // file — nessun Plex Pass, file non ancora processato, o titolo non in libreria: nessuno di
        // questi casi è un errore, il client li tratta tutti allo stesso modo (nessun marker → i
        // pulsanti dipendenti restano nascosti/il fallback +30s prende il loro posto).
        group.MapGet("/library/markers", async (string mediaType, string title, int? season, int? episode, PlexClient plex, CancellationToken ct) =>
        {
            string? ratingKey = null;

            if (string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) && season is { } s && episode is { } e)
            {
                var libShows = await plex.GetAllAsync(toTv: true, ct);
                var show = libShows.FirstOrDefault(sh => TitlesMatch(sh.Title, title));
                if (!string.IsNullOrWhiteSpace(show?.RatingKey))
                {
                    var owned = await plex.GetOwnedEpisodesAsync(show!.RatingKey!, ct);
                    ratingKey = owned.FirstOrDefault(o => o.Season == s && o.Episode == e)?.RatingKey;
                }
            }
            else
            {
                var libMovies = await plex.GetAllAsync(toTv: false, ct);
                ratingKey = libMovies.FirstOrDefault(m => TitlesMatch(m.Title, title))?.RatingKey;
            }

            if (string.IsNullOrWhiteSpace(ratingKey))
                return Results.Json(new { intro = (object?)null, credits = (object?)null });

            var markers = await plex.GetMarkersAsync(ratingKey, ct);
            object? ToDto(string type)
            {
                var m = markers.FirstOrDefault(mk => mk.Type == type);
                return m is null ? null : new { startSeconds = m.StartSeconds, endSeconds = m.EndSeconds };
            }

            return Results.Json(new { intro = ToDto("intro"), credits = ToDto("credits") });
        });

        // Elenco stagione/episodio già presenti su Plex per una serie (bulk, una chiamata sola
        // invece di una per episodio) — usato dalla lista episodi webOS per il badge "💾 già
        // presente", stesso principio di /history/show per il badge "✓ Visto".
        group.MapGet("/library/episodes", async (string title, PlexClient plex, Downloader downloader, CancellationToken ct) =>
        {
            // Unione di due fonti, non solo Plex: un refresh Plex fallito (bug pre-esistente,
            // capita spesso in pratica — vedi i messaggi "⚠️ Refresh Plex non riuscito" nella
            // cronologia download) lascerebbe altrimenti un episodio già scaricato segnato come
            // "non in libreria" nel badge, in contraddizione con "💾 Guarda da disco" (che invece
            // guarda il disco direttamente) mostrato aprendone la versione.
            var libShows = await plex.GetAllAsync(toTv: true, ct);
            var show = libShows.FirstOrDefault(sh => TitlesMatch(sh.Title, title));
            var plexOwned = !string.IsNullOrWhiteSpace(show?.RatingKey)
                ? (await plex.GetOwnedEpisodesAsync(show!.RatingKey!, ct)).Select(o => (o.Season, o.Episode))
                : Enumerable.Empty<(int, int)>();

            var localOwned = downloader.FindLocalEpisodes(title);

            var combined = plexOwned.Concat(localOwned).Distinct()
                .Select(t => new { season = t.Item1, episode = t.Item2 });
            return Results.Json(combined);
        });

        // Sfoglio bulk della libreria Plex (nuova vista webOS "Libreria", equivalente di
        // Library.razor sul web, docs/piano-premiumize-libreria.md) — a differenza di
        // /library/status (un titolo alla volta, per il badge durante una ricerca) qui serve
        // l'intera griglia in un colpo solo. Il file locale/streaming diretto si applica solo ai
        // FILM (un film = un file): per le serie il client naviga comunque al dettaglio show per
        // scegliere l'episodio, come già fa oggi — hasLocalFile/localFilePath restano quindi
        // sempre assenti per mediaType=tv, non è un'omissione.
        group.MapGet("/library/all", async (string mediaType, PlexClient plex, Downloader downloader, LibraryAcquisitionService acquisitions, CancellationToken ct) =>
        {
            var toTv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
            var items = await plex.GetAllAsync(toTv, ct);

            var result = items.Select(i => new
            {
                title = i.Title,
                year = i.Year,
                thumbUrl = i.ThumbUrl,
                ratingKey = i.RatingKey,
                addedAt = i.AddedAt,
                genres = i.Genres,
                hasLocalFile = !toTv && downloader.FindLocalMovieFile(i.Title) is not null,
                localFilePath = toTv ? null : downloader.FindLocalMovieFile(i.Title),
                isPremiumize = acquisitions.FindByTitle(i.Title, toTv) is not null
            });

            return Results.Json(result);
        });

        // Risolve un titolo Plex (senza TmdbId, vedi PlexLibraryItem) al suo candidato TMDB, per
        // aprire dalla griglia Libreria webOS la VERA pagina di dettaglio (poster/backdrop/generi/
        // cast/stagioni) invece di rifare una ricerca torrent — richiesta utente, "al click sul
        // titolo non deve partire la ricerca su torrentio, ma direttamente pagina del film o della
        // serie". Chiamato al click, non in bulk su /library/all: centinaia di ricerche TMDB ad
        // ogni apertura della griglia sarebbero troppo lente, una sola al momento del click è
        // rapida e non richiede alcuna cache.
        group.MapGet("/library/resolve", async (string title, string mediaType, TmdbClient tmdb, CancellationToken ct) =>
        {
            var candidates = await tmdb.SearchCandidatesAsync(title, ct);
            var sameType = candidates.Where(c => c.MediaType == mediaType).ToList();
            var match = sameType.FirstOrDefault(c => TitlesMatch(c.Title, title)) ?? sameType.FirstOrDefault();

            if (match is null)
                return Results.Json(new { found = false });

            return Results.Json(new
            {
                found = true,
                tmdbId = match.TmdbId,
                mediaType = match.MediaType,
                title = match.Title,
                year = match.Year,
                posterUrl = match.PosterUrl,
                overview = match.Overview
            });
        });

        // Sfoglio diretto delle cartelle Premiumize organizzate a mano dall'utente (richiesta
        // utente 2026-09-13: alternativa più semplice al mount WebDAV, funziona subito senza
        // configurazione infrastrutturale). Senza folderId, parte dalla cartella radice configurata
        // per il tipo (film/serie); con folderId, naviga dentro una sottocartella già vista in un
        // browse precedente (drill-down, es. show → stagione).
        group.MapGet("/library/premiumize/browse", async (string type, string? folderId, PremiumizeClient premiumize, ConfigStore configStore, CancellationToken ct) =>
        {
            if (!premiumize.IsConfigured)
                return Results.Json(new { error = "Premiumize non configurato nelle Impostazioni." }, statusCode: 400);

            var lib = configStore.Current.Library;
            var targetId = folderId ?? (string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase) ? lib.PremiumizeTvFolderId : lib.PremiumizeMovieFolderId);
            if (string.IsNullOrWhiteSpace(targetId))
                return Results.Json(new { error = $"Nessuna cartella Premiumize configurata per \"{type}\" nelle Impostazioni." }, statusCode: 400);

            try
            {
                var entries = await premiumize.ListFolderAsync(targetId, ct);
                var ordered = entries
                    .OrderByDescending(e => e.IsFolder)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new { id = e.Id, name = e.Name, isFolder = e.IsFolder, size = e.Size, link = e.Link });

                return Results.Json(ordered);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        // Tab "Premiumize" della vista Libreria webOS (equivalente della tabella in Library.razor)
        // — elenco delle acquisizioni tracciate, con sezione risolta e stato di visione leggibile.
        group.MapGet("/library/acquisitions", async (LibraryAcquisitionService acquisitions, WatchHistoryService watchHistory, PlexClient plex, CancellationToken ct) =>
        {
            var (movieSections, tvSections) = await plex.GetConfiguredSectionsAsync(ct);
            var allSections = movieSections.Concat(tvSections).ToList();

            var rows = acquisitions.GetAll()
                .OrderByDescending(a => a.AcquiredAt)
                .Select(a => new
                {
                    transferId = a.TransferId,
                    title = a.Title,
                    toTv = a.ToTv,
                    sectionTitle = allSections.FirstOrDefault(s => s.Id == a.SectionId)?.Title ?? $"(sezione {a.SectionId})",
                    acquiredAt = a.AcquiredAt,
                    watchStatus = LibraryAcquisitionService.DescribeWatchStatus(a, watchHistory)
                });

            return Results.Json(rows);
        });

        group.MapPost("/library/acquisitions/remove", async (RemoveAcquisitionRequest req, LibraryAcquisitionService acquisitions, DownloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            var acquisition = acquisitions.Get(req.TransferId);
            if (acquisition is null)
                return Results.Json(new { error = "Acquisizione non trovata (già rimossa?)." }, statusCode: 404);

            try
            {
                await orchestrator.RemoveLibraryAcquisitionAsync(acquisition, ct);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        // ------------------------------------------------------------
        // Cronologia di visione (Fase 3, docs/piano-restyle-webos-ux.md): persistenza lato server
        // in WatchHistoryService, così "Continua a guardare" ed "episodi visti" sopravvivono a un
        // reset dell'app/localStorage sulla TV.
        // ------------------------------------------------------------

        group.MapPost("/history/progress", (WatchProgressEntry entry, WatchHistoryService history) =>
        {
            if (entry.TmdbId <= 0 || string.IsNullOrWhiteSpace(entry.MediaType))
                return Results.Json(new { error = "tmdbId/mediaType mancanti." }, statusCode: 400);

            history.SaveProgress(entry);
            return Results.Json(new { ok = true });
        });

        // Eventi play/pausa dal player webOS (sessione 2026-09-14, docs/idee-miglioramento-webos.md):
        // senza questo, i log server durante uno stallo non distinguono una pausa volontaria
        // dell'utente da un vero blocco in lettura. "Fire and forget" lato client, best-effort qui:
        // un evento perso non deve mai rompere la riproduzione, quindi nessun controllo su "link"
        // mancante/invalido oltre al fallback già in LogClientPlaybackEvent.
        group.MapPost("/playback-event", (PlaybackEventRequest req, StreamingService streaming) =>
        {
            streaming.LogClientPlaybackEvent(req.Link, req.EventName, req.PositionSeconds);
            return Results.Json(new { ok = true });
        });

        // "Continua a guardare" per la home: film e serie separati (un tmdbId per volta anche per
        // le serie, con solo l'episodio toccato più di recente). Arricchito con poster/backdrop
        // freschi da TMDB (non salvati nella cronologia: evita immagini stantie se TMDB le
        // aggiorna, e tiene la cronologia leggera).
        group.MapGet("/history/continue-watching", async (string mediaType, WatchHistoryService history, TmdbClient tmdb, CancellationToken ct) =>
        {
            var entries = history.GetContinueWatching(mediaType).Take(12);
            var enriched = new List<object>();
            foreach (var e in entries)
            {
                var detail = await tmdb.GetTitleDetailsAsync(e.TmdbId, e.MediaType, ct);

                string? episodeTitle = null;
                if (string.Equals(e.MediaType, "tv", StringComparison.OrdinalIgnoreCase) && e.Season is { } season && e.Episode is { } episode)
                {
                    var episodes = await tmdb.GetSeasonEpisodesAsync(e.TmdbId, season, ct);
                    episodeTitle = episodes.FirstOrDefault(ep => ep.EpisodeNumber == episode)?.Name;
                }

                enriched.Add(new
                {
                    tmdbId = e.TmdbId,
                    mediaType = e.MediaType,
                    title = e.Title,
                    season = e.Season,
                    episode = e.Episode,
                    episodeTitle,
                    positionSeconds = e.PositionSeconds,
                    durationSeconds = e.DurationSeconds,
                    fileLink = e.FileLink,
                    fileName = e.FileName,
                    provider = e.Provider,
                    posterUrl = detail?.PosterUrl,
                    backdropUrl = detail?.BackdropUrl
                });
            }
            return Results.Json(enriched);
        });

        // Episodi con progresso/completamento noto per una serie — usato dalla pagina di dettaglio
        // per il badge "✓ Visto" e per calcolare il "prossimo episodio" (lato client, che ha già
        // seasons/episodeCount dalla stessa chiamata a /search/detail, non serve duplicarlo qui).
        group.MapGet("/history/show", (int tmdbId, WatchHistoryService history) =>
        {
            var episodes = history.GetShowHistory(tmdbId).Select(e => new
            {
                season = e.Season,
                episode = e.Episode,
                completed = e.Completed,
                positionSeconds = e.PositionSeconds,
                durationSeconds = e.DurationSeconds
            });
            return Results.Json(episodes);
        });

        // Titoli degli episodi di una stagione — usato per mostrare il titolo vero invece del solo
        // "S01E01" in "Continua a guardare"/"Prossimo episodio" (richiesta utente).
        group.MapGet("/search/episodes", async (int tmdbId, int season, TmdbClient tmdb, CancellationToken ct) =>
        {
            var episodes = await tmdb.GetSeasonEpisodesAsync(tmdbId, season, ct);
            return Results.Json(episodes.Select(e => new { episodeNumber = e.EpisodeNumber, name = e.Name }));
        });

        // Rimozione esplicita da "Continua a guardare" (richiesta utente, es. per un titolo già
        // finito altrove o che non interessa più riprendere).
        group.MapDelete("/history/progress", (int tmdbId, string mediaType, int? season, int? episode, WatchHistoryService history) =>
        {
            history.RemoveProgress(mediaType, tmdbId, season, episode);
            return Results.Json(new { ok = true });
        });

        // ------------------------------------------------------------
        // Watchlist ("da vedere più tardi") — richiesta utente, indipendente dalla cronologia di
        // visione: qui non c'è un file/posizione, solo l'intenzione di guardarlo in futuro.
        // ------------------------------------------------------------

        group.MapGet("/watchlist", async (WatchlistService watchlist, TmdbClient tmdb, CancellationToken ct) =>
        {
            var entries = watchlist.GetAll();
            var enriched = new List<object>();
            foreach (var e in entries)
            {
                var detail = await tmdb.GetTitleDetailsAsync(e.TmdbId, e.MediaType, ct);
                enriched.Add(new
                {
                    tmdbId = e.TmdbId,
                    mediaType = e.MediaType,
                    title = e.Title,
                    year = e.Year,
                    posterUrl = detail?.PosterUrl,
                    backdropUrl = detail?.BackdropUrl
                });
            }
            return Results.Json(enriched);
        });

        group.MapGet("/watchlist/check", (int tmdbId, string mediaType, WatchlistService watchlist) =>
            Results.Json(new { saved = watchlist.IsSaved(mediaType, tmdbId) }));

        group.MapPost("/watchlist", (WatchlistEntry entry, WatchlistService watchlist) =>
        {
            if (entry.TmdbId <= 0 || string.IsNullOrWhiteSpace(entry.MediaType))
                return Results.Json(new { error = "tmdbId/mediaType mancanti." }, statusCode: 400);
            watchlist.Add(entry);
            return Results.Json(new { ok = true });
        });

        group.MapDelete("/watchlist", (int tmdbId, string mediaType, WatchlistService watchlist) =>
        {
            watchlist.Remove(mediaType, tmdbId);
            return Results.Json(new { ok = true });
        });

        // Preferenze (qualità streaming, qualità/lingua torrent) — NON l'host, quello resta solo
        // sul client (serve per raggiungere questo stesso server). Richiesta utente: dopo un
        // reinstall dell'app WebOS che svuota il localStorage, recuperarle da qui invece di doverle
        // reimpostare a mano ogni volta.
        group.MapGet("/preferences", (TvPreferencesService prefs) => Results.Json(prefs.Get()));

        group.MapPost("/preferences", (TvPreferences prefs, TvPreferencesService service) =>
        {
            service.Save(prefs);
            return Results.Json(new { ok = true });
        });
    }

    private record PrepareRequest(string Title, string Magnet, string? SiteName, string? Provider);

    private record AcquireRequest(string MagnetId, string? Title, bool ToTv, int TmdbId, int? SectionId);

    private record RemoveAcquisitionRequest(string TransferId);

    private record DownloadRequest(string? Title, string Magnet, string? SiteName, bool ToTv, string? Provider, int? TmdbId, int? Season, int? Episode);

    private record PlaybackEventRequest(string Link, string EventName, double PositionSeconds);

    // Compatibilità con versioni vecchie dell'app webOS che non inviano "provider" (default
    // AllDebrid) — vedi piano-multi-provider-debrid.md / piano-premiumize-libreria.md.
    private static DebridProvider ParseProvider(string? value) => DebridProviderNames.ParseApiValue(value);

    // Duplicato volutamente da Search.razor (stesso principio, chiamante separato): confronto
    // tollerante per titolo, niente TMDB id salvato lato Plex.
    private static bool TitlesMatch(string a, string b)
    {
        static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var na = Norm(a);
        var nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        return na == nb || na.Contains(nb) || nb.Contains(na);
    }

    // --- Parsing (duplicato volutamente da Search.razor: stessa logica, chiamante separato) ---

    private static int? ParseEpisode(string title)
    {
        var m = Regex.Match(title, @"\bS\d{1,2}E(\d{1,3})\b", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var e) ? e : null;
    }

    private static string ParseResolution(string title) =>
        Regex.IsMatch(title, @"\b(2160p|4K|UHD)\b", RegexOptions.IgnoreCase) ? "4K" :
        Regex.IsMatch(title, @"\b1080p\b", RegexOptions.IgnoreCase) ? "1080p" :
        Regex.IsMatch(title, @"\b720p\b", RegexOptions.IgnoreCase) ? "720p" :
        Regex.IsMatch(title, @"\b(480p|SD)\b", RegexOptions.IgnoreCase) ? "SD" : "Altro";

    private static string ParseLanguage(string title)
    {
        if (Regex.IsMatch(title, @"\bMULTI\b", RegexOptions.IgnoreCase)) return "MULTI";
        if (Regex.IsMatch(title, @"\b(SUB\.?\s*ITA|ITA\.?\s*SUB)S?\b", RegexOptions.IgnoreCase)) return "SUB ITA";
        if (Regex.IsMatch(title, @"\bITA(?:LIAN)?\b", RegexOptions.IgnoreCase)) return "ITA";
        return "ENG";
    }

    // "17.35 GB" -> byte grezzi, per poter ordinare per dimensione reale invece che per stringa.
    // Il testo arriva già formattato da Torrentio (emoji 💾 nel titolo, vedi TorrentSearchService),
    // non c'è un valore numerico a monte da riusare direttamente.
    private static double ParseSizeBytes(string? sizeText)
    {
        if (string.IsNullOrWhiteSpace(sizeText)) return 0;
        var m = Regex.Match(sizeText, @"([\d.]+)\s*([KMGT]?B)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value))
            return 0;

        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => value * 1024,
            "MB" => value * 1024 * 1024,
            "GB" => value * 1024 * 1024 * 1024,
            "TB" => value * 1024 * 1024 * 1024 * 1024,
            _ => value
        };
    }
}
