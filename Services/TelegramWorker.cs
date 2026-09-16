using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendToPlex.Bot.Models;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SendToPlex.Bot.UI;
using Serilog.Sinks.File;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups; // 👈 bottoni inline

namespace SendToPlex.Bot.Services;


public class DownloadResult
{
    public bool Success { get; set; }
    public List<string> SavedFiles { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public TimeSpan ElapsedTime { get; set; }
    public string? MainFileName { get; set; }
    public long TotalBytes { get; set; }

    
    public Dictionary<string, long> FileSizes { get; set; } = new(); // filename -> size in bytes
}


public class TelegramWorker : BackgroundService
{
    private readonly ILogger<TelegramWorker> _log;
    private readonly ITelegramBotClient _bot;
    private readonly TelegramSettings _tg;
    private readonly AllDebridClient _ad;
    private readonly PlexClient _plex;
    private readonly Downloader _down;
    private readonly ArchiveExtractor _extractor;
    private readonly TorrentSearchService _search;
    private readonly TorrentSearchSettings _searchCfg;
    private readonly TmdbClient _tmdb;
    private readonly PathSettings _paths;
    private readonly NordVpnController _vpn;
    private readonly IHostApplicationLifetime _lifetime;

    // stato in-memory
    private readonly ConcurrentDictionary<long, bool> _destTv = new();          // true → TV, false → Movies
    private readonly ConcurrentDictionary<long, string> _pendingLinks = new();  // link in attesa di destinazione
    private readonly ConcurrentDictionary<long, List<TorrentSearchResult>> _lastSearchResults = new(); // ultimi risultati /search
    private readonly ConcurrentDictionary<long, bool> _awaitingSearchQuery = new(); // chat in attesa del testo di ricerca dopo /search
    private readonly ConcurrentDictionary<long, string> _pendingSearchQuery = new(); // query in attesa che l'utente scelga il sito
    private readonly ConcurrentDictionary<long, (string Query, TorrentSiteConfig Site, List<(int Index, TorrentSearchResult Result)> Items)> _pendingSeasonChoice = new(); // in attesa della scelta stagione
    private readonly ConcurrentDictionary<long, (string Query, TorrentSiteConfig Site, List<(int Index, TorrentSearchResult Result)> Items, int? Season)> _pendingResolutionChoice = new(); // in attesa della scelta risoluzione
    private readonly ConcurrentDictionary<long, (string Query, TorrentSiteConfig Site, List<(int Index, TorrentSearchResult Result)> Items, int? Season, string Resolution)> _pendingLanguageChoice = new(); // in attesa della scelta lingua
    private readonly ConcurrentDictionary<long, List<string>> _lastTrendingShown = new(); // ultimo elenco /trending mostrato (titoli, per indice)
    private readonly ConcurrentDictionary<long, (string Query, TorrentSiteConfig Site, string ImdbId, string MediaType)> _pendingTorrentioSeason = new(); // Torrentio: in attesa del numero di stagione
    private readonly ConcurrentDictionary<long, (string Query, TorrentSiteConfig Site, string ImdbId, string MediaType, int Season)> _pendingTorrentioEpisode = new(); // Torrentio: in attesa del numero di episodio
    private readonly ConcurrentDictionary<long, List<(TorrentSiteConfig Site, TorrentIndexPage Page)>> _lastIndexPagesShown = new(); // ultimo menù /lists
    private readonly ConcurrentDictionary<long, int> _awaitingListQuery = new(); // chat in attesa del filtro dopo /list_N senza testo
    private readonly ConcurrentDictionary<long, List<string>> _lastMagnetChoices = new(); // magnet multipli trovati su un topic, in attesa di scelta
    private readonly ConcurrentDictionary<long, List<string>> _lastTvShowsShown = new(); // ultimo elenco cartelle serie mostrato da /libtv
    private volatile bool _paused; // true dopo /stop: il bot resta in ascolto ma ignora tutto tranne /start

    // gestione job concorrenti (evita che un torrent lento blocchi il polling Telegram)
    private readonly SemaphoreSlim _jobsSemaphore;
    private readonly ConcurrentDictionary<Guid, Task> _activeJobs = new();
    private CancellationTokenSource _jobsCts = new();

    public TelegramWorker(
         ILogger<TelegramWorker> log,
         IOptions<TelegramSettings> tg,
         AllDebridClient ad,
         PlexClient plex,
         Downloader down,
         ArchiveExtractor extractor,
         TorrentSearchService search,
         IOptions<TorrentSearchSettings> searchCfg,
         IOptions<PathSettings> paths,
         NordVpnController vpn,
         TmdbClient tmdb,
         IHostApplicationLifetime lifetime)
    {
        _log = log;
        _tg = tg.Value;
        // Connessione HTTP dedicata con durata limitata del pool: senza questo, una connessione
        // aperta mentre NordVPN è attiva resta "valida" secondo .NET anche dopo la disconnessione
        // (l'interfaccia di rete sparisce ma .NET non lo scopre finché non prova a riusarla),
        // causando un errore di connessione resettata sulla richiesta Telegram subito successiva.
        var botHttpClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) });
        _bot = new TelegramBotClient(_tg.BotToken, botHttpClient);
        _ad = ad;
        _plex = plex;
        _vpn = vpn;
        _down = down;
        _extractor = extractor;
        _search = search;
        _searchCfg = searchCfg.Value;
        _paths = paths.Value;
        _tmdb = tmdb;
        _lifetime = lifetime;
        _jobsSemaphore = new SemaphoreSlim(Math.Max(1, _tg.MaxConcurrentJobs));
    }

    // --- Controllo da UI web (Impostazioni → Servizi collegati) ---
    // Riusa lo stesso meccanismo di pausa già raggiungibile via /stop e /start da Telegram:
    // il loop di polling in ExecuteAsync resta sempre attivo, ma ignora tutto tranne /start
    // quando è in pausa. Non è un vero stop/riavvio del processo (il bot token è fissato
    // all'avvio, vedi Punto 2) — è "metti in pausa"/"riprendi", utile quando non si vuole
    // aprire Telegram solo per questo.
    public bool IsPaused => _paused;
    public string? BotUsername { get; private set; }

    /// <summary>Notifica i componenti Blazor (badge in nav, Impostazioni) quando lo stato pausa/attivo
    /// cambia — sia da qui sia da /stop e /start su Telegram — così restano sincronizzati senza reload.</summary>
    public event Action? StatusChanged;

    public void Pause()
    {
        _paused = true;
        _log.LogWarning("⏸️ Pausa richiesta dalla UI web");
        StatusChanged?.Invoke();
    }

    public void Resume()
    {
        _paused = false;
        _log.LogInformation("▶️ Bot riattivato dalla UI web");
        StatusChanged?.Invoke();
    }

    // Telegram non permette EditMessageText su un messaggio foto (serve EditMessageCaption):
    // questo helper sceglie l'API giusta a seconda che il messaggio originale sia una foto
    // (es. il poster TMDB mostrato dopo /search) o un normale messaggio di testo.
    private async Task EditMessageTextOrCaptionAsync(long chatId, Telegram.Bot.Types.Message message, string text, CancellationToken ct)
    {
        try
        {
            if (message.Photo is not null)
                await _bot.EditMessageCaption(new ChatId(chatId), message.MessageId, text, cancellationToken: ct);
            else
                await _bot.EditMessageText(new ChatId(chatId), message.MessageId, text, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Impossibile modificare il messaggio (chat {ChatId}, messageId {MessageId})", chatId, message.MessageId);
        }
    }

    /// <summary>
    /// Esegue <paramref name="job"/> in background senza bloccare il loop di polling Telegram,
    /// rispettando il limite di concorrenza configurato in TelegramSettings.MaxConcurrentJobs.
    /// </summary>
    private void DispatchJob(long chatId, Func<CancellationToken, Task> job)
    {
        var jobId = Guid.NewGuid();
        var token = _jobsCts.Token;

        var task = Task.Run(async () =>
        {
            try
            {
                await _jobsSemaphore.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await job(token);
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation("⏹️ Job {JobId} annullato (chat {ChatId})", jobId, chatId);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "❌ Errore non gestito nel job {JobId} (chat {ChatId})", jobId, chatId);
            }
            finally
            {
                _jobsSemaphore.Release();
                _activeJobs.TryRemove(jobId, out _);
            }
        });

        _activeJobs[jobId] = task;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _jobsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var me = await _bot.GetMe(ct);
        BotUsername = me.Username;
        _log.LogInformation("🤖 Bot @{User} avviato (ID={Id})", me.Username, me.Id);

        try
        {
            var commands = new[]
            {
                new BotCommand { Command = "start", Description = "Avvia il bot e mostra le istruzioni" },
                new BotCommand { Command = "movies", Description = "Imposta la cartella di destinazione su Movies" },
                new BotCommand { Command = "tv", Description = "Imposta la cartella di destinazione su TV" },
                new BotCommand { Command = "torrents", Description = "Mostra gli ultimi 20 torrent scaricati" },
                new BotCommand { Command = "search", Description = "Cerca un torrent sui siti configurati" },
                new BotCommand { Command = "trending", Description = "Serie/film di tendenza questa settimana (TMDB)" },
                new BotCommand { Command = "lists", Description = "Mostra le categorie/liste sfogliabili per intero" },
                new BotCommand { Command = "library", Description = "Sfoglia i file già scaricati (Film/Serie TV)" },
                new BotCommand { Command = "watchlist", Description = "Mostra le serie/link salvati da ricontrollare" },
                new BotCommand { Command = "stop", Description = "Ferma il bot e i download in corso" }
            };
            await _bot.SetMyCommands(commands, cancellationToken: ct);
            _log.LogInformation("✅ Menu comandi Telegram aggiornato.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "⚠️ Errore durante l'impostazione dei comandi Telegram. Il bot si avvierà comunque.");
        }

        var offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await _bot.GetUpdates(
                    offset: offset,
                    timeout: 30,
                    cancellationToken: ct);

                foreach (var u in updates)
                {
                    offset = u.Id + 1;

                    if (_paused)
                    {
                        var resumeText = u.Message?.Text?.Trim();
                        var isResumeCommand = resumeText is not null && resumeText.Equals("/start", StringComparison.OrdinalIgnoreCase);

                        // In pausa: ignora tutto (comandi, testo, bottoni) tranne /start, che riattiva il bot.
                        if (!isResumeCommand)
                            continue;
                    }

                    // --- GESTIONE CALLBACK (bottoni inline) ---
                    if (u.CallbackQuery is { } cb)
                    {
                        var cbChatId = cb.Message.Chat.Id;
                        var choice = cb.Data;

                        if (_pendingLinks.TryGetValue(cbChatId, out var pendingLink))
                        {
                            _pendingLinks.TryRemove(cbChatId, out _);

                            try
                            {
                                if (choice == "dest_movies" || choice == "dest_tv")
                                {
                                    var toTv = choice == "dest_tv";
                                    _destTv[cbChatId] = toTv;

                                    var titleForCheck = pendingLink.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                                        ? ExtractFileNameFromMagnet(pendingLink) ?? pendingLink
                                        : Path.GetFileName(pendingLink);

                                    var dupWarning = CheckForDuplicate(titleForCheck, toTv);

                                    if (dupWarning is not null)
                                    {
                                        _pendingLinks[cbChatId] = pendingLink; // serve ancora per il prossimo callback (conferma/annulla)

                                        var confirmKeyboard = new InlineKeyboardMarkup(new[]
                                        {
                                            new []
                                            {
                                                InlineKeyboardButton.WithCallbackData("⬇️ Scarica comunque", "confirm_download"),
                                                InlineKeyboardButton.WithCallbackData("❌ Annulla", "cancel_download")
                                            }
                                        });

                                        await _bot.EditMessageText(
                                            new ChatId(cbChatId),
                                            cb.Message.MessageId,
                                            $"{dupWarning}\n\nVuoi scaricare comunque?",
                                            replyMarkup: confirmKeyboard,
                                            cancellationToken: ct);

                                        _log.LogInformation("⚠️ Possibile duplicato per chat {ChatId}: {Warning}", cbChatId, dupWarning);
                                    }
                                    else
                                    {
                                        var destLabel = toTv ? "TV" : "Movies";
                                        await _bot.EditMessageText(
                                            new ChatId(cbChatId),
                                            cb.Message.MessageId,
                                            $"📂 Destinazione scelta: {destLabel}",
                                            cancellationToken: ct);

                                        _log.LogInformation("➡️ Chat {ChatId}: scelta {Dest} per link {Link}", cbChatId, destLabel, pendingLink);
                                        DispatchJob(cbChatId, jobCt => HandleLinkAsync(cbChatId, pendingLink, jobCt));
                                    }
                                }
                                else if (choice == "confirm_download")
                                {
                                    await _bot.EditMessageText(new ChatId(cbChatId), cb.Message.MessageId, "⬇️ Scarico comunque…", cancellationToken: ct);
                                    _log.LogInformation("➡️ Chat {ChatId}: download confermato nonostante possibile duplicato", cbChatId);
                                    DispatchJob(cbChatId, jobCt => HandleLinkAsync(cbChatId, pendingLink, jobCt));
                                }
                                else if (choice == "cancel_download")
                                {
                                    await _bot.EditMessageText(new ChatId(cbChatId), cb.Message.MessageId, "❌ Download annullato.", cancellationToken: ct);
                                    _log.LogInformation("🚫 Chat {ChatId}: download annullato (possibile duplicato)", cbChatId);
                                }
                            }
                            catch (Exception ex)
                            {
                                _log.LogError(ex, "❌ Errore in HandleLinkAsync (CallbackQuery)");
                                await _bot.SendMessage(new ChatId(cbChatId),
                                    $"❌ Errore avviando download: {ex.Message}",
                                    cancellationToken: ct);
                            }
                        }
                        else if (choice is not null && choice.StartsWith("searchsite_", StringComparison.Ordinal) &&
                                 _pendingSearchQuery.TryRemove(cbChatId, out var pendingQuery))
                        {
                            var siteName = choice.Substring("searchsite_".Length);
                            var site = _searchCfg.Sites.FirstOrDefault(s => s.Enabled && s.Name == siteName);

                            if (site is null)
                            {
                                await EditMessageTextOrCaptionAsync(cbChatId, cb.Message,
                                    "⚠️ Sito non più disponibile. Rifai /search.", ct);
                            }
                            else if (site.UseTorrentioApi)
                            {
                                await EditMessageTextOrCaptionAsync(cbChatId, cb.Message,
                                    $"🔎 Cerco \"{pendingQuery}\" su TMDB per Torrentio…", ct);
                                DispatchJob(cbChatId, jobCt => StartTorrentioSearchAsync(cbChatId, pendingQuery, site, jobCt));
                            }
                            else
                            {
                                await EditMessageTextOrCaptionAsync(cbChatId, cb.Message,
                                    $"🔎 Ricerca \"{pendingQuery}\" su {site.Name} in corso…", ct);
                                DispatchJob(cbChatId, jobCt => RunSearchOnSiteAsync(cbChatId, pendingQuery, site, jobCt));
                            }
                        }
                        else if (choice is not null && choice.StartsWith("searchseason_", StringComparison.Ordinal) &&
                                 _pendingSeasonChoice.TryRemove(cbChatId, out var seasonState))
                        {
                            var seasonKey = choice.Substring("searchseason_".Length);
                            int? chosenSeason = seasonKey != "none" && int.TryParse(seasonKey, out var sNum) ? sNum : null;
                            var seasonLabel = chosenSeason.HasValue ? $"Stagione {chosenSeason}" : "Senza stagione";

                            var filtered = seasonState.Items
                                .Where(x => ParseSeasonAndResolution(x.Result.Title).Season == chosenSeason)
                                .ToList();

                            await _bot.EditMessageText(new ChatId(cbChatId), cb.Message.MessageId,
                                $"📅 {seasonLabel} scelta. Controllo le risoluzioni disponibili…", cancellationToken: ct);

                            DispatchJob(cbChatId, jobCt =>
                                PresentResolutionChoiceAsync(cbChatId, seasonState.Query, seasonState.Site, filtered, chosenSeason, jobCt));
                        }
                        else if (choice is not null && choice.StartsWith("searchres_", StringComparison.Ordinal) &&
                                 _pendingResolutionChoice.TryRemove(cbChatId, out var resState))
                        {
                            var resolution = choice.Substring("searchres_".Length);

                            var filtered = resState.Items
                                .Where(x => ParseSeasonAndResolution(x.Result.Title).Resolution == resolution)
                                .ToList();

                            await _bot.EditMessageText(new ChatId(cbChatId), cb.Message.MessageId,
                                $"🎞 Risoluzione {resolution} scelta.", cancellationToken: ct);

                            DispatchJob(cbChatId, jobCt =>
                                PresentLanguageChoiceAsync(cbChatId, resState.Query, resState.Site, filtered, resState.Season, resolution, jobCt));
                        }
                        else if (choice is not null && choice.StartsWith("searchlang_", StringComparison.Ordinal) &&
                                 _pendingLanguageChoice.TryRemove(cbChatId, out var langState))
                        {
                            var language = choice.Substring("searchlang_".Length);

                            var filtered = langState.Items
                                .Where(x => ParseLanguage(x.Result.Title) == language)
                                .ToList();

                            await _bot.EditMessageText(new ChatId(cbChatId), cb.Message.MessageId,
                                $"🗣️ Lingua {language} scelta.", cancellationToken: ct);

                            DispatchJob(cbChatId, jobCt =>
                                SendFinalResultsAsync(cbChatId, langState.Query, langState.Site.Name, filtered, langState.Season, langState.Resolution, language, jobCt));
                        }
                        else if (choice is not null && choice.StartsWith("trendcat_", StringComparison.Ordinal))
                        {
                            var mediaType = choice.Substring("trendcat_".Length); // "tv" o "movie"

                            await _bot.EditMessageText(new ChatId(cbChatId), cb.Message.MessageId,
                                "🔥 Carico le tendenze della settimana…", cancellationToken: ct);

                            DispatchJob(cbChatId, jobCt => ShowTrendingListAsync(cbChatId, mediaType, jobCt));
                        }
                        else if (choice is not null && choice.StartsWith("trendpick_", StringComparison.Ordinal) &&
                                 _lastTrendingShown.TryGetValue(cbChatId, out var trendingTitles))
                        {
                            if (int.TryParse(choice.Substring("trendpick_".Length), out var trendIndex) &&
                                trendIndex >= 0 && trendIndex < trendingTitles.Count)
                            {
                                var title = trendingTitles[trendIndex];

                                await _bot.EditMessageText(new ChatId(cbChatId), cb.Message.MessageId,
                                    $"🔥 Hai scelto: \"{title}\". Avvio la ricerca…", cancellationToken: ct);

                                DispatchJob(cbChatId, jobCt => HandleSearchCommandAsync(cbChatId, title, jobCt));
                            }
                        }
                        else
                        {
                            _log.LogWarning("⚠️ Nessun pendingLink/pendingSearchQuery/pendingSeason/pendingResolution/pendingLanguage/trending trovato per chat {ChatId}", cbChatId);
                        }

                        await _bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                        continue;
                    }

                    // --- GESTIONE MESSAGGI TESTUALI ---
                    var msg = u.Message;
                    if (msg?.Text is null) continue;

                    var chatId = msg.Chat.Id;
                    var text = msg.Text.Trim();
                    _log.LogInformation("📩 Messaggio ricevuto da chat {ChatId}: {Text}", chatId, text);

                    if (!_tg.AllowedChatIds.Contains(chatId))
                    {
                        _log.LogWarning("❌ ChatId {ChatId} non autorizzato", chatId);
                        await _bot.SendMessage(new ChatId(chatId),
                            "❌ Non sei autorizzato a usare questo bot.",
                            cancellationToken: ct);
                        continue;
                    }

                    // --- STAGIONE TORRENTIO IN ATTESA ---
                    if (_pendingTorrentioSeason.TryRemove(chatId, out var torrentioSeasonState) && !text.StartsWith("/"))
                    {
                        if (!int.TryParse(text, out var seasonNum))
                        {
                            _pendingTorrentioSeason[chatId] = torrentioSeasonState;
                            await _bot.SendMessage(new ChatId(chatId), "⚠️ Scrivi solo il numero della stagione (es. 1).", cancellationToken: ct);
                            continue;
                        }

                        _pendingTorrentioEpisode[chatId] = (torrentioSeasonState.Query, torrentioSeasonState.Site,
                            torrentioSeasonState.ImdbId, torrentioSeasonState.MediaType, seasonNum);
                        await _bot.SendMessage(new ChatId(chatId), $"📺 Stagione {seasonNum} — che episodio? Scrivi il numero.", cancellationToken: ct);
                        continue;
                    }

                    // --- EPISODIO TORRENTIO IN ATTESA ---
                    if (_pendingTorrentioEpisode.TryRemove(chatId, out var torrentioEpisodeState) && !text.StartsWith("/"))
                    {
                        if (!int.TryParse(text, out var episodeNum))
                        {
                            _pendingTorrentioEpisode[chatId] = torrentioEpisodeState;
                            await _bot.SendMessage(new ChatId(chatId), "⚠️ Scrivi solo il numero dell'episodio (es. 1).", cancellationToken: ct);
                            continue;
                        }

                        DispatchJob(chatId, jobCt => RunTorrentioSearchAsync(chatId, torrentioEpisodeState.Query, torrentioEpisodeState.Site,
                            torrentioEpisodeState.ImdbId, torrentioEpisodeState.MediaType, torrentioEpisodeState.Season, episodeNum, jobCt));
                        continue;
                    }

                    // --- QUERY DI RICERCA IN ATTESA (dopo /search senza testo) ---
                    if (_awaitingSearchQuery.TryRemove(chatId, out _) && !text.StartsWith("/"))
                    {
                        DispatchJob(chatId, jobCt => HandleSearchCommandAsync(chatId, text, jobCt));
                        continue;
                    }

                    // --- FILTRO IN ATTESA (dopo /list_N senza testo) ---
                    if (_awaitingListQuery.TryRemove(chatId, out var pendingListIndex) && !text.StartsWith("/"))
                    {
                        var listQuery = text.Trim().Equals("tutti", StringComparison.OrdinalIgnoreCase) ||
                                        text.Trim().Equals("tutto", StringComparison.OrdinalIgnoreCase)
                            ? null
                            : text.Trim();
                        DispatchJob(chatId, jobCt => HandleBrowseListAsync(chatId, pendingListIndex, listQuery, jobCt));
                        continue;
                    }

                    // --- COMANDI ---
                    if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_paused)
                        {
                            _paused = false;
                            StatusChanged?.Invoke();
                            _log.LogInformation("▶️ Bot riattivato da chat {ChatId}", chatId);
                            await _bot.SendMessage(new ChatId(chatId), "▶️ Bot riattivato!", cancellationToken: ct);
                            continue;
                        }

                        await _bot.SendMessage(new ChatId(chatId),
                            "Benvenuto! Inviami un magnet o un URL hoster supportato.\n" +
                            "Puoi anche incollare un link e scegliere la destinazione con i bottoni.",
                            cancellationToken: ct);
                        continue;
                    }

                    if (text.Equals("/movies", StringComparison.OrdinalIgnoreCase))
                    {
                        _destTv[chatId] = false;
                        await _bot.SendMessage(new ChatId(chatId), "📂 Destinazione predefinita: Movies", cancellationToken: ct);
                        continue;
                    }

                    if (text.Equals("/tv", StringComparison.OrdinalIgnoreCase))
                    {
                        _destTv[chatId] = true;
                        await _bot.SendMessage(new ChatId(chatId), "📂 Destinazione predefinita: TV", cancellationToken: ct);
                        continue;
                    }

                    if (text.Equals("/torrents", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleTorrentsCommandAsync(chatId, ct);
                        continue;
                    }

                    if (text.Equals("/trending", StringComparison.OrdinalIgnoreCase))
                    {
                        DispatchJob(chatId, jobCt => HandleTrendingCommandAsync(chatId, jobCt));
                        continue;
                    }

                    if (text.StartsWith("/search", StringComparison.OrdinalIgnoreCase))
                    {
                        var query = text.Length > 7 ? text.Substring(7).Trim() : "";
                        if (string.IsNullOrWhiteSpace(query))
                        {
                            _awaitingSearchQuery[chatId] = true;
                            await _bot.SendMessage(new ChatId(chatId),
                                "🔎 Cosa vuoi cercare? Scrivi il termine nel prossimo messaggio.",
                                cancellationToken: ct);
                            continue;
                        }

                        DispatchJob(chatId, jobCt => HandleSearchCommandAsync(chatId, query, jobCt));
                        continue;
                    }

                    if (text.StartsWith("/r_", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(text.Substring(3), out var resultIndex))
                        {
                            DispatchJob(chatId, jobCt => HandleSearchSelectionAsync(chatId, resultIndex, jobCt));
                        }
                        continue;
                    }

                    if (text.StartsWith("/rm_", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(text.Substring(4), out var magnetIndex))
                        {
                            await HandleMagnetChoiceSelectionAsync(chatId, magnetIndex, ct);
                        }
                        continue;
                    }

                    if (text.Equals("/lists", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleListsCommandAsync(chatId, ct);
                        continue;
                    }

                    if (text.StartsWith("/list_", StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = text.Substring(6);
                        var spaceIdx = rest.IndexOf(' ');
                        var indexPart = spaceIdx < 0 ? rest : rest.Substring(0, spaceIdx);
                        var queryPart = spaceIdx < 0 ? "" : rest.Substring(spaceIdx + 1).Trim();

                        if (int.TryParse(indexPart, out var listIndex))
                        {
                            if (string.IsNullOrWhiteSpace(queryPart))
                            {
                                _awaitingListQuery[chatId] = listIndex;
                                await _bot.SendMessage(new ChatId(chatId),
                                    "🔎 Vuoi tutti i titoli o cerchi qualcosa di specifico in questa lista?\nScrivi il termine, oppure \"tutti\".",
                                    cancellationToken: ct);
                            }
                            else
                            {
                                DispatchJob(chatId, jobCt => HandleBrowseListAsync(chatId, listIndex, queryPart, jobCt));
                            }
                        }
                        continue;
                    }

                    if (text.Equals("/library", StringComparison.OrdinalIgnoreCase))
                    {
                        await _bot.SendMessage(new ChatId(chatId),
                            "📁 Cosa vuoi vedere?\n\n🎬 Film: /libmovies\n📺 Serie TV: /libtv",
                            cancellationToken: ct);
                        continue;
                    }

                    if (text.Equals("/libmovies", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleLibraryMoviesAsync(chatId, ct);
                        continue;
                    }

                    if (text.Equals("/libtv", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleLibraryTvAsync(chatId, ct);
                        continue;
                    }

                    if (text.StartsWith("/libshow_", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(text.Substring(9), out var showIndex))
                        {
                            await HandleLibraryShowFilesAsync(chatId, showIndex, ct);
                        }
                        continue;
                    }

                    if (text.Equals("/watchlist", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleWatchlistCommandAsync(chatId, ct);
                        continue;
                    }

                    if (text.StartsWith("/watchadd_", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(text.Substring(10), out var addIndex))
                        {
                            await HandleWatchAddAsync(chatId, addIndex, ct);
                        }
                        continue;
                    }

                    if (text.StartsWith("/watchdel_", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(text.Substring(10), out var delIndex))
                        {
                            await HandleWatchDeleteAsync(chatId, delIndex, ct);
                        }
                        continue;
                    }

                    if (text.StartsWith("/watch_", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(text.Substring(7), out var watchIndex))
                        {
                            DispatchJob(chatId, jobCt => HandleWatchCheckAsync(chatId, watchIndex, jobCt));
                        }
                        continue;
                    }

                    if (text.Equals("/stop", StringComparison.OrdinalIgnoreCase))
                    {
                        _paused = true;
                        StatusChanged?.Invoke();
                        _log.LogWarning("⏸️ Pausa richiesta da chat {ChatId}", chatId);
                        await _bot.SendMessage(new ChatId(chatId),
                            "⏸️ Bot in pausa: ignoro tutto finché non ricevo /start. I download già in corso continuano.",
                            cancellationToken: ct);
                        continue;
                    }

                    if (text.StartsWith("/dlall_", StringComparison.OrdinalIgnoreCase))
            {
                var id = text.Substring(7);
                var toTv = _destTv.TryGetValue(chatId, out var v) && v;
                DispatchJob(chatId, jobCt => HandleMagnetBatchAsync(chatId, id, $"Batch ID: {id}", toTv, jobCt));
                continue;
            }

            if (text.StartsWith("/dlf_", StringComparison.OrdinalIgnoreCase))
            {
                var parts = text.Substring(5).Split('_');
                if (parts.Length >= 2 && int.TryParse(parts[1], out var index))
                {
                    var id = parts[0];
                    var toTv = _destTv.TryGetValue(chatId, out var v) && v;

                    var files = await _ad.GetMagnetFilesDetailedAsync(id, ct);
                    if (index >= 0 && index < files.Count)
                    {
                        var link = files[index].Link;
                        var name = files[index].Name;
                        DispatchJob(chatId, jobCt => HandleDirectLinkAsync(chatId, link, toTv, jobCt, name));
                    }
                }
                continue;
            }

            if (text.StartsWith("/dl_", StringComparison.OrdinalIgnoreCase))
                    {
                        var magnetId = text.Substring(4);
                        _pendingLinks[chatId] = $"dl:{magnetId}";
                        
                        var keyboard = new InlineKeyboardMarkup(new[]
                        {
                            new []
                            {
                                InlineKeyboardButton.WithCallbackData("🎬 Movies", "dest_movies"),
                                InlineKeyboardButton.WithCallbackData("📺 TV", "dest_tv")
                            }
                        });

                        await _bot.SendMessage(new ChatId(chatId),
                            $"Hai selezionato il torrent con ID {magnetId}.\nIn quale cartella vuoi salvare i file?",
                            replyMarkup: keyboard,
                            cancellationToken: ct);
                        
                        continue;
                    }

                    // --- LINK RICEVUTO ---
                    if (text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
                        text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsValidLink(text))
                        {
                            await _bot.SendMessage(new ChatId(chatId),
                                "❌ Link non valido. Un magnet deve contenere `xt=urn:btih:`; un URL deve essere http/https e non puntare a un host locale.",
                                cancellationToken: ct);
                            continue;
                        }

                        _pendingLinks[chatId] = text;

                        var keyboard = new InlineKeyboardMarkup(new[]
                        {
                            new []
                            {
                                InlineKeyboardButton.WithCallbackData("🎬 Movies", "dest_movies"),
                                InlineKeyboardButton.WithCallbackData("📺 TV", "dest_tv")
                            }
                        });

                        await _bot.SendMessage(new ChatId(chatId),
                            "In quale cartella vuoi salvare?",
                            replyMarkup: keyboard,
                            cancellationToken: ct);

                        _log.LogInformation("🔗 Chat {ChatId}: link ricevuto, in attesa scelta destinazione", chatId);
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "❌ Errore nel polling Telegram");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task HandleTorrentsCommandAsync(long chatId, CancellationToken ct)
    {
        try
        {
            await _bot.SendMessage(new ChatId(chatId), "🔄 Recupero lista torrent...", cancellationToken: ct);
            var magnets = await _ad.GetMagnetsAsync(ct);

            if (magnets.Count == 0)
            {
                await _bot.SendMessage(new ChatId(chatId), "Nessun torrent trovato su AllDebrid.", cancellationToken: ct);
                return;
            }

            // Ordina per data (più recenti prima) e prendi i primi 20
            var recent = magnets.OrderByDescending(m => m.UploadDate).Take(20).ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("📋 **Ultimi Torrent:**\n");
            
            foreach (var m in recent)
            {
                var icon = m.IsReady ? "✅" : "⏳";
                var name = m.Filename.Length > 30 ? m.Filename.Substring(0, 27) + "..." : m.Filename;
                var size = FormatFileSize(m.Size);
                
                sb.AppendLine($"{icon} `{name}`");
                sb.AppendLine($"📦 {size} | Stato: {m.Status}");
                sb.AppendLine($"👉 Avvia: /dl\\_{m.Id}");
                sb.AppendLine();
            }

            await _bot.SendMessage(new ChatId(chatId), sb.ToString(), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Errore durante il recupero dei torrent");
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task HandleListsCommandAsync(long chatId, CancellationToken ct)
    {
        var pages = TorrentSearchService.GetAvailableIndexPages(_searchCfg);

        if (pages.Count == 0)
        {
            await _bot.SendMessage(new ChatId(chatId),
                "⚠️ Nessuna lista/categoria configurata. Aggiungine una dalla tab \"Ricerca Torrent\" nell'app.",
                cancellationToken: ct);
            return;
        }

        _lastIndexPagesShown[chatId] = pages;

        var sb = new StringBuilder();
        sb.AppendLine("📂 **Categorie disponibili:**\n");

        for (int i = 0; i < pages.Count; i++)
        {
            sb.AppendLine($"**{i + 1}.** {pages[i].Page.Name} ({pages[i].Site.Name})");
            sb.AppendLine($"👉 Sfoglia: /list\\_{i}");
            sb.AppendLine();
        }

        await _bot.SendMessage(new ChatId(chatId), sb.ToString(), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
    }

    private async Task HandleBrowseListAsync(long chatId, int index, string? query, CancellationToken ct)
    {
        try
        {
            if (!_lastIndexPagesShown.TryGetValue(chatId, out var pages) || index < 0 || index >= pages.Count)
            {
                await _bot.SendMessage(new ChatId(chatId), "⚠️ Categoria non trovata. Rifai /lists.", cancellationToken: ct);
                return;
            }

            var (site, page) = pages[index];

            await EnsureVpnConnectedAsync(chatId, site.RequiresVpn, ct);

            var loadingMsg = string.IsNullOrWhiteSpace(query)
                ? $"🔄 Carico \"{page.Name}\"…"
                : $"🔄 Cerco \"{query}\" in \"{page.Name}\"…";
            await _bot.SendMessage(new ChatId(chatId), loadingMsg, cancellationToken: ct);

            var results = await _search.BrowseIndexPageAsync(site, page, _searchCfg.TimeoutSeconds, _searchCfg.MaxResultsPerSite, ct, query);

            if (results.Count == 0)
            {
                await _bot.SendMessage(new ChatId(chatId), "❌ Nessun titolo trovato in questa lista.", cancellationToken: ct);
                return;
            }

            _lastSearchResults[chatId] = results;

            var sb = new StringBuilder();
            var header = string.IsNullOrWhiteSpace(query) ? page.Name : $"{page.Name} — filtro: \"{query}\"";
            sb.AppendLine($"📂 **{header}:**\n");

            for (int i = 0; i < results.Count; i++)
            {
                var title = results[i].Title.Length > 60 ? results[i].Title.Substring(0, 57) + "..." : results[i].Title;
                sb.AppendLine($"**{i + 1}.** `{title}`");
                sb.AppendLine($"👉 Scarica: /r\\_{i} | 📌 Salva: /watchadd\\_{i}");
                sb.AppendLine();
            }

            if (results.Count >= _searchCfg.MaxResultsPerSite)
                sb.AppendLine($"⚠️ Mostrati i primi {results.Count} titoli (limite \"Max risultati/sito\"). Alza il limite nella tab Ricerca Torrent per vederne di più.");

            await _bot.SendMessage(new ChatId(chatId), sb.ToString(), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
            _log.LogInformation("📂 Sfoglia lista \"{List}\" per chat {ChatId}: {Count} titoli", page.Name, chatId, results.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore durante /list_{Index} per chat {ChatId}", index, chatId);
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task HandleLibraryMoviesAsync(long chatId, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_paths.Movies) || !Directory.Exists(_paths.Movies))
            {
                await _bot.SendMessage(new ChatId(chatId), "⚠️ Cartella Movies non configurata o non trovata.", cancellationToken: ct);
                return;
            }

            var files = Directory.GetFiles(_paths.Movies).OrderBy(f => f).ToList();

            if (files.Count == 0)
            {
                await _bot.SendMessage(new ChatId(chatId), "📁 Cartella Movies vuota.", cancellationToken: ct);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"🎬 **Movies** ({files.Count} file):\n");

            foreach (var f in files.Take(40))
            {
                var info = new FileInfo(f);
                sb.AppendLine($"📄 `{info.Name}` — {FormatFileSize(info.Length)}");
            }

            if (files.Count > 40)
                sb.AppendLine($"\n...e altri {files.Count - 40} file non mostrati.");

            await _bot.SendMessage(new ChatId(chatId), sb.ToString(), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore durante /libmovies per chat {ChatId}", chatId);
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task HandleLibraryTvAsync(long chatId, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_paths.Tv) || !Directory.Exists(_paths.Tv))
            {
                await _bot.SendMessage(new ChatId(chatId), "⚠️ Cartella TV non configurata o non trovata.", cancellationToken: ct);
                return;
            }

            var shows = Directory.GetDirectories(_paths.Tv).OrderBy(d => d).ToList();

            if (shows.Count == 0)
            {
                await _bot.SendMessage(new ChatId(chatId), "📁 Cartella TV vuota.", cancellationToken: ct);
                return;
            }

            _lastTvShowsShown[chatId] = shows;

            var sb = new StringBuilder();
            sb.AppendLine($"📺 **Serie TV** ({shows.Count}):\n");

            for (int i = 0; i < shows.Count && i < 60; i++)
            {
                var name = Path.GetFileName(shows[i]);
                var episodeCount = Directory.GetFiles(shows[i], "*", SearchOption.AllDirectories).Length;
                sb.AppendLine($"**{i + 1}.** {name} ({episodeCount} file)");
                sb.AppendLine($"👉 /libshow\\_{i}");
            }

            if (shows.Count > 60)
                sb.AppendLine($"\n...e altre {shows.Count - 60} serie non mostrate.");

            await _bot.SendMessage(new ChatId(chatId), sb.ToString(), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore durante /libtv per chat {ChatId}", chatId);
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task HandleLibraryShowFilesAsync(long chatId, int index, CancellationToken ct)
    {
        try
        {
            if (!_lastTvShowsShown.TryGetValue(chatId, out var shows) || index < 0 || index >= shows.Count)
            {
                await _bot.SendMessage(new ChatId(chatId), "⚠️ Serie non trovata. Rifai /libtv.", cancellationToken: ct);
                return;
            }

            var showPath = shows[index];
            var name = Path.GetFileName(showPath);
            var files = Directory.GetFiles(showPath, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();

            if (files.Count == 0)
            {
                await _bot.SendMessage(new ChatId(chatId), $"📁 \"{name}\" è vuota.", cancellationToken: ct);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"📺 **{name}** ({files.Count} file):\n");

            foreach (var f in files.Take(60))
            {
                var info = new FileInfo(f);
                sb.AppendLine($"📄 `{info.Name}` — {FormatFileSize(info.Length)}");
            }

            if (files.Count > 60)
                sb.AppendLine($"\n...e altri {files.Count - 60} file non mostrati.");

            await _bot.SendMessage(new ChatId(chatId), sb.ToString(), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore durante /libshow_{Index} per chat {ChatId}", index, chatId);
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task HandleWatchlistCommandAsync(long chatId, CancellationToken ct)
    {
        var items = WatchlistManager.Load();

        if (items.Count == 0)
        {
            await _bot.SendMessage(new ChatId(chatId),
                "📌 Watchlist vuota. Dopo una ricerca, usa /watchadd\\_N su un risultato per salvarlo qui.",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("📌 **Watchlist:**\n");

        for (int i = 0; i < items.Count; i++)
        {
            sb.AppendLine($"**{i + 1}.** {items[i].Name} ({items[i].SiteName})");
            sb.AppendLine($"👉 Controlla: /watch\\_{i} | Rimuovi: /watchdel\\_{i}");
            sb.AppendLine();
        }

        await _bot.SendMessage(new ChatId(chatId), sb.ToString(), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
    }

    private async Task HandleWatchAddAsync(long chatId, int index, CancellationToken ct)
    {
        if (!_lastSearchResults.TryGetValue(chatId, out var results) || index < 0 || index >= results.Count)
        {
            await _bot.SendMessage(new ChatId(chatId), "⚠️ Risultato non trovato. Rifai una ricerca con /search.", cancellationToken: ct);
            return;
        }

        var result = results[index];
        var url = result.DetailUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            await _bot.SendMessage(new ChatId(chatId), "❌ Questo risultato non ha un link di pagina da salvare.", cancellationToken: ct);
            return;
        }

        var items = WatchlistManager.Load();

        if (items.Any(i => i.Url == url))
        {
            await _bot.SendMessage(new ChatId(chatId), "ℹ️ Questo link è già in watchlist.", cancellationToken: ct);
            return;
        }

        items.Add(new WatchlistItem { Name = result.Title, Url = url, SiteName = result.SiteName });
        WatchlistManager.Save(items);

        await _bot.SendMessage(new ChatId(chatId), $"✅ Aggiunto alla watchlist: `{result.Title}`", parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
        _log.LogInformation("📌 Chat {ChatId}: aggiunto a watchlist \"{Title}\" ({Url})", chatId, result.Title, url);
    }

    private async Task HandleWatchDeleteAsync(long chatId, int index, CancellationToken ct)
    {
        var items = WatchlistManager.Load();

        if (index < 0 || index >= items.Count)
        {
            await _bot.SendMessage(new ChatId(chatId), "⚠️ Voce non trovata. Rifai /watchlist.", cancellationToken: ct);
            return;
        }

        var removed = items[index];
        items.RemoveAt(index);
        WatchlistManager.Save(items);

        await _bot.SendMessage(new ChatId(chatId), $"🗑️ Rimosso dalla watchlist: `{removed.Name}`", parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
    }

    private async Task HandleWatchCheckAsync(long chatId, int index, CancellationToken ct)
    {
        try
        {
            var items = WatchlistManager.Load();

            if (index < 0 || index >= items.Count)
            {
                await _bot.SendMessage(new ChatId(chatId), "⚠️ Voce non trovata. Rifai /watchlist.", cancellationToken: ct);
                return;
            }

            var item = items[index];
            var site = _searchCfg.Sites.FirstOrDefault(s => s.Name == item.SiteName);

            if (site is null)
            {
                await _bot.SendMessage(new ChatId(chatId), $"❌ Sito \"{item.SiteName}\" non più configurato.", cancellationToken: ct);
                return;
            }

            await _bot.SendMessage(new ChatId(chatId), $"🔄 Controllo \"{item.Name}\"…", cancellationToken: ct);

            // Ricostruisce un risultato "sintetico" con i parametri correnti del sito (cookie,
            // selettori, VPN...) e riusa lo stesso flusso di selezione già usato per /r_N.
            var syntheticResult = new TorrentSearchResult
            {
                SiteName = site.Name,
                Title = item.Name,
                DetailUrl = item.Url,
                MagnetSelectorOnDetailPage = site.MagnetSelectorOnDetailPage,
                Cookie = site.Cookie
            };

            await EnsureVpnConnectedAsync(chatId, site.RequiresVpn, ct);

            _lastSearchResults[chatId] = new List<TorrentSearchResult> { syntheticResult };
            await HandleSearchSelectionAsync(chatId, 0, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore durante /watch_{Index} per chat {ChatId}", index, chatId);
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task HandleTrendingCommandAsync(long chatId, CancellationToken ct)
    {
        if (!_tmdb.IsConfigured)
        {
            await _bot.SendMessage(new ChatId(chatId),
                "⚠️ TMDB non configurato. Aggiungi una API Key nella tab Settings dell'app.",
                cancellationToken: ct);
            return;
        }

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📺 Serie TV", "trendcat_tv"),
                InlineKeyboardButton.WithCallbackData("🎬 Film", "trendcat_movie")
            }
        });

        await _bot.SendMessage(new ChatId(chatId),
            "🔥 Tendenze della settimana: cosa vuoi vedere?",
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    private async Task ShowTrendingListAsync(long chatId, string mediaType, CancellationToken ct)
    {
        try
        {
            var items = await _tmdb.GetTrendingAsync(mediaType, ct);

            if (items.Count == 0)
            {
                await _bot.SendMessage(new ChatId(chatId), "❌ Nessuna tendenza trovata (o errore TMDB).", cancellationToken: ct);
                return;
            }

            var top = items.Take(10).ToList();
            _lastTrendingShown[chatId] = top.Select(i => i.Title).ToList();

            var mediaLabel = mediaType == "tv" ? "📺" : "🎬";
            var buttons = top.Select((item, i) =>
            {
                var yearLabel = item.Year is not null ? $" ({item.Year})" : "";
                return new[] { InlineKeyboardButton.WithCallbackData($"{mediaLabel} {item.Title}{yearLabel}", $"trendpick_{i}") };
            });

            await _bot.SendMessage(new ChatId(chatId),
                "🔥 Tendenze di questa settimana. Scegli cosa cercare:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore mostrando le tendenze TMDB ({MediaType}) per chat {ChatId}", mediaType, chatId);
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task HandleSearchCommandAsync(long chatId, string query, CancellationToken ct)
    {
        try
        {
            var enabledSites = _searchCfg.Sites.Where(s => s.Enabled).ToList();

            if (enabledSites.Count == 0)
            {
                await _bot.SendMessage(new ChatId(chatId),
                    "⚠️ Nessun sito di ricerca configurato. Aggiungine uno dalla tab \"Ricerca Torrent\" nell'app.",
                    cancellationToken: ct);
                return;
            }

            _pendingSearchQuery[chatId] = query;

            var keyboard = new InlineKeyboardMarkup(
                enabledSites.Select(s => new[] { InlineKeyboardButton.WithCallbackData($"🌐 {s.Name}", $"searchsite_{s.Name}") }));

            var poster = await _tmdb.SearchPosterAsync(query, ct);

            if (poster is not null)
            {
                var yearLabel = poster.Year is not null ? $" ({poster.Year})" : "";
                var overview = string.IsNullOrWhiteSpace(poster.Overview)
                    ? ""
                    : "\n\n" + (poster.Overview.Length > 300 ? poster.Overview[..297] + "..." : poster.Overview);
                var caption = $"🎬 {poster.Title}{yearLabel}{overview}\n\nSu quale sito vuoi cercare \"{query}\"?";

                try
                {
                    await _bot.SendPhoto(new ChatId(chatId), InputFile.FromUri(poster.PosterUrl),
                        caption: caption, replyMarkup: keyboard, cancellationToken: ct);
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "⚠️ Impossibile inviare il poster TMDB per \"{Query}\", proseguo senza immagine", query);
                }
            }

            await _bot.SendMessage(new ChatId(chatId),
                $"🔎 Ricerca: \"{query}\"\nSu quale sito vuoi cercare?",
                replyMarkup: keyboard,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore preparando la scelta del sito per /search per chat {ChatId}", chatId);
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task RunSearchOnSiteAsync(long chatId, string query, TorrentSiteConfig site, CancellationToken ct)
    {
        try
        {
            await EnsureVpnConnectedAsync(chatId, site.RequiresVpn, ct);

            var results = await _search.SearchSiteAsync(site, query, _searchCfg.TimeoutSeconds, _searchCfg.MaxResultsPerSite, ct);

            if (results.Count == 0)
            {
                await _bot.SendMessage(new ChatId(chatId), $"❌ Nessun risultato trovato su {site.Name}.", cancellationToken: ct);
                return;
            }

            _lastSearchResults[chatId] = results;

            await SaveSearchResultsJsonAsync(query, site.Name, results, ct);

            var items = results.Select((r, i) => (Index: i, Result: r)).ToList();
            await PresentSeasonChoiceAsync(chatId, query, site, items, ct);

            _log.LogInformation("🔎 Ricerca \"{Query}\" su {Site} per chat {ChatId}: {Count} risultati", query, site.Name, chatId, results.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore durante la ricerca su {Site} per chat {ChatId}", site.Name, chatId);
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore durante la ricerca: {ex.Message}", cancellationToken: ct);
        }
    }

    // Torrentio vuole un IMDb ID, non una query testuale: lo risolviamo da TMDB a partire dal
    // titolo. Per i film si può cercare subito; per le serie servono anche stagione ed episodio,
    // quindi li chiediamo prima di interrogare Torrentio (a differenza degli altri siti, dove si
    // filtra DOPO aver visto i risultati).
    private async Task StartTorrentioSearchAsync(long chatId, string query, TorrentSiteConfig site, CancellationToken ct)
    {
        var match = await _tmdb.ResolveImdbIdAsync(query, ct);
        if (match is null)
        {
            await _bot.SendMessage(new ChatId(chatId),
                $"❌ Non ho trovato \"{query}\" su TMDB, impossibile cercare su {site.Name}.", cancellationToken: ct);
            return;
        }

        if (match.MediaType == "movie")
        {
            await _bot.SendMessage(new ChatId(chatId), $"🎬 {match.Title} — cerco su {site.Name}…", cancellationToken: ct);
            await RunTorrentioSearchAsync(chatId, query, site, match.ImdbId, match.MediaType, null, null, ct);
            return;
        }

        _pendingTorrentioSeason[chatId] = (query, site, match.ImdbId, match.MediaType);
        await _bot.SendMessage(new ChatId(chatId), $"📺 {match.Title} — che stagione? Scrivi il numero.", cancellationToken: ct);
    }

    private async Task RunTorrentioSearchAsync(long chatId, string query, TorrentSiteConfig site,
        string imdbId, string mediaType, int? season, int? episode, CancellationToken ct)
    {
        try
        {
            var results = await _search.SearchTorrentioApiAsync(site, imdbId, mediaType, season, episode, _searchCfg.MaxResultsPerSite, ct);

            if (results.Count == 0)
            {
                await _bot.SendMessage(new ChatId(chatId), $"❌ Nessun risultato trovato su {site.Name}.", cancellationToken: ct);
                return;
            }

            _lastSearchResults[chatId] = results;
            await SaveSearchResultsJsonAsync(query, site.Name, results, ct);

            var items = results.Select((r, i) => (Index: i, Result: r)).ToList();
            // Stagione già fissata (o assente per un film): si salta la scelta stagione e si va
            // dritti alla risoluzione, come già facciamo per i siti "per singolo episodio".
            await PresentResolutionChoiceAsync(chatId, query, site, items, season, ct);

            _log.LogInformation("🔎 Ricerca Torrentio \"{Query}\" su {Site} per chat {ChatId}: {Count} risultati", query, site.Name, chatId, results.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore durante la ricerca Torrentio su {Site} per chat {ChatId}", site.Name, chatId);
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore durante la ricerca: {ex.Message}", cancellationToken: ct);
        }
    }

    // Estrae stagione e risoluzione dal titolo per poter raggruppare i risultati nel messaggio Telegram.
    private static (int? Season, string Resolution) ParseSeasonAndResolution(string title)
    {
        int? season = null;
        var seasonMatch = Regex.Match(title, @"\bS(?:tagione)?\.?\s*(\d{1,2})(?:E\d{1,3})?\b", RegexOptions.IgnoreCase);
        if (seasonMatch.Success && int.TryParse(seasonMatch.Groups[1].Value, out var s))
            season = s;

        string resolution =
            Regex.IsMatch(title, @"\b(2160p|4K|UHD)\b", RegexOptions.IgnoreCase) ? "4K" :
            Regex.IsMatch(title, @"\b1080p\b", RegexOptions.IgnoreCase) ? "1080p" :
            Regex.IsMatch(title, @"\b720p\b", RegexOptions.IgnoreCase) ? "720p" :
            Regex.IsMatch(title, @"\b(480p|SD)\b", RegexOptions.IgnoreCase) ? "SD" :
            "Altro";

        return (season, resolution);
    }

    private static int ResolutionSortRank(string resolution) => resolution switch
    {
        "4K" => 0,
        "1080p" => 1,
        "720p" => 2,
        "SD" => 3,
        _ => 4
    };

    // Estrae la lingua/audio dal titolo (solo indicativo, dedotto dalle convenzioni di naming dei
    // gruppi di release: tag come MULTI, ITA, SUB ITA). Usato solo sui siti con DetectLanguage=true.
    private static string ParseLanguage(string title)
    {
        if (Regex.IsMatch(title, @"\bMULTI\b", RegexOptions.IgnoreCase))
            return "MULTI";
        if (Regex.IsMatch(title, @"\b(SUB\.?\s*ITA|ITA\.?\s*SUB)S?\b", RegexOptions.IgnoreCase))
            return "SUB ITA";
        if (Regex.IsMatch(title, @"\bITA(?:LIAN)?\b", RegexOptions.IgnoreCase))
            return "ITA";
        return "ENG";
    }

    // Mostra le stagioni trovate tra i risultati e chiede quale vedere. Se nessun titolo ha una
    // stagione riconoscibile (es. è un film), salta direttamente alla scelta della risoluzione.
    private async Task PresentSeasonChoiceAsync(long chatId, string query, TorrentSiteConfig site,
        List<(int Index, TorrentSearchResult Result)> items, CancellationToken ct)
    {
        var seasons = items
            .Select(x => ParseSeasonAndResolution(x.Result.Title).Season)
            .Distinct()
            .OrderBy(s => s ?? int.MaxValue)
            .ToList();

        if (seasons.Count <= 1)
        {
            await PresentResolutionChoiceAsync(chatId, query, site, items, seasons.SingleOrDefault(), ct);
            return;
        }

        _pendingSeasonChoice[chatId] = (query, site, items);

        var buttons = seasons.Select(s => new[]
        {
            InlineKeyboardButton.WithCallbackData(
                s.HasValue ? $"📅 Stagione {s}" : "📦 Senza stagione",
                $"searchseason_{(s.HasValue ? s.Value.ToString() : "none")}")
        });

        var seasonsList = string.Join(", ", seasons.Select(s => s.HasValue ? $"Stagione {s}" : "senza stagione"));
        await _bot.SendMessage(new ChatId(chatId),
            $"📅 Trovati {items.Count} risultati su {site.Name} per \"{query}\".\nStagioni disponibili: {seasonsList}.\nQuale vuoi vedere?",
            replyMarkup: new InlineKeyboardMarkup(buttons),
            cancellationToken: ct);
    }

    // Mostra le risoluzioni trovate (nel sottoinsieme già filtrato per stagione, se applicabile)
    // e chiede quale vedere. Se ce n'è una sola, salta direttamente alla scelta della lingua.
    private async Task PresentResolutionChoiceAsync(long chatId, string query, TorrentSiteConfig site,
        List<(int Index, TorrentSearchResult Result)> items, int? season, CancellationToken ct)
    {
        var resolutions = items
            .Select(x => ParseSeasonAndResolution(x.Result.Title).Resolution)
            .Distinct()
            .OrderBy(ResolutionSortRank)
            .ToList();

        if (resolutions.Count <= 1)
        {
            await PresentLanguageChoiceAsync(chatId, query, site, items, season, resolutions.SingleOrDefault(), ct);
            return;
        }

        _pendingResolutionChoice[chatId] = (query, site, items, season);

        var buttons = resolutions.Select(r => new[]
        {
            InlineKeyboardButton.WithCallbackData($"🎞 {r}", $"searchres_{r}")
        });

        var seasonLabel = season.HasValue ? $"Stagione {season}" : "senza stagione";
        await _bot.SendMessage(new ChatId(chatId),
            $"🎞 {seasonLabel} su {site.Name}: risoluzioni disponibili: {string.Join(", ", resolutions)}.\nQuale vuoi vedere?",
            replyMarkup: new InlineKeyboardMarkup(buttons),
            cancellationToken: ct);
    }

    // Mostra le lingue trovate (nel sottoinsieme già filtrato per stagione+risoluzione) e chiede
    // quale vedere. Salta il passaggio sui siti con DetectLanguage=false (es. forum tutti in italiano)
    // o se c'è una sola lingua disponibile.
    private async Task PresentLanguageChoiceAsync(long chatId, string query, TorrentSiteConfig site,
        List<(int Index, TorrentSearchResult Result)> items, int? season, string resolution, CancellationToken ct)
    {
        if (!site.DetectLanguage)
        {
            await SendFinalResultsAsync(chatId, query, site.Name, items, season, resolution, null, ct);
            return;
        }

        var languages = items
            .Select(x => ParseLanguage(x.Result.Title))
            .Distinct()
            .ToList();

        if (languages.Count <= 1)
        {
            await SendFinalResultsAsync(chatId, query, site.Name, items, season, resolution, languages.SingleOrDefault(), ct);
            return;
        }

        _pendingLanguageChoice[chatId] = (query, site, items, season, resolution);

        var buttons = languages.Select(l => new[]
        {
            InlineKeyboardButton.WithCallbackData($"🗣️ {l}", $"searchlang_{l}")
        });

        await _bot.SendMessage(new ChatId(chatId),
            $"🗣️ Lingue disponibili su {site.Name}: {string.Join(", ", languages)}.\nQuale vuoi vedere?",
            replyMarkup: new InlineKeyboardMarkup(buttons),
            cancellationToken: ct);
    }

    // Estrae il numero di episodio dal titolo (es. "S01E05" → 5), per ordinare/etichettare i
    // risultati sui siti dove ogni torrent è un singolo episodio (PerEpisodeResults=true).
    private static int? ParseEpisode(string title)
    {
        var m = Regex.Match(title, @"\bS\d{1,2}E(\d{1,3})\b", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var e) ? e : null;
    }

    private async Task SendFinalResultsAsync(long chatId, string query, string siteName,
        List<(int Index, TorrentSearchResult Result)> items, int? season, string? resolution, string? language, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            await _bot.SendMessage(new ChatId(chatId), "⚠️ Nessun risultato corrisponde ai filtri scelti.", cancellationToken: ct);
            return;
        }

        // Se i risultati sono per singolo episodio, ordiniamo per numero di episodio invece
        // di lasciare l'ordine grezzo del sito (che spesso è per data di upload).
        var orderedItems = items.OrderBy(x => ParseEpisode(x.Result.Title) ?? int.MaxValue).ToList();

        const int maxShown = 20;
        var sb = new StringBuilder();
        var seasonLabel = season.HasValue ? $"Stagione {season}" : "senza stagione";
        var resLabel = resolution is not null ? $" — {resolution}" : "";
        var langLabel = language is not null ? $" — {language}" : "";
        sb.AppendLine($"🔎 **{siteName} — \"{query}\" — {seasonLabel}{resLabel}{langLabel}:**\n");

        int shown = 0;
        foreach (var (index, r) in orderedItems)
        {
            if (shown >= maxShown) break;
            var episode = ParseEpisode(r.Title);
            var epLabel = episode.HasValue ? $"🎬 Ep. {episode} — " : "";
            var title = r.Title.Length > 60 ? r.Title.Substring(0, 57) + "..." : r.Title;
            sb.AppendLine($"**{index + 1}.** {epLabel}`{title}`");
            sb.AppendLine($"📦 {r.SizeText ?? "?"} | 🌱 {r.SeedsText ?? "?"}");
            if (!string.IsNullOrWhiteSpace(r.SourceListName))
                sb.AppendLine($"📂 {r.SourceListName}");
            sb.AppendLine($"👉 /r\\_{index} | 📌 /watchadd\\_{index}");
            sb.AppendLine();
            shown++;
        }

        if (items.Count > shown)
            sb.AppendLine($"...e altri {items.Count - shown} risultati non mostrati.");

        await _bot.SendMessage(new ChatId(chatId), sb.ToString(), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
    }

    // Salva un dump JSON di ogni ricerca in locale, per poterlo rianalizzare/rielaborare in sessioni future.
    private async Task SaveSearchResultsJsonAsync(string query, string siteName, List<TorrentSearchResult> results, CancellationToken ct)
    {
        try
        {
            var dir = AppPaths.SearchHistory;

            var safeName = string.Concat($"{query}_{siteName}".Select(c => char.IsLetterOrDigit(c) ? c : '-'));
            var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}.json";

            var payload = results.Select(r =>
            {
                var (season, resolution) = ParseSeasonAndResolution(r.Title);
                return new
                {
                    r.SiteName,
                    r.Title,
                    Season = season,
                    Resolution = resolution,
                    r.SizeText,
                    r.SeedsText,
                    r.SeedsNumeric,
                    r.Magnet,
                    r.DetailUrl,
                    r.SourceListName
                };
            });

            var json = JsonSerializer.Serialize(
                new { Query = query, Site = siteName, SearchedAt = DateTime.Now, Results = payload },
                new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(Path.Combine(dir, fileName), json, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Impossibile salvare il JSON della ricerca per query \"{Query}\"", query);
        }
    }

    private async Task HandleSearchSelectionAsync(long chatId, int index, CancellationToken ct)
    {
        try
        {
            if (!_lastSearchResults.TryGetValue(chatId, out var results) || index < 0 || index >= results.Count)
            {
                await _bot.SendMessage(new ChatId(chatId), "⚠️ Risultato non trovato. Rifai una ricerca con /search.", cancellationToken: ct);
                return;
            }

            var result = results[index];

            List<string> magnets = result.Magnet is not null ? new List<string> { result.Magnet } : new List<string>();

            if (magnets.Count == 0 && result.DetailUrl is not null && !string.IsNullOrWhiteSpace(result.MagnetSelectorOnDetailPage))
            {
                // Tentativo di risoluzione di tutti i magnet dalla pagina di dettaglio
                // (un topic può contenere più episodi/versioni, non solo il primo trovato).
                magnets = await _search.ResolveMagnetsFromDetailPageAsync(
                    result.DetailUrl, result.MagnetSelectorOnDetailPage, result.Cookie, _searchCfg.TimeoutSeconds, ct);
            }

            if (magnets.Count == 0)
            {
                var fallbackLink = result.DetailUrl;
                if (string.IsNullOrWhiteSpace(fallbackLink))
                {
                    await _bot.SendMessage(new ChatId(chatId), "❌ Impossibile ottenere un link scaricabile per questo risultato.", cancellationToken: ct);
                    return;
                }

                _pendingLinks[chatId] = fallbackLink;
                await SendDestinationPromptAsync(chatId, result.Title, ct);
                return;
            }

            if (magnets.Count == 1)
            {
                _pendingLinks[chatId] = magnets[0];
                await SendDestinationPromptAsync(chatId, result.Title, ct);
                _log.LogInformation("➡️ Chat {ChatId}: selezionato risultato ricerca #{Index} ({Title})", chatId, index, result.Title);
                return;
            }

            // Più magnet trovati sullo stesso topic: fai scegliere all'utente quale scaricare.
            _lastMagnetChoices[chatId] = magnets;

            var sb = new StringBuilder();
            sb.AppendLine($"📦 Trovati **{magnets.Count}** file/versioni per `{result.Title}`. Scegli quale scaricare:\n");

            for (int i = 0; i < magnets.Count; i++)
            {
                var name = ExtractFileNameFromMagnet(magnets[i]) ?? $"File {i + 1}";
                sb.AppendLine($"**{i + 1}.** `{name}`");
                sb.AppendLine($"👉 /rm\\_{i}");
                sb.AppendLine();
            }

            await _bot.SendMessage(new ChatId(chatId), sb.ToString(), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
            _log.LogInformation("➡️ Chat {ChatId}: risultato ricerca #{Index} ({Title}) ha {Count} magnet, in attesa di scelta", chatId, index, result.Title, magnets.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore durante /r_{Index} per chat {ChatId}", index, chatId);
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task HandleMagnetChoiceSelectionAsync(long chatId, int index, CancellationToken ct)
    {
        if (!_lastMagnetChoices.TryGetValue(chatId, out var magnets) || index < 0 || index >= magnets.Count)
        {
            await _bot.SendMessage(new ChatId(chatId), "⚠️ Scelta non trovata. Riseleziona il risultato con /r_N.", cancellationToken: ct);
            return;
        }

        _pendingLinks[chatId] = magnets[index];
        await SendDestinationPromptAsync(chatId, ExtractFileNameFromMagnet(magnets[index]) ?? "file selezionato", ct);
    }

    private async Task SendDestinationPromptAsync(long chatId, string title, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new []
            {
                InlineKeyboardButton.WithCallbackData("🎬 Movies", "dest_movies"),
                InlineKeyboardButton.WithCallbackData("📺 TV", "dest_tv")
            }
        });

        await _bot.SendMessage(new ChatId(chatId),
            $"Hai selezionato: `{title}`\nIn quale cartella vuoi salvare?",
            parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    /// <summary>
    /// Se il sito lo richiede e la gestione automatica NordVPN è attiva, connette e attende
    /// una verifica reale di connettività (non solo lo stato dell'adattatore) prima di
    /// tornare, informando l'utente su Telegram dei due passaggi — altrimenti una ricerca
    /// avviata subito dopo la connessione rischia di partire mentre il tunnel non instrada
    /// ancora traffico, restituendo "nessun risultato" per un problema di timing, non di dati.
    /// </summary>
    private async Task EnsureVpnConnectedAsync(long chatId, bool required, CancellationToken ct)
    {
        if (!required || !_vpn.IsEnabled) return;

        await SendMessageAfterVpnToggleAsync(chatId, "🔒 Connessione a NordVPN in corso...", ct);
        var ok = await _vpn.ConnectAsync(ct);
        await SendMessageAfterVpnToggleAsync(chatId,
            ok ? "✅ VPN connessa, avvio la ricerca…" : "⚠️ Non sono riuscito a verificare la VPN entro il timeout, provo comunque...",
            ct);
    }

    // La primissima richiesta Telegram subito dopo un cambio di stato VPN è quella più esposta a
    // una connessione HTTP rimasta "appesa" sulla rete precedente (vedi commento sul costruttore
    // di TelegramBotClient): un singolo retry qui copre il caso raro in cui il pool di connessioni
    // non abbia ancora scartato quella vecchia.
    private async Task SendMessageAfterVpnToggleAsync(long chatId, string text, CancellationToken ct)
    {
        try
        {
            await _bot.SendMessage(new ChatId(chatId), text, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is Telegram.Bot.Exceptions.RequestException or HttpRequestException)
        {
            _log.LogWarning(ex, "⚠️ Invio messaggio fallito subito dopo un cambio di stato VPN, riprovo una volta");
            await Task.Delay(1000, ct);
            await _bot.SendMessage(new ChatId(chatId), text, cancellationToken: ct);
        }
    }

    /// <summary>
    /// Se la gestione automatica NordVPN è attiva, disconnette e attende conferma prima di
    /// tornare, informando l'utente — stesso motivo di <see cref="EnsureVpnConnectedAsync"/>
    /// ma al contrario: un download avviato mentre la disconnessione è ancora in corso
    /// potrebbe passare comunque dal tunnel per qualche istante.
    /// </summary>
    private async Task EnsureVpnDisconnectedAsync(long chatId, CancellationToken ct)
    {
        if (!_vpn.IsEnabled) return;

        await SendMessageAfterVpnToggleAsync(chatId, "🔓 Disconnessione da NordVPN in corso...", ct);
        var ok = await _vpn.DisconnectAsync(ct);
        await SendMessageAfterVpnToggleAsync(chatId,
            ok ? "✅ VPN disconnessa, scarico alla velocità diretta…" : "⚠️ Non sono riuscito a confermare la disconnessione VPN, procedo comunque...",
            ct);
    }

    private async Task HandleLinkAsync(long chatId, string text, CancellationToken ct)
    {
        try
        {
            _log.LogInformation("➡️ Inizio elaborazione link per chat {ChatId}: {Text}", chatId, text);

            // Il download vero passa dai server di AllDebrid, non dal sito di origine: disconnette
            // la VPN (se gestita in automatico) per scaricare alla piena velocità della linea diretta.
            await EnsureVpnDisconnectedAsync(chatId, ct);

            await _bot.SendMessage(new ChatId(chatId), "⏳ Elaboro…", cancellationToken: ct);

            var toTv = _destTv.TryGetValue(chatId, out var v) && v;

            if (text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                // Upload magnet
                var id = await _ad.UploadMagnetAsync(text, ct);
                _log.LogInformation("Magnet caricato (id={Id})", id);

                await _bot.SendMessage(new ChatId(chatId),
                    "🧲 Magnet caricato, controllo disponibilità…",
                    cancellationToken: ct);

                var ready = await _ad.WaitReadyAsync(id, TimeSpan.FromMinutes(60), ct);
                if (!ready)
                {
                    await _bot.SendMessage(new ChatId(chatId),
                        "⚠️ Magnet non pronto in tempo. Annullato.",
                        cancellationToken: ct);

                    await _ad.DeleteMagnetAsync(id, ct);
                    _log.LogWarning("Magnet {Id} non pronto in tempo, cancellato", id);
                    return;
                }

                // Determina tipo magnet e routing
                var files = await _ad.GetMagnetFilesDetailedAsync(id, ct);

                DownloadResult result;
                if (files.Count == 1)
                {
                    await _bot.SendMessage(new ChatId(chatId),
                        $"🔓 Trovato 1 file, inizio download…",
                        cancellationToken: ct);
                    result = await HandleDirectLinkAsync(chatId, files[0].Link, toTv, ct, files[0].Name);
                }
                else if (files.Count > 1)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"📂 **Trovati {files.Count} file.** Seleziona cosa scaricare:\n");
                    sb.AppendLine($"👉 **SCARICA TUTTI**: /dlall\\_{id}\n");
                    
                    for(int i = 0; i < files.Count && i < 30; i++)
                    {
                        sb.AppendLine($"📄 `{files[i].Name}` ({FormatFileSize(files[i].Size)})");
                        sb.AppendLine($"👉 /dlf\\_{id}\\_{i}\n");
                    }

                    if (files.Count > 30) {
                        sb.AppendLine($"...e altri {files.Count - 30} file.");
                    }

                    await _bot.SendMessage(new ChatId(chatId), sb.ToString(), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
                }
            }
            else if (text.StartsWith("dl:", StringComparison.OrdinalIgnoreCase))
            {
                var id = text.Substring(3);
                _log.LogInformation("Avvio download da ID esistente: {Id}", id);
                
                var files = await _ad.GetMagnetFilesDetailedAsync(id, ct);

                if (files.Count == 1)
                {
                    await _bot.SendMessage(new ChatId(chatId),
                        $"🔓 Trovato 1 file per ID {id}, inizio download…",
                        cancellationToken: ct);
                    await HandleDirectLinkAsync(chatId, files[0].Link, toTv, ct, files[0].Name);
                }
                else if (files.Count > 1)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"📂 **Trovati {files.Count} file per ID {id}.** Seleziona cosa scaricare:\n");
                    sb.AppendLine($"👉 **SCARICA TUTTI**: /dlall\\_{id}\n");
                    
                    for(int i = 0; i < files.Count && i < 30; i++)
                    {
                        sb.AppendLine($"📄 `{files[i].Name}` ({FormatFileSize(files[i].Size)})");
                        sb.AppendLine($"👉 /dlf\\_{id}\\_{i}\n");
                    }

                    if (files.Count > 30) {
                        sb.AppendLine($"...e altri {files.Count - 30} file.");
                    }

                    await _bot.SendMessage(new ChatId(chatId), sb.ToString(), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
                }
            }
            else
            {
                // Link diretto
                var result = await HandleDirectLinkAsync(chatId, text, toTv, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore in HandleLinkAsync per chat {ChatId}", chatId);
            await _bot.SendMessage(new ChatId(chatId),
                $"❌ Errore: {ex.Message}",
                cancellationToken: ct);
        }
    }

    private async Task<DownloadResult> HandleDirectLinkAsync(long chatId, string url, bool toTv, CancellationToken ct, string? overrideFileName = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new DownloadResult();

        try
        {
            var direct = await _ad.UnlockLinkAsync(url, ct);
            _log.LogInformation("Link diretto sbloccato: {Direct}", direct);

            var fileName = overrideFileName ?? Path.GetFileName(new Uri(direct).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "File sconosciuto";
            result.MainFileName = fileName;

            var downloadMessage = await _bot.SendMessage(
                new ChatId(chatId),
                $"📥 **Download iniziato:**\n`{fileName}`\n\n⬜⬜⬜⬜⬜ 0%",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            TelegramProgressNotifier.Setup(chatId, downloadMessage.MessageId, fileName, _bot);

            var (savedPath, fileSize) = await _down.SaveOneAsync(direct, toTv, ct);
            result.SavedFiles.Add(savedPath);
            result.FileSizes[fileName] = fileSize;
            result.TotalBytes += fileSize;
            result.Success = true;

            var extractOk = await _extractor.ExtractIfArchiveAsync(savedPath, _bot, chatId, downloadMessage.MessageId, ct);
            if (extractOk)
                await _plex.RefreshAsync(toTv, ct);

            var targetFolder = toTv ? "📺 Serie TV" : "🎬 Movies";
            var successMessage =
                $"✅ **Download completato!**\n\n" +
                $"📁 **File:** `{fileName}`\n" +
                $"📂 **Cartella:** {targetFolder}\n" +
                $"📄 **Percorso file:** `{savedPath}`\n" +
                $"📦 **Dimensione:** {FormatFileSize(fileSize)}\n" +
                $"⏱️ **Tempo:** {sw.Elapsed.TotalSeconds:n1}s\n" +
                (extractOk ? "🔄 **Plex:** Aggiornamento avviato" : "⚠️ **Plex:** Refresh saltato (estrazione archivio fallita)");

            await _bot.EditMessageText(
                new ChatId(chatId),
                downloadMessage.MessageId,
                successMessage,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            _log.LogInformation("Completato link diretto: {File} in {Sec:n1}s", savedPath, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add(ex.Message);
            throw;
        }
        finally
        {
            result.ElapsedTime = sw.Elapsed;
            sw.Stop();
            if (result.MainFileName is not null)
                TelegramProgressNotifier.Reset(result.MainFileName);
        }

        return result;
    }

    private async Task<DownloadResult> HandleMagnetSingleAsync(long chatId, string magnetId, string magnetText, bool toTv, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new DownloadResult();

        try
        {
            var links = await _ad.GetMagnetLinksAsync(magnetId, ct);
            _log.LogInformation("Magnet {Id} → {Count} link trovati", magnetId, links.Count);

            if (links.Count == 0)
            {
                await _bot.SendMessage(new ChatId(chatId), "❌ Nessun file trovato nel magnet.", cancellationToken: ct);
                await _ad.DeleteMagnetAsync(magnetId, ct);
                result.Success = false;
                result.Errors.Add("Nessun file trovato nel magnet");
                return result;
            }

            _log.LogInformation("🔓 Sbloccando link singolo: {Link}", links[0]);
            var directLink = await _ad.UnlockLinkAsync(links[0], ct);

            var fileName = ExtractFileNameFromMagnet(magnetText) ?? "File sconosciuto";
            result.MainFileName = fileName;

            var downloadMessage = await _bot.SendMessage(
                new ChatId(chatId),
                $"📥 **Download iniziato:**\n`{fileName}`\n\n⬜⬜⬜⬜⬜ 0%",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            TelegramProgressNotifier.Setup(chatId, downloadMessage.MessageId, fileName, _bot);

            var (savedPath, fileSize) = await _down.SaveOneAsync(directLink, toTv, ct);
            result.SavedFiles.Add(savedPath);
            result.TotalBytes += fileSize;

            // usa un filename coerente con ciò che hai salvato
            var displayFileName = Path.GetFileName(savedPath);
            result.FileSizes[displayFileName] = fileSize;

            var extractOk = await _extractor.ExtractIfArchiveAsync(savedPath, _bot, chatId, downloadMessage.MessageId, ct);
            if (extractOk)
                await _plex.RefreshAsync(toTv, ct);

            var targetFolder = toTv ? "📺 Serie TV" : "🎬 Movies";
            var successMessage =
                $"✅ **Download completato!**\n\n" +
                $"📁 **File:** `{fileName}`\n" +
                $"📂 **Cartella:** {targetFolder}\n" +
                $"📄 **Percorso:** `{savedPath}`\n" +
                $"📦 **Dimensione:** {FormatFileSize(fileSize)}\n" +
                $"⏱️ **Tempo:** {sw.Elapsed.TotalSeconds:n1}s\n" +
                (extractOk ? "🔄 **Plex:** Aggiornamento avviato" : "⚠️ **Plex:** Refresh saltato (estrazione archivio fallita)");

            await _bot.EditMessageText(
                new ChatId(chatId),
                downloadMessage.MessageId,
                successMessage,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            _log.LogInformation("Completato magnet singolo {Id}: 1 file salvato in {Sec:n1}s", magnetId, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add(ex.Message);
            _log.LogError(ex, "❌ Errore durante download singolo");
            await _bot.SendMessage(new ChatId(chatId), $"❌ Errore: {ex.Message}", cancellationToken: ct);
        }
        finally
        {
            result.ElapsedTime = sw.Elapsed;
            sw.Stop();
            if (result.MainFileName is not null)
                TelegramProgressNotifier.Reset(result.MainFileName);
        }

        return result;
    }

    private async Task<DownloadResult> HandleMagnetBatchAsync(long chatId, string magnetId, string magnetText, bool toTv, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new DownloadResult();
        result.MainFileName = ExtractFileNameFromMagnet(magnetText);

        try
        {
            var files = await _ad.GetMagnetFilesDetailedAsync(magnetId, ct);
            _log.LogInformation("Magnet {Id} → {Count} file trovati", magnetId, files.Count);

            if (files.Count == 0)
            {
                await _bot.SendMessage(new ChatId(chatId), "❌ Nessun file trovato nel magnet.", cancellationToken: ct);
                await _ad.DeleteMagnetAsync(magnetId, ct);
                result.Success = false;
                result.Errors.Add("Nessun file trovato nel magnet");
                return result;
            }

            // Messaggio di riepilogo iniziale
            var summaryMessage = await _bot.SendMessage(
                new ChatId(chatId),
                $"📥 **Download batch iniziato:**\n`{files.Count} file da scaricare`\n\n🔄 Preparazione...",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            var fileMessages = new List<int>();

            // FASE 1: Crea messaggi individuali per ogni file
            for (int i = 0; i < files.Count; i++)
            {
                var fileMessage = await _bot.SendMessage(
                    new ChatId(chatId),
                    $"📥 **File {i + 1}/{files.Count}**\n`Preparazione...`\n\n⬜⬜⬜⬜⬜ 0%",
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                    cancellationToken: ct);

                fileMessages.Add(fileMessage.MessageId);
            }

            // FASE 2: Processa ogni file con il suo messaggio dedicato
            for (int i = 0; i < files.Count; i++)
            {
                await ProcessSingleBatchFileAsync(chatId, files[i], i, files.Count, fileMessages[i], summaryMessage.MessageId, toTv, result, ct);
            }

            // Estrazione archivi
            await ExtractBatchArchivesAsync(chatId, result.SavedFiles, summaryMessage.MessageId, ct);

            // Aggiorna Plex solo se almeno un file è stato effettivamente salvato
            // (evita un refresh a vuoto quando l'intero batch è fallito).
            if (result.SavedFiles.Count > 0)
                await _plex.RefreshAsync(toTv, ct);

            // Messaggio finale
            var targetFolder = toTv ? "📺 Serie TV" : "🎬 Movies";
            var finalSummary = $"🎉 **Batch completato!**\n\n" +
                $"📁 **Magnet:** `{result.MainFileName ?? "Unknown"}`\n" +
                $"📂 **Cartella:** {targetFolder}\n" +
                $"📄 **File salvati:** {result.SavedFiles.Count}/{files.Count}\n" +
                $"⏱️ **Tempo totale:** {sw.Elapsed.TotalSeconds:n1}s\n" +
                $"🔄 **Plex:** Aggiornamento avviato";

            if (result.Errors.Count > 0)
            {
                finalSummary += $"\n\n⚠️ **Errori:** {result.Errors.Count} file falliti";
            }

            await _bot.EditMessageText(
                new ChatId(chatId),
                summaryMessage.MessageId,
                finalSummary,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            result.Success = result.SavedFiles.Count > 0;
            _log.LogInformation("Completato magnet batch {Id}: {Count}/{Total} file salvati in {Sec:n1}s",
                magnetId, result.SavedFiles.Count, files.Count, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add(ex.Message);
            throw;
        }
        finally
        {
            result.ElapsedTime = sw.Elapsed;
            sw.Stop();
        }

        return result;
    }

    /// <summary>
    /// Sblocca, scarica e riporta l'esito di un singolo file di un magnet batch, aggiornando
    /// il suo messaggio dedicato e il riepilogo. Estratto da HandleMagnetBatchAsync per leggibilità.
    /// </summary>
    private async Task ProcessSingleBatchFileAsync(
        long chatId, MagnetFile file, int index, int totalFiles, int fileMessageId, int summaryMessageId,
        bool toTv, DownloadResult result, CancellationToken ct)
    {
        var link = file.Link;
        string? fileName = null;

        try
        {
            _log.LogInformation("🔓 Processing file {Index}/{Total}: {Link}", index + 1, totalFiles, link);

            await _bot.EditMessageText(
                new ChatId(chatId),
                fileMessageId,
                $"📥 **File {index + 1}/{totalFiles}**\n`Sbloccaggio link...`\n\n🔄 In corso...",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            var directLink = await _ad.UnlockLinkAsync(link, ct);
            fileName = string.IsNullOrWhiteSpace(file.Name) ? $"File_{index + 1}" : file.Name;

            await _bot.EditMessageText(
                new ChatId(chatId),
                fileMessageId,
                $"📥 **File {index + 1}/{totalFiles}:**\n`{fileName}`\n\n⬜⬜⬜⬜⬜ 0%",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            TelegramProgressNotifier.Setup(chatId, fileMessageId, fileName, _bot);

            var (savedPath, fileSize) = await _down.SaveOneAsync(directLink, toTv, ct);
            result.SavedFiles.Add(savedPath);
            result.FileSizes[fileName] = fileSize;
            result.TotalBytes += fileSize;

            var perFileMsg = BuildFileCompletedMessage(fileName, savedPath, fileSize, toTv, seconds: 0);

            await _bot.EditMessageText(
                new ChatId(chatId),
                fileMessageId,
                perFileMsg,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            _log.LogInformation("✅ File {Index}/{Total} completato: {Path}", index + 1, totalFiles, savedPath);

            await _bot.EditMessageText(
                new ChatId(chatId),
                summaryMessageId,
                $"📥 **Download batch in corso:**\n`Completati: {index + 1}/{totalFiles}`\n\n✅ File scaricati correttamente",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore processing file {Index}: {Link}", index + 1, link);
            result.Errors.Add($"File {index + 1}: {ex.Message}");

            await _bot.EditMessageText(
                new ChatId(chatId),
                fileMessageId,
                $"❌ **File {index + 1}/{totalFiles} fallito!**\n`Errore durante download`\n\n🔴 {ex.Message}",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            await Task.Delay(1000, ct);
        }
        finally
        {
            if (fileName is not null)
                TelegramProgressNotifier.Reset(fileName);
        }
    }

    /// <summary>
    /// Estrae gli archivi (.zip/.rar) tra i file salvati di un batch, aggiornando il messaggio
    /// di riepilogo. Estratto da HandleMagnetBatchAsync per leggibilità.
    /// </summary>
    private async Task ExtractBatchArchivesAsync(long chatId, List<string> savedFiles, int summaryMessageId, CancellationToken ct)
    {
        if (savedFiles.Count == 0) return;

        await _bot.EditMessageText(
            new ChatId(chatId),
            summaryMessageId,
            $"📦 **Estrazione archivi...**\n`{savedFiles.Count} file da processare`\n\n🔄 Controllo archivi compressi...",
            parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
            cancellationToken: ct);

        foreach (var file in savedFiles)
        {
            try
            {
                await _extractor.ExtractIfArchiveAsync(file, _bot, chatId, summaryMessageId, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Errore estrazione {File}", file);
            }
        }
    }

    /// <summary>
    /// Valida un link incollato manualmente dall'utente prima di accettarlo: un magnet deve
    /// avere un hash BitTorrent valido, un URL deve essere http/https e non puntare a un host
    /// locale (mitiga SSRF verso servizi interni tramite AllDebrid come proxy involontario).
    /// </summary>
    private static bool IsValidLink(string link)
    {
        if (string.IsNullOrWhiteSpace(link)) return false;

        if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return link.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("127.", StringComparison.Ordinal) ||
            host == "0.0.0.0" ||
            host == "::1")
            return false;

        return true;
    }

    /// <summary>
    /// Controlla se esiste già in libreria un file/episodio simile a <paramref name="title"/>.
    /// Restituisce null se non trova nulla di sospetto, altrimenti un messaggio di avviso da mostrare all'utente.
    /// Best-effort: confronto per parole normalizzate (rimuove tag come [1080p], anni, lingua, ecc.), non un match esatto.
    /// </summary>
    private string? CheckForDuplicate(string title, bool toTv)
    {
        try
        {
            if (toTv)
            {
                if (string.IsNullOrWhiteSpace(_paths.Tv) || !Directory.Exists(_paths.Tv)) return null;

                var normalizedTitle = NormalizeTitle(title);
                var episodeCode = ExtractEpisodeCode(title);

                var matchingShowDir = Directory.GetDirectories(_paths.Tv)
                    .FirstOrDefault(d => TitlesLikelyMatch(normalizedTitle, NormalizeTitle(Path.GetFileName(d))));

                if (matchingShowDir is null) return null;

                var existingFiles = Directory.GetFiles(matchingShowDir, "*", SearchOption.AllDirectories);
                var showName = Path.GetFileName(matchingShowDir);

                if (episodeCode is not null)
                {
                    var codeStr = $"S{episodeCode.Value.Season:D2}E{episodeCode.Value.Episode:D2}";
                    var alreadyHave = existingFiles.Any(f => f.Contains(codeStr, StringComparison.OrdinalIgnoreCase));
                    return alreadyHave
                        ? $"⚠️ Hai già un file per **{codeStr}** in \"{showName}\"."
                        : null;
                }

                return existingFiles.Length > 0
                    ? $"⚠️ Hai già {existingFiles.Length} file per la serie \"{showName}\" — controlla che questo download non si sovrapponga."
                    : null;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_paths.Movies) || !Directory.Exists(_paths.Movies)) return null;

                var normalizedTitle = NormalizeTitle(title);
                if (normalizedTitle.Length < 4) return null; // troppo corto per un confronto affidabile

                var match = Directory.GetFiles(_paths.Movies)
                    .FirstOrDefault(f => TitlesLikelyMatch(normalizedTitle, NormalizeTitle(Path.GetFileNameWithoutExtension(f))));

                return match is not null
                    ? $"⚠️ Trovato un file simile già scaricato: `{Path.GetFileName(match)}`."
                    : null;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore durante il controllo duplicati per \"{Title}\"", title);
            return null;
        }
    }

    private static string NormalizeTitle(string raw)
    {
        var s = raw.ToLowerInvariant();
        s = Regex.Replace(s, @"\[[^\]]*\]", " ");   // [1080p], [G66], [COMPLETA]...
        s = Regex.Replace(s, @"\([^)]*\)", " ");    // (2024 ITA/ENG)
        s = Regex.Replace(s, @"[._]", " ");
        s = Regex.Replace(s, @"[^a-z0-9\s]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static (int Season, int Episode)? ExtractEpisodeCode(string title)
    {
        var m = Regex.Match(title, @"S(\d{1,2})E(\d{1,2})", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    /// <summary>
    /// Confronto approssimativo per parole: vero se almeno il 70% delle parole del titolo più
    /// corto compare anche nell'altro. Tollera differenze di ordine/tag extra tra i due nomi.
    /// </summary>
    private static bool TitlesLikelyMatch(string normalizedA, string normalizedB)
    {
        var wordsA = normalizedA.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToHashSet();
        var wordsB = normalizedB.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToHashSet();
        if (wordsA.Count == 0 || wordsB.Count == 0) return false;

        var shorter = wordsA.Count <= wordsB.Count ? wordsA : wordsB;
        var longer = wordsA.Count <= wordsB.Count ? wordsB : wordsA;

        var overlap = shorter.Count(w => longer.Contains(w));
        return (double)overlap / shorter.Count >= 0.7;
    }

    private static string? ExtractFileNameFromMagnet(string magnetLink)
    {
        try
        {
            var dnIndex = magnetLink.IndexOf("dn=", StringComparison.OrdinalIgnoreCase);
            if (dnIndex == -1) return null;

            var start = dnIndex + 3;
            var end = magnetLink.IndexOf("&", start);
            if (end == -1) end = magnetLink.Length;

            var fileName = magnetLink.Substring(start, end - start);
            return Uri.UnescapeDataString(fileName);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes == 0) return "0 B";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unitIndex = 0;
        double size = bytes;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:F1} {units[unitIndex]}";
    }

    private static string BuildFileCompletedMessage(
    string fileName,
    string savedPath,
    long fileSizeBytes,
    bool toTv,
    double seconds)
    {
        var targetFolder = toTv ? "📺 Serie TV" : "🎬 Movies";
        return
            "✅ **Download completato!**\n\n" +
            $"📁 **File:** `{fileName}`\n" +
            $"📂 **Cartella:** {targetFolder}\n" +
            $"📄 **Percorso:** `{savedPath}`\n" +
            $"📦 **Dimensione:** {FormatFileSize(fileSizeBytes)}\n" +
            $"⏱️ **Tempo:** {seconds:n1}s\n" +
            "🔄 **Plex:** Aggiornamento avviato";
    }

}