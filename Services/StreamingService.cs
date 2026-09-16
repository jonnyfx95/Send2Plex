using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Una traccia audio del file sorgente (da ffprobe) — Index è l'indice RELATIVO alle sole tracce
/// audio (0, 1, 2…), lo stesso numero che ffmpeg si aspetta in "-map 0:a:N" (StreamingService.
/// BuildStreamArgs), non l'indice assoluto dello stream nel contenitore.
/// </summary>
public sealed record AudioTrackInfo(int Index, string? Language, string? CodecName, int? Channels, string? Title);

/// <summary>
/// Streaming diretto (senza scaricare su disco): risolve il link AllDebrid del file richiesto e lo
/// serve al player passando sempre da FFmpeg (mai un proxy byte-a-byte puro, anche quando i codec
/// sono già compatibili) — stesso schema validato nei prototipi (docs/piano-streaming-diretto.md):
/// remux "copy" (video+audio invariati, solo cambio contenitore) come primo tentativo, economico e
/// spesso sufficiente sulla TV WebOS reale (HEVC 10-bit nativo); trascodifica completa solo se il
/// player segnala un errore sul remux (gestito lato client in Watch.razor), con encoder/qualità
/// configurabili e fallback automatico a libx264 se l'encoder scelto non si apre.
/// </summary>
public partial class StreamingService
{
    private readonly DebridProviderFactory _debridFactory;
    private readonly ConfigStore _configStore;
    private readonly ILogger<StreamingService> _log;

    private StreamingSettings _cfg => _configStore.Current.Streaming;
    private PathSettings _paths => _configStore.Current.Paths;

    // Percorso noto dell'installazione winget su questa macchina: usato come fallback deterministico
    // quando "ffmpeg"/"ffprobe" non sono risolvibili dal PATH del processo (winget aggiorna il PATH
    // di sistema, ma un processo già avviato non lo rilegge finché non riparte).
    private const string KnownFfmpegPath =
        @"C:\Users\jonny\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe";

    // Livello H.264 imposto esplicitamente ad ogni trascodifica video (vedi BuildStreamArgs) —
    // 5.1 = 0x33, copre qualunque risoluzione fino al 4K. High profile = 0x64 (100). Insieme danno
    // sempre lo stesso codec string MSE "avc1.640033", indipendentemente dal file sorgente.
    private const string H264Level51 = "5.1";
    public const string Avc1HighLevel51CodecString = "avc1.640033";
    public const string Mp4aAacLcCodecString = "mp4a.40.2";

    // ----------------------------------------------------
    // Ottimizzazione streaming (docs/piano-ottimizzazione-streaming.md, Fasi 1-2) — vedi
    // StreamSession più sotto per il disegno completo. In breve: ffmpeg non scrive più byte-a-byte
    // direttamente sulla risposta HTTP della singola richiesta, ma su un buffer condiviso
    // (System.IO.Pipelines.Pipe) che sopravvive alla disconnessione del client per una finestra di
    // grazia — un reload del watchdog anti-stallo lato TV può quindi riagganciarsi al processo
    // ffmpeg già in esecuzione invece di doverne avviare uno nuovo da zero (nuovo probe, nuovo
    // link AllDebrid, nuova inizializzazione hwaccel: nei log reali osservato costare diversi
    // secondi, e in un caso il riavvio stesso è fallito, richiedendone un secondo a cascata).
    // ----------------------------------------------------

    // Dimensionamento del buffer: bitrate reali osservati nei log 3-17 Mbit/s (picco teorico 40
    // Mbit/s per qualità "alta"). Raddoppiato da 40MB/20MB a 80MB/40MB (2026-09-13, sessione reale
    // "Operazione Speciale: Lioness" S03E01): con lo stallo lato consegna-alla-TV ormai risolto
    // (fix risparmio energetico NIC, stessa sessione), gli stalli residui osservati erano tutti
    // "IN LETTURA da AllDebrid" (log `🐌 lettura da ffmpeg lenta`/`buffer vuoto`) — intoppi di
    // pochi secondi nella consegna di AllDebrid a ffmpeg, ricorrenti ogni 5-10 minuti. Il margine
    // vecchio (40MB) copriva solo ~8s a bitrate di picco; raddoppiato per dare più scorta da
    // consumare durante un intoppo prima che diventi uno stallo visibile e un reload lato player.
    private const long PipeBufferPauseBytes = 80L * 1024 * 1024;
    private const long PipeBufferResumeBytes = 40L * 1024 * 1024;

    // Quanto resta vivo un processo ffmpeg dopo che l'ultimo client si è disconnesso, in attesa di
    // un eventuale riaggancio, prima di essere chiuso per davvero.
    private static readonly TimeSpan SessionDetachGrace = TimeSpan.FromSeconds(30);

    // Ampiezza del bucket per l'offset di partenza nella chiave di sessione: due richieste sullo
    // stesso file/qualità entro questa distanza sono considerate "la stessa visione" e riusano la
    // sessione esistente invece di aprirne una nuova.
    private const double SessionStartBucketSeconds = 10.0;

    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();

    // Lock globale (non per-chiave) sulla fase di setup di una sessione (unlock AllDebrid + probe +
    // avvio ffmpeg): scelta deliberatamente semplice invece di un lock per-chiave, accettabile
    // perché l'app è pensata per un solo spettatore alla volta sulla rete di casa — nel raro caso di
    // due richieste di file diversi in contemporanea, la seconda aspetta che la prima finisca il
    // setup (non lo streaming vero e proprio) invece di partire in parallelo.
    private readonly SemaphoreSlim _sessionSetupGate = new(1, 1);

    public StreamingService(DebridProviderFactory debridFactory, ConfigStore configStore, ILogger<StreamingService> log)
    {
        _debridFactory = debridFactory;
        _configStore = configStore;
        _log = log;
    }

    // ----------------------------------------------------
    // Endpoint (registrato in Program.cs)
    // ----------------------------------------------------
    public static Task HandleAsync(HttpContext http, StreamingService svc) => svc.StreamAsync(http, http.RequestAborted);

    public async Task StreamAsync(HttpContext http, CancellationToken ct)
    {
        var req = http.Request;
        var fileLink = req.Query["link"].ToString();
        if (string.IsNullOrWhiteSpace(fileLink))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Parametro 'link' mancante.", ct);
            return;
        }

        var mode = req.Query["mode"].ToString();
        if (string.IsNullOrWhiteSpace(mode)) mode = "copy";
        var isCopyMode = string.Equals(mode, "copy", StringComparison.OrdinalIgnoreCase);
        // BUG REALE (regressione): l'app webOS chiede sempre mode=transcode per garantire un
        // percorso MSE prevedibile, ma IsVideoCompatible sceglieva comunque "-c:v copy" per
        // contenuto H.264 8-bit già compatibile (es. "Oceania" 1080p) — in quel caso il client,
        // vedendo videoEncoded=false da GetStreamInfoAsync, tornava al <video src> diretto:
        // esattamente il percorso storicamente inaffidabile su webOS che tutta la pipeline MSE
        // doveva eliminare. forceEncode bypassa del tutto la scelta "copy", garantendo sempre il
        // ramo ricodificato/MSE per chi lo richiede esplicitamente (solo l'app TV).
        var forceEncode = string.Equals(req.Query["forceEncode"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

        var startSeconds = double.TryParse(req.Query["start"], NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0;

        var quality = req.Query["quality"].ToString();
        if (string.IsNullOrWhiteSpace(quality)) quality = _cfg.Quality;

        var encoderSetting = req.Query["encoder"].ToString();
        if (string.IsNullOrWhiteSpace(encoderSetting)) encoderSetting = _cfg.Encoder;

        // "local": il file è già sul disco (Paths.Movies/Tv, scaricato in precedenza da Send2Plex)
        // — si salta del tutto lo sblocco debrid e si passa il percorso locale direttamente a
        // ffmpeg come input, dopo averlo validato contro le cartelle configurate (nessuna cartella
        // arbitraria: questo endpoint non ha autenticazione, vedi ResolveLocalPath).
        var providerRaw = req.Query["provider"].ToString();
        var isLocal = string.Equals(providerRaw, "local", StringComparison.OrdinalIgnoreCase);
        var provider = ParseProvider(providerRaw);

        // Traccia audio scelta dal player (nuovo selettore, restyle 2026-09-14) — indice relativo
        // alle sole tracce audio, come richiesto da "-map 0:a:N" in BuildStreamArgs. Il valore
        // vero contro il numero di tracce disponibili si verifica dopo il probe, in
        // CreateSessionLockedAsync: qui un valore assente/malformato ricade semplicemente su 0.
        var audioIndex = int.TryParse(req.Query["audioIndex"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ai) && ai >= 0 ? ai : 0;

        // Motivo del (ri)caricamento, passato dal client (2026-09-14, richiesta utente: capire dai
        // log se una sequenza di "nuova sessione" ravvicinate è un salto utente reale o il watchdog
        // anti-stallo che ricarica da solo — prima erano indistinguibili, si poteva solo indovinare
        // dal timing). "sconosciuto" per build client vecchie che non mandano ancora il parametro.
        var reason = req.Query["reason"].ToString();
        if (string.IsNullOrWhiteSpace(reason)) reason = "sconosciuto";

        // Bypass ffmpeg per i file locali già riproducibili così come sono (2026-09-14, richiesta
        // utente: "lo streaming in locale si ferma spesso" — ricerca su Jellyfin: la loro
        // differenza vera per questo caso non è la segmentazione HLS (già provata e abbandonata su
        // questa TV, vedi piano-ottimizzazione-streaming.md), è che per un file locale già
        // compatibile non passano affatto da un encoder: servono il file così com'è con il
        // supporto Range nativo del framework. Vale solo per mode=copy senza forceEncode esplicito
        // (lo stesso caso che oggi tenta comunque prima "-c:v copy") e traccia audio di default
        // (audioIndex 0): un file servito così com'è porta con sé qualunque traccia il player
        // nativo scelga di default, non si può forzarne una diversa senza un encoder di mezzo.
        if (isLocal && isCopyMode && !forceEncode && audioIndex == 0)
        {
            var directResult = await TryBuildLocalDirectPlayResultAsync(fileLink, ct);
            if (directResult is not null)
            {
                await directResult.ExecuteAsync(http);
                return;
            }
        }

        http.Response.ContentType = "video/mp4";
        http.Response.Headers.CacheControl = "no-store";
        // Esplicito invece di lasciarlo ambiguo: questo endpoint non supporta affatto le richieste
        // di range (lo stream è generato al volo, senza accesso casuale ai byte) — dichiararlo
        // evita che un player più aggressivo (osservato sul browser di webOS) tenti comunque una
        // richiesta Range a metà streaming aspettandosi di riprendere da un offset, cosa che
        // manderebbe fuori sincrono il player rispetto a un nuovo processo ffmpeg ripartito da zero.
        http.Response.Headers.AcceptRanges = "none";

        // Chiave di sessione (piano-ottimizzazione-streaming.md): file + modalità/qualità + un
        // bucket di 10s sull'offset di partenza. Due richieste ravvicinate sullo stesso punto della
        // stessa visione (es. il watchdog anti-stallo che ricarica) riusano la stessa sessione/lo
        // stesso processo ffmpeg invece di aprirne uno nuovo da zero.
        var sessionKey = BuildSessionKey(fileLink, mode, quality, forceEncode, startSeconds, isLocal ? "local" : provider.ToString(), audioIndex);

        StreamSession? session;
        await _sessionSetupGate.WaitAsync(ct);
        try
        {
            session = GetReattachableSessionLocked(sessionKey);
            session ??= await CreateSessionLockedAsync(http, sessionKey, fileLink, mode, isCopyMode, quality, encoderSetting, forceEncode, startSeconds, provider, isLocal, audioIndex, reason, ct);
        }
        finally
        {
            _sessionSetupGate.Release();
        }

        if (session is null) return; // errore già risposto (unlock fallito) o fallback ffmpeg esauriti

        await ConsumeSessionAsync(session, http, ct);
    }

    // Bucket di SessionStartBucketSeconds sull'offset richiesto: due richieste entro quella
    // distanza sono considerate "la stessa visione" (vedi commento sopra in StreamAsync).
    private static string BuildSessionKey(string link, string mode, string quality, bool forceEncode, double startSeconds, string providerTag, int audioIndex)
    {
        var bucket = Math.Round(startSeconds / SessionStartBucketSeconds, MidpointRounding.AwayFromZero) * SessionStartBucketSeconds;
        // audioIndex nella chiave: cambiare traccia deve aprire una sessione (processo ffmpeg)
        // nuova, mai riagganciarsi a una già in corso sulla traccia precedente.
        return $"{link}|{mode}|{quality}|{forceEncode}|{bucket:F0}|{providerTag}|a{audioIndex}";
    }

    // BUG REALE trovato con un test dal vevo (2026-09-13): questa era una seconda copia del
    // parsing provider, rimasta a 2 vie quando è stato aggiunto Premiumize altrove (TvApiEndpoints,
    // Search.razor, ecc.) — "provider=premiumize" nella query di /stream ricadeva silenziosamente
    // su AllDebrid invece di usare PremiumizeClient. Ora delega al punto unico condiviso
    // (DebridProviderNames.ParseApiValue, Models/DebridProvider.cs) invece di avere una propria
    // logica — "local" (file già sul disco) resta gestito a parte in StreamAsync, non passa da qui.
    private static DebridProvider ParseProvider(string? value) => DebridProviderNames.ParseApiValue(value);

    // Conferma che il percorso locale richiesto ricada davvero dentro una delle cartelle Plex
    // configurate (Movies/TV, incluse le eventuali cartelle per-libreria) prima di darlo in pasto a
    // ffmpeg — questo endpoint non ha autenticazione (rete domestica fidata, vedi
    // piano-streaming-diretto.md), quindi senza questo controllo un client sulla rete potrebbe
    // chiedere di leggere un file arbitrario del disco (es. appsettings.json). Torna il percorso
    // canonico se valido ed esistente, altrimenti null.
    private string? ResolveLocalPath(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath)) return null;

        string full;
        try { full = Path.GetFullPath(candidatePath); }
        catch { return null; }

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(_paths.Movies)) roots.Add(_paths.Movies);
        if (!string.IsNullOrWhiteSpace(_paths.Tv)) roots.Add(_paths.Tv);
        roots.AddRange(_paths.MovieFolders.Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)));
        roots.AddRange(_paths.TvFolders.Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)));

        var contained = roots.Any(root =>
        {
            try { return full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });

        return contained && File.Exists(full) ? full : null;
    }

    // Nome del file dall'URL sbloccato (l'ultimo segmento del path, es. "Lanterns.S01E01...mkv") —
    // usato solo per dare un nome leggibile al log dedicato, non per la logica di streaming.
    private static string? ExtractFileName(string url)
    {
        try
        {
            var path = new Uri(url).LocalPath;
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? null : Uri.UnescapeDataString(name);
        }
        catch
        {
            return null;
        }
    }

    // Log dedicato per singolo file in streaming (richiesta utente): un file per titolo+giorno in
    // logs/streams/, per isolare più facilmente stalli/problemi di qualità senza dover scorrere
    // l'intero log applicativo (Blazor/SignalR/Telegram/tutto il resto). "Best effort": se il file
    // non si apre per qualunque motivo, lo streaming prosegue comunque solo sul log applicativo.
    private sealed class StreamFileLogger : IDisposable
    {
        private readonly StreamWriter? _writer;

        public StreamFileLogger(string fileName)
        {
            try
            {
                var dir = Path.Combine(AppPaths.Logs, "streams");
                Directory.CreateDirectory(dir);

                var invalid = Path.GetInvalidFileNameChars();
                var safeName = new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
                if (safeName.Length > 120) safeName = safeName[..120];

                var path = Path.Combine(dir, $"{safeName}_{DateTime.Now:yyyyMMdd}.log");
                // BUG REALE (Fase 2): con le sessioni disaccoppiate, una vecchia sessione dello
                // STESSO file può restare aperta nella finestra di grazia (fino a 30s) mentre una
                // nuova sessione per un'altra posizione dello stesso episodio viene già creata —
                // "new StreamWriter(path, append)" apre di default con FileShare.Read, quindi il
                // secondo StreamFileLogger falliva ad aprirsi (eccezione silenziosa, vedi sotto) e
                // per quella sessione il log dedicato restava vuoto, proprio quando serviva di più
                // per capire cosa fosse successo. FileShare.ReadWrite permette a più
                // StreamFileLogger dello stesso file/giorno di scrivere in append concorrentemente.
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream) { AutoFlush = true };
            }
            catch
            {
                _writer = null;
            }
        }

        public void Log(string message)
        {
            try { _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}"); }
            catch { /* best-effort */ }
        }

        public void Dispose() => _writer?.Dispose();
    }

    // Eventi play/pausa loggati dal client (app.js) nello stesso file per-file/giorno usato dal
    // resto della sessione ffmpeg — senza questo, una pausa volontaria dell'utente e uno stallo
    // vero in lettura producono nei log server la stessa identica firma (currentTime fermo, buffer
    // pieno, ffmpeg che frena), rendendo ogni diagnosi sui blocchi ambigua (vedi
    // docs/idee-miglioramento-webos.md, sessione 2026-09-14). Istanzia un logger a parte invece di
    // riusare quello della sessione attiva: FileShare.ReadWrite lo rende già sicuro in scrittura
    // concorrente (vedi commento su StreamFileLogger) e non serve inseguire la sessione per link.
    public void LogClientPlaybackEvent(string link, string eventName, double positionSeconds)
    {
        var fileName = ExtractFileName(link) ?? "sconosciuto";
        using var log = new StreamFileLogger(fileName);
        log.Log($"👤 client: {eventName} a {positionSeconds:n1}s");
    }

    // ----------------------------------------------------
    // Info preventiva (per l'app WebOS: sapere PRIMA se il video verrà copiato o ricodificato,
    // per scegliere se usare Media Source Extensions — che richiede di conoscere in anticipo lo
    // string codec esatto — oppure il tag <video> "semplice" per il caso copy, dove il codec resta
    // quello originale del sorgente e non è prevedibile a priori).
    // ----------------------------------------------------

    public record StreamInfo(bool VideoEncoded, string? MimeCodecs, string? Error, double? DurationSeconds = null, bool DirectFile = false);

    // Estensioni di contenitore che <video> apre nativamente senza remux — un file con codec
    // compatibili ma contenitore diverso (es. .mkv) NON qualifica: servirlo così com'è
    // funzionerebbe per i byte ma il tag <video> non sa aprire quel contenitore, servirebbe
    // comunque un remux (quindi ffmpeg), non un vero bypass.
    private static readonly HashSet<string> DirectPlayContainerExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };

    // Verifica STRETTA (a differenza di IsVideoCompatible/tryVideoCopy, che è ottimista e lascia a
    // ffmpeg il compito di accorgersi a runtime se "-c:v copy" fallisce): qui non c'è alcun
    // encoder di riserva, quindi serve sapere con certezza che il file sia già riproducibile così
    // com'è, non solo "probabilmente sì". Contenitore + codec video H.264 8-bit + (se presente)
    // audio già AAC — esattamente i requisiti di un vero "DirectPlay" (non "DirectStream"/remux).
    private static bool IsEligibleForLocalDirectPlay(string path, ProbeInfo probe)
    {
        if (!DirectPlayContainerExtensions.Contains(Path.GetExtension(path))) return false;
        if (!string.Equals(probe.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase) || probe.Is10Bit) return false;
        if (probe.AudioTracks.Count > 0 && !string.Equals(probe.AudioTracks[0].CodecName, "aac", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // Usata sia da StreamAsync (per servire davvero il file) sia da GetStreamInfoAsync (per dirlo
    // in anticipo al player, che deve sapere PRIMA di caricare se aspettarsi un file statico con
    // Range vero invece del solito flusso ffmpeg). Ritorna il ProbeInfo insieme all'esito perché
    // entrambi i chiamanti ne hanno comunque bisogno subito dopo (durata per l'uno, niente per
    // l'altro, ma evita un secondo probe nello stesso posto).
    private async Task<(bool Eligible, ProbeInfo Probe)> CheckLocalDirectPlayAsync(string resolvedPath, CancellationToken ct)
    {
        ProbeInfo probe;
        try { probe = await ProbeAsync(resolvedPath, ResolveTool("ffprobe.exe"), ct); }
        catch { return (false, ProbeInfo.Unknown); }
        return (IsEligibleForLocalDirectPlay(resolvedPath, probe), probe);
    }

    // Costruisce il risultato HTTP per il bypass (PhysicalFileResult-equivalente con Range
    // abilitato, stesso pattern del "DirectPlay" di Jellyfin — vedi ricerca 2026-09-14) se il file
    // è idoneo, altrimenti null: il chiamante (StreamAsync) ricade sulla pipeline ffmpeg esistente.
    private async Task<IResult?> TryBuildLocalDirectPlayResultAsync(string fileLink, CancellationToken ct)
    {
        var resolved = ResolveLocalPath(fileLink);
        if (resolved is null) return null;

        var (eligible, _) = await CheckLocalDirectPlayAsync(resolved, ct);
        if (!eligible) return null;

        var contentType = string.Equals(Path.GetExtension(resolved), ".mov", StringComparison.OrdinalIgnoreCase)
            ? "video/quicktime" : "video/mp4";
        return Results.File(resolved, contentType, enableRangeProcessing: true);
    }

    public async Task<StreamInfo> GetStreamInfoAsync(string fileLink, string mode, string quality, bool forceEncode, DebridProvider provider, bool isLocal, int audioIndex, CancellationToken ct)
    {
        string directUrl;
        if (isLocal)
        {
            var resolved = ResolveLocalPath(fileLink);
            if (resolved is null)
                return new StreamInfo(false, null, "File locale non trovato (o fuori dalle cartelle Movies/TV configurate).");
            directUrl = resolved;
        }
        else
        {
            try
            {
                directUrl = await _debridFactory.Get(provider).UnlockLinkAsync(fileLink, ct);
            }
            catch (Exception ex)
            {
                return new StreamInfo(false, null, $"Impossibile sbloccare il link: {ex.Message}");
            }
        }

        var isCopyRequest = !forceEncode && string.Equals(mode, "copy", StringComparison.OrdinalIgnoreCase);

        // Il probe qui sotto normalmente si salta del tutto in modalità copy (vedi commento poco
        // più giù) — ma per un file LOCALE candidato al bypass serve comunque sapere se è idoneo
        // PRIMA che il player scelga come caricare lo stream. Costa un probe in più anche nel
        // percorso "veloce", ma solo per i file locali: un ffprobe sul disco della stessa macchina
        // costa millisecondi, non i secondi di un link debrid remoto — il percorso veloce per i
        // link non locali resta invariato, vedi il primo "return" subito sotto per quel caso.
        if (isLocal && isCopyRequest && audioIndex == 0)
        {
            var (eligible, localProbe) = await CheckLocalDirectPlayAsync(directUrl, ct);
            if (eligible) return new StreamInfo(false, null, null, localProbe.DurationSeconds, DirectFile: true);

            // Trascodifica "intelligente" per codec (2026-09-14, docs/idee-miglioramento-webos.md):
            // il probe qui sopra è già stato fatto per il controllo del bypass, quindi se conferma
            // che il video non è H.264 va riusato invece di lasciare che il player scopra il
            // problema da solo — oggi (sotto) IsVideoCompatible con tryVideoCopy=true prova sempre
            // "-c:v copy" a prescindere dal codec, quindi ogni file HEVC della libreria spreca un
            // ciclo intero (diretta -> errore lato player -> recupero -> ritenta in trascodifica)
            // ad ogni riproduzione. Gate SOLO sul codec confermato dal probe, mai su ipotesi di
            // risoluzione/bit depth (richiesta esplicita: il probe potrebbe comunque sbagliarsi su
            // cosa il player accetta in HEVC diretto su alcune TV, quindi non disattivare il
            // tentativo per quei casi — solo quando è certo che il codec non è nemmeno H.264).
            if (!string.IsNullOrEmpty(localProbe.VideoCodec) && !string.Equals(localProbe.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
            {
                return new StreamInfo(true, $"{Avc1HighLevel51CodecString}, {Mp4aAacLcCodecString}", null, localProbe.DurationSeconds);
            }
        }

        // In modalità "copy" (senza forceEncode) il video non viene mai ricodificato dal server per
        // questa richiesta (BuildStreamArgs forza sempre -c:v copy quando tryVideoCopy=true) —
        // nessun bisogno di probe per saperlo.
        if (isCopyRequest)
            return new StreamInfo(false, null, null);

        ProbeInfo probe;
        try
        {
            probe = await ProbeAsync(directUrl, ResolveTool("ffprobe.exe"), ct);
        }
        catch
        {
            probe = ProbeInfo.Unknown; // sconosciuto -> verrà ricodificato per sicurezza
        }

        // BUG REALE (regressione): usava sempre la qualità di default del server (_cfg.Quality)
        // invece di quella effettivamente richiesta dal client — potendo differire, la previsione
        // di videoEncoded/scaleFilter poteva non corrispondere a quella usata dalla vera richiesta
        // /stream, che invece rispetta la qualità del client.
        var videoEncoded = !IsVideoCompatible(probe, tryVideoCopy: false, scaleFilter: QualityPreset(quality).ScaleFilter, forceEncode);
        return new StreamInfo(
            videoEncoded,
            videoEncoded ? $"{Avc1HighLevel51CodecString}, {Mp4aAacLcCodecString}" : null,
            null,
            probe.DurationSeconds);
    }

    // ----------------------------------------------------
    // Costruzione argomenti FFmpeg
    // ----------------------------------------------------

    // tryVideoCopy=true (modalità "copy" richiesta dal client): prova -c:v copy a prescindere dal
    // probe, per lasciare che sia il player a dire se il codec/profilo va bene (varia da device a
    // device). tryVideoCopy=false (fallback dopo un errore, o modalità "transcode" esplicita):
    // decide in base al probe, copiando solo se davvero già H.264 8-bit alla risoluzione voluta.
    // L'audio invece segue SEMPRE il probe, in entrambi i casi (vedi commento in StreamAsync).
    // asHls: piano-streaming-multiutente-hls.md, Fase 2 — riusa TUTTA la logica di selezione
    // codec/qualità già validata per il pipe MP4 esistente, cambiando SOLO il muxer di output
    // (AddHlsOutput invece di AddOutput). Deliberatamente NON cambia nient'altro (niente
    // passthrough audio multicanale via TS, niente bypass DirectPlay) per isolare i bug del nuovo
    // trasporto da qualunque altro comportamento — stesso principio già seguito per Fase 2 vs
    // Fase 3 nel piano.
    private static List<string> BuildStreamArgs(string url, double startSeconds, ProbeInfo probe, string quality, string encoderName, bool tryVideoCopy, bool forceEncode = false, int audioIndex = 0, bool asHls = false)
    {
        // La traccia scelta può avere un codec diverso da probe.AudioCodec (che è sempre quello
        // della PRIMA traccia, per compatibilità con il resto di questo metodo) — la decisione
        // "copy" vs trascodifica sotto deve guardare il codec REALE della traccia richiesta,
        // altrimenti selezionare una traccia diversa dalla prima con un codec diverso userebbe
        // ancora la valutazione della traccia 0.
        var selectedAudioCodec = audioIndex >= 0 && audioIndex < probe.AudioTracks.Count
            ? probe.AudioTracks[audioIndex].CodecName
            : probe.AudioCodec;
        var (cq, scaleFilter, maxrate, bufsize) = QualityPreset(quality);
        var videoCompatible = IsVideoCompatible(probe, tryVideoCopy, scaleFilter, forceEncode);

        // "-progress pipe:2 -stats_period 5": telemetria periodica (speed/out_time) sullo stesso
        // stderr già letto riga per riga — senza questa, i log server non permettono di distinguere
        // un vero encode-sotto-tempo-reale (colpa della pipeline CPU-bound di tone-mapping) da un
        // problema di consegna rete verso il player (in quel caso l'encode risulterebbe comunque a
        // velocità normale). Aggiunta dopo un test reale (Lanterns S01E01, più blocchi/riprese
        // durante la visione) in cui i soli log esistenti non bastavano a capire la causa.
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "warning", "-stats_period", "5", "-progress", "pipe:2" };
        if (!videoCompatible && (encoderName == "h264_nvenc" || encoderName == "hevc_nvenc"))
        {
            // BUG REALE DI PERFORMANCE (secondo, oltre al mancato scale-prima-del-tonemap): la
            // decodifica dell'input restava su CPU nonostante NVENC fosse disponibile — per
            // sorgenti HEVC 10-bit 4K (il caso HDR tipico) decodificare via software costa MOLTO
            // più della catena di filtri di tonemap stessa, ed è la causa dominante del throughput
            // sub-realtime misurato (~0.48x anche dopo aver limitato la risoluzione del tonemap).
            // Con la stessa GPU NVIDIA usata per l'encode (NVENC) è disponibile anche il decoder
            // hardware (NVDEC): "-hwaccel cuda" lo attiva mantenendo l'output in memoria di sistema
            // (nessun hwaccel_output_format cuda), così la catena di filtri CPU (zscale/tonemap)
            // continua a funzionare invariata sui frame decodificati.
            args.Add("-hwaccel"); args.Add("cuda");
        }
        AddSeek(args, startSeconds);
        args.Add("-i"); args.Add(url);
        args.Add("-map"); args.Add("0:v:0");
        args.Add("-map"); args.Add($"0:a:{audioIndex}?");

        if (videoCompatible)
        {
            // Solo per il remux "copy" (ancora servito come <video src> diretto, non MSE):
            // -avoid_negative_ts make_zero evita timestamp negativi dopo un seek.
            args.Add("-avoid_negative_ts"); args.Add("make_zero");
            args.Add("-c:v"); args.Add("copy");
        }
        else
        {
            // "disabled" è pensato per il pipe MP4 esistente (CTS con B-frame in un flusso
            // fMP4 unico e continuo, vedi commento in AddOutput) — per HLS lasciato al default di
            // ffmpeg invece di riusarlo alla cieca (piano-streaming-multiutente-hls.md, Fase 2:
            // la causa reale del "Format error"/DEMUXER_ERROR_COULD_NOT_PARSE del player nativo si
            // è poi rivelata essere altro — segmenti MPEG-TS invece di fMP4, e playlist ancora
            // aperta senza ENDLIST — ma questa esclusione fa comunque parte della configurazione
            // HLS confermata funzionante sulla TV vera, non toccarla senza riverificare).
            if (!asHls) { args.Add("-avoid_negative_ts"); args.Add("disabled"); }

            var vf = BuildVideoFilter(probe.Is10Bit, probe.IsHdr, scaleFilter);
            if (vf is not null) { args.Add("-vf"); args.Add(vf); }

            args.Add("-c:v"); args.Add(encoderName);
            args.AddRange(EncoderPresetArgs(encoderName));
            args.AddRange(EncoderRateArgs(encoderName, cq, maxrate, bufsize));
            // Profilo/livello H.264 fissi (non lasciati decidere all'encoder in base a risoluzione/
            // bitrate): serve perché il player WebOS ora usa Media Source Extensions per i flussi
            // trascodificati (vedi Watch/app.js), che richiede di dichiarare in anticipo lo string
            // codec esatto (es. "avc1.640033") — se cambiasse da un file all'altro non potremmo
            // saperlo prima di aver già iniziato a leggere lo stream. High@5.1 copre fino al 4K.
            args.Add("-profile:v"); args.Add("high");
            args.Add("-level:v"); args.Add(H264Level51);
            // GOP corto e fisso — BUG REALE trovato analizzando byte-per-byte l'output: senza
            // questo, NVENC (nessun -g esplicito) non inserisce keyframe periodici, quindi
            // "frag_keyframe" non produce MAI un secondo frammento fMP4 — tutto il contenuto
            // finisce in un moof+mdat unico e sempre più grande. Se il processo si interrompe per
            // qualunque motivo (anche solo il player che chiude la connessione) mentre quel
            // frammento gigante è ancora in scrittura, il trun dichiara più campioni di quanti
            // byte siano realmente presenti — il demuxer MSE lo rifiuta in blocco con
            // "CHUNK_DEMUXER_ERROR_APPEND_FAILED: stream parsing failed" (riprodotto anche in
            // Chrome desktop scaricando l'output ed ispezionandolo a livello di box MP4). Un GOP
            // di 48 frame (~2s) rende ogni frammento piccolo, frequente e quasi innocuo da perdere.
            args.Add("-g"); args.Add("48");
            args.Add("-keyint_min"); args.Add("48");
            args.Add("-sc_threshold"); args.Add("0");
        }

        if (!videoCompatible)
        {
            // CAUSA REALE del CHUNK_DEMUXER_ERROR_APPEND_FAILED (trovata per esclusione, testando
            // localmente file sintetici e reali via appendBuffer in Chrome): non erano l'edit-list
            // né i metadati HDR residui (mdcv/clli) — il parser MSE di Chrome rifiuta l'INIT SEGMENT
            // stesso quando la traccia audio AAC ha più di 2 canali (es. 5.1). Confermato: lo stesso
            // identico file, con solo l'audio ridotto a stereo, viene accettato da appendBuffer senza
            // errori. Per il ramo MSE quindi l'audio va SEMPRE ricodificato in AAC stereo, anche se
            // la sorgente è già AAC (niente "copy": servirebbe comunque il downmix dei canali).
            args.Add("-ac"); args.Add("2");
            args.Add("-c:a"); args.Add("aac"); args.Add("-b:a"); args.Add("192k");
        }
        else if (string.Equals(selectedAudioCodec, "aac", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-c:a"); args.Add("copy");
        }
        else
        {
            // BUG REALE (trovato con un test reale, log per-episodio: ffmpeg smetteva di produrre
            // byte pur restando vivo — battiti di "-progress" ancora regolari, nessun avanzamento
            // reale — subito dopo l'avviso "[mp4] track 1: codec frame size is not set"): avevo
            // esteso "-c:a copy" a QUALUNQUE codec nel percorso diretto per far passare Dolby Atmos
            // (dentro E-AC-3/TrueHD), ma copiare E-AC-3 in un MP4 frammentato mette in crisi il
            // muxer di ffmpeg in un modo che non è un semplice errore rilevabile (FailedBeforeAnyBytes
            // non scatta: il processo resta vivo, bloccato) — la catena di fallback esistente non
            // ha modo di accorgersene e recuperare. Tornati alla trascodifica sicura per qualunque
            // codec diverso da AAC, come prima di quel tentativo: si perde il passthrough Atmos, ma
            // il video (Dolby Vision incluso) resta comunque intatto via -c:v copy sopra. Un vero
            // passthrough Atmos affidabile richiederebbe un contenitore diverso (es. MPEG-TS, che
            // supporta E-AC-3/TrueHD in modo nativo) — cambio di architettura non banale, non fatto
            // qui dopo le difficoltà già viste con TS/HLS in questa stessa app.
            args.Add("-ac"); args.Add("2");
            args.Add("-c:a"); args.Add("aac"); args.Add("-b:a"); args.Add("192k");
        }

        if (asHls) AddHlsOutput(args); else AddOutput(args);
        return args;
    }

    private static bool IsVideoCompatible(ProbeInfo probe, bool tryVideoCopy, string? scaleFilter, bool forceEncode = false) =>
        !forceEncode &&
        (tryVideoCopy ||
        (string.Equals(probe.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase) && !probe.Is10Bit && scaleFilter is null));

    private static void AddSeek(List<string> args, double startSeconds)
    {
        // -ss va messo PRIMA di -i (seek "di input"): messo dopo è fino a 100 volte più lento
        // perché FFmpeg decodificherebbe e scarterebbe tutto quello che precede invece di saltarci
        // direttamente (verificato nei prototipi, vedi docs/piano-streaming-diretto.md).
        if (startSeconds > 0.01)
        {
            args.Add("-ss");
            args.Add(startSeconds.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddOutput(List<string> args)
    {
        args.Add("-f"); args.Add("mp4");
        // delay_moov è indispensabile con alcune tracce audio (es. AC-3): senza, ffmpeg rifiuta di
        // scrivere l'header con "Cannot write moov atom before AC3 packets" perché certi parametri
        // del codec non sono noti finché non arrivano i primi pacchetti (osservato in pratica sul
        // primo file reale testato con questa pipeline).
        //
        // negative_cts_offsets — BUG REALE (trovato con un test reale: "The Chosen" in DirectPlay
        // rifiutato dal player quasi istantaneamente, loop di riconnessione ogni ~165ms, non uno
        // stallo): questo flag era applicato SOLO al ramo trascodifica/MSE ("resta la scelta
        // corretta per fMP4/MSE", pensato per il bug del CHUNK_DEMUXER_ERROR_APPEND_FAILED — che in
        // realtà era l'audio multicanale, vedi BuildStreamArgs). Ma serve a rappresentare
        // correttamente gli offset di composizione (CTS) quando il video ha B-frame riordinati —
        // vero per QUALUNQUE fMP4 frammentato con B-frame, non solo per MSE. Il ramo copy/DirectPlay
        // copia il video ESATTAMENTE come codificato nel file sorgente (quasi sempre con B-frame,
        // a differenza delle nostre trascodifiche dove lo controlliamo noi) — senza questo flag il
        // file frammentato risultante aveva CTS non validi per il demuxer nativo di webOS, che lo
        // rifiutava subito. Applicato ora SEMPRE, non solo per MSE.
        var movflags = "frag_keyframe+empty_moov+delay_moov+default_base_moof+negative_cts_offsets";
        args.Add("-movflags"); args.Add(movflags);
        args.Add("pipe:1");
    }

    // piano-streaming-multiutente-hls.md, Fase 2. Segmenti fMP4 (NON MPEG-TS — il tentativo
    // abbandonato il 2026-09-13 aveva concluso il contrario, MAI verificato davvero sulla TV:
    // il motore nativo webOS/Chromium 132 rifiuta il TS con DEMUXER_ERROR_COULD_NOT_PARSE anche
    // quando è perfettamente valido per ffmpeg/ffprobe, ma accetta fMP4 senza problemi,
    // confermato sia in locale sia sulla TV vera). La playlist SERVITA al player non è quella che
    // ffmpeg scrive qui su disco — il server ne dichiara una sintetica e già chiusa fin dalla
    // prima richiesta (BuildFullPlaylist in StreamingService.Hls.cs, usa la durata nota da
    // ffprobe): una playlist "live"/ancora aperta non veniva proprio riprodotta dal player nativo,
    // né in locale né sulla TV, indipendentemente da questo `-hls_playlist_type`. Il chiamante
    // imposta SEMPRE ProcessStartInfo.WorkingDirectory sulla cartella di sessione, quindi qui SOLO
    // nomi relativi — bug #1/#2 del tentativo precedente (percorso assoluto scritto su disco ma
    // non anche nel manifesto, o viceversa) evitati per costruzione.
    private static void AddHlsOutput(List<string> args)
    {
        args.Add("-f"); args.Add("hls");
        args.Add("-hls_time"); args.Add("2");
        // Nessun -hls_playlist_type qui (né "event" né "vod") — testato esplicitamente CON
        // "event" e SENZA, stesso risultato in entrambi i casi: non è quel tag a decidere se il
        // player nativo riproduce o no (vedi sotto per la causa vera). Il file playlist.m3u8 che
        // ffmpeg scrive qui non viene comunque mai letto dal server: la playlist SERVITA al
        // player è sintetica, costruita da BuildFullPlaylist in StreamingService.Hls.cs.
        //
        // BUG REALE trovato nello spike Fase 1 (Services/HlsSpike.cs, poi rimosso): il default di
        // ffmpeg per hls_list_size è 5 — tronca la playlist alle sole ultime 5 segmenti,
        // comportamento pensato per streaming live. Ininfluente ora che la playlist servita è
        // sintetica, ma lasciato esplicito per chiarezza su cosa scrive ffmpeg sul proprio file.
        args.Add("-hls_list_size"); args.Add("0");
        // temp_file: ogni segmento viene scritto con nome temporaneo e rinominato solo a
        // scrittura completata — un client non può mai leggere un segmento a metà.
        args.Add("-hls_flags"); args.Add("temp_file");
        // BUG REALE, LA SCOPERTA PIÙ IMPORTANTE DI QUESTA FASE (2026-09-16): fMP4 invece di
        // MPEG-TS — sospetto opposto rispetto al tentativo abbandonato il 2026-09-13 (che aveva
        // concluso "TS più supportato di fMP4 sui motori nativi datati" e non l'aveva mai
        // verificato davvero sulla TV prima di arrendersi). Contenuto TS verificato pulito con
        // ffmpeg/ffprobe (decodifica senza il minimo avviso anche con -xerror) ma rifiutato dal
        // demuxer nativo Chromium (DEMUXER_ERROR_COULD_NOT_PARSE), riprodotto sia in locale sia su
        // Edge desktop sia sulla TV vera — MSE in Chrome non ha mai accettato "video/mp2t" in
        // MediaSource.isTypeSupported, solo fMP4/CMAF, e il player nativo webOS sembra basarsi
        // sulla stessa pipeline interna. Con fMP4, confermato funzionante sulla TV vera per un
        // intero episodio (piano-streaming-multiutente-hls.md, Fase 2). Non tornare a TS senza
        // una ragione forte e riverificata.
        args.Add("-hls_segment_type"); args.Add("fmp4");
        args.Add("-hls_fmp4_init_filename"); args.Add("init.mp4");
        args.Add("-hls_segment_filename"); args.Add("segment_%05d.m4s");
        args.Add("playlist.m3u8");
    }

    private static string? BuildVideoFilter(bool is10Bit, bool isHdr, string? scaleFilter)
    {
        var parts = new List<string>();

        if (isHdr)
        {
            // BUG REALE DI PERFORMANCE trovato misurando il throughput reale dello streaming
            // (curl con -w "%{time_total}", confrontato con la durata di contenuto prodotta
            // contando i frammenti fMP4): a piena risoluzione 4K questa catena di filtri CPU
            // (zscale+tonemap, non accelerata da NVENC) codifica a ~0.48x tempo reale — il buffer
            // lato player si scarica più svelto di quanto il server lo riempia, causando lo scatto
            // periodico ogni ~2s (un GOP) osservato in streaming. La scala va applicata PRIMA del
            // tonemap, non dopo come faceva prima (sprecava lavoro CPU tonemappando a piena
            // risoluzione anche per qualità "media"/"bassa" già pensate per uno scale minore): il
            // costo per pixel di zscale/tonemap cala con il quadrato del fattore di riduzione. Per
            // "alta" (nessuno scale esplicito) applichiamo comunque un cap a 1080p SOLO qui, solo
            // per l'HDR — il contenuto SDR nativo non ha questo collo di bottiglia e resta senza cap.
            var hdrScale = scaleFilter ?? "scale=-2:'min(1080,ih)'";
            parts.Add(hdrScale);
            // Tone-mapping HDR (PQ/HLG) -> SDR: senza questo filtro l'immagine risulta più piatta e
            // con tinta sbagliata (calda/verdastra invece che quella voluta dalla color grading
            // originale) — confermato su una scena scura reale, vedi ottavo prototipo nel piano.
            parts.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p");
        }
        else if (is10Bit)
        {
            // NVENC (e la maggior parte degli encoder H.264) non accetta input 10-bit: serve
            // riportarlo a 8-bit prima dell'encoder, altrimenti fallisce con "10 bit encode not
            // supported" (terzo prototipo).
            parts.Add("format=yuv420p");
            if (scaleFilter is not null) parts.Add(scaleFilter);
        }
        else if (scaleFilter is not null)
        {
            parts.Add(scaleFilter);
        }

        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    // Cap di risoluzione (solo verso il basso, mai upscale) + parametro di qualità costante per
    // ciascun livello scelto dall'utente (decisione di design, vedi piano). maxrate/bufsize sono un
    // tetto di sicurezza sui picchi (scene complesse), non il bitrate medio effettivo.
    private static (int Cq, string? ScaleFilter, string Maxrate, string Bufsize) QualityPreset(string quality) => quality?.ToLowerInvariant() switch
    {
        "bassa" => (26, "scale=-2:'min(720,ih)'", "4M", "8M"),
        "media" => (23, "scale=-2:'min(1080,ih)'", "12M", "24M"),
        _ => (19, null, "40M", "80M"), // "alta"
    };

    private static string[] EncoderPresetArgs(string encoderName) => encoderName switch
    {
        "h264_nvenc" or "hevc_nvenc" => new[] { "-preset", "p5" },
        "h264_qsv" or "hevc_qsv" => new[] { "-preset", "medium" },
        _ => new[] { "-preset", "veryfast" }
    };

    private static string[] EncoderRateArgs(string encoderName, int cq, string maxrate, string bufsize) => encoderName switch
    {
        // -rc vbr -cq X -b:v 0 (constant-quality con tetto massimo): senza istruzioni esplicite
        // NVENC sceglie un bitrate troppo basso per contenuti 4K (quarto/quinto prototipo).
        "h264_nvenc" or "hevc_nvenc" => new[] { "-rc", "vbr", "-cq", cq.ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-maxrate", maxrate, "-bufsize", bufsize },
        "h264_qsv" or "hevc_qsv" => new[] { "-global_quality", cq.ToString(CultureInfo.InvariantCulture), "-look_ahead", "0", "-maxrate", maxrate, "-bufsize", bufsize },
        _ => new[] { "-crf", cq.ToString(CultureInfo.InvariantCulture), "-maxrate", maxrate, "-bufsize", bufsize }
    };

    // ----------------------------------------------------
    // Rilevamento encoder ("auto")
    // ----------------------------------------------------

    private async Task<string> ResolveEncoderAsync(string configured, CancellationToken ct) => configured?.ToLowerInvariant() switch
    {
        "nvenc" => "h264_nvenc",
        "qsv" => "h264_qsv",
        "cpu" => "libx264",
        _ => await DetectHardwareEncoderAsync(ct)
    };

    private async Task<string> DetectHardwareEncoderAsync(CancellationToken ct)
    {
        // Solo nvidia-smi: è il segnale più affidabile e veloce (fallisce/torna subito se manca).
        // Niente rilevamento Intel via wmic — è deprecato su Windows 11 e in certi casi resta
        // appeso invece di fallire, con il rischio di bloccare l'intera richiesta di streaming per
        // decine di secondi (osservato in pratica). Chi ha hardware Intel può comunque selezionare
        // "qsv" esplicitamente nelle Impostazioni, bypassando questo rilevamento automatico.
        // --query-gpu invece della tabella completa: risposta quasi istantanea anche a sistema
        // sotto carico (es. un altro stream già in trascodifica), riduce il rischio che il timeout
        // scada per contesa della CPU e faccia ripiegare inutilmente su libx264 software.
        if (await CommandSucceedsAsync("nvidia-smi", new[] { "--query-gpu=name", "--format=csv,noheader" }, ct))
            return "h264_nvenc";
        return "libx264";
    }

    private static async Task<bool> CommandSucceedsAsync(string exe, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return false;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Il comando non ha risposto entro il timeout (es. eseguibile presente ma bloccato):
                // lo consideriamo assente invece di far aspettare la richiesta di streaming.
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ----------------------------------------------------
    // ffprobe
    // ----------------------------------------------------

    private readonly record struct ProbeInfo(string? VideoCodec, bool Is10Bit, bool IsHdr, string? AudioCodec, double? DurationSeconds, IReadOnlyList<AudioTrackInfo> AudioTracks)
    {
        public static readonly ProbeInfo Unknown = new(null, false, false, null, null, Array.Empty<AudioTrackInfo>());
    }

    private async Task<ProbeInfo> ProbeAsync(string url, string ffprobePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add("-show_format"); // serve solo per format.duration (barra di avanzamento lato client)
        psi.ArgumentList.Add(url);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Impossibile avviare ffprobe.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        // Timeout di sicurezza: ffprobe legge dalla rete (link AllDebrid), non da disco locale —
        // se il sorgente è lento/irraggiungibile non deve bloccare la richiesta di streaming a
        // tempo indeterminato. Allo scadere si passa a ProbeInfo.Unknown (trascodifica forzata e
        // sicura di video+audio), non un errore fatale.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("ffprobe non ha risposto entro 15 secondi.");
        }
        var stdout = await stdoutTask;

        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"ffprobe uscito con codice {proc.ExitCode}: {await stderrTask}");

        using var doc = JsonDocument.Parse(stdout);
        string? videoCodec = null, pixFmt = null, colorTransfer = null, audioCodec = null;
        var audioTracks = new List<AudioTrackInfo>();

        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            // Indice RELATIVO alle sole tracce audio (0, 1, 2…) — è quello che "-map 0:a:N" si
            // aspetta in BuildStreamArgs, non l'indice assoluto dello stream nel contenitore
            // (che ffprobe espone come "index" ma include anche video/sottotitoli/dati).
            var audioRelativeIndex = 0;
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && videoCodec is null)
                {
                    videoCodec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    pixFmt = stream.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() : null;
                    colorTransfer = stream.TryGetProperty("color_transfer", out var ctr) ? ctr.GetString() : null;
                }
                else if (type == "audio")
                {
                    var codecName = stream.TryGetProperty("codec_name", out var acn) ? acn.GetString() : null;
                    int? channels = stream.TryGetProperty("channels", out var chEl) && chEl.TryGetInt32(out var chVal) ? chVal : null;
                    string? language = null, title = null;
                    if (stream.TryGetProperty("tags", out var tags))
                    {
                        if (tags.TryGetProperty("language", out var langEl)) language = langEl.GetString();
                        if (tags.TryGetProperty("title", out var titleEl)) title = titleEl.GetString();
                    }
                    audioTracks.Add(new AudioTrackInfo(audioRelativeIndex, language, codecName, channels, title));
                    audioCodec ??= codecName; // compatibilità: probe.AudioCodec resta "la prima traccia", usato come default quando l'indice richiesto non è ancora noto
                    audioRelativeIndex++;
                }
            }
        }

        var is10Bit = pixFmt is not null && pixFmt.Contains("10", StringComparison.Ordinal);
        var isHdr = string.Equals(colorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(colorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase); // PQ o HLG

        double? durationSeconds = null;
        if (doc.RootElement.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durProp) &&
            double.TryParse(durProp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dur))
        {
            durationSeconds = dur;
        }

        return new ProbeInfo(videoCodec, is10Bit, isHdr, audioCodec, durationSeconds, audioTracks);
    }

    // Usato dal picker traccia audio lato client (nuovo pulsante nel player) — sonda il file PRIMA
    // ancora di sapere se lo streaming vero andrà in copy o trascodifica, chiamato in parallelo
    // all'avvio della riproduzione (mai a bloccarla: l'elenco tracce compare appena pronto, la
    // riproduzione parte comunque sulla traccia 0 di default come sempre). Duplica il probe che
    // CreateSessionLockedAsync fa comunque per la sessione vera — accettabile, stesso principio già
    // in uso in GetStreamInfoAsync più sotto (due probe indipendenti, non un caching cross-request).
    public async Task<IReadOnlyList<AudioTrackInfo>> GetAudioTracksAsync(string fileLink, DebridProvider provider, bool isLocal, CancellationToken ct)
    {
        string directUrl;
        if (isLocal)
        {
            var resolved = ResolveLocalPath(fileLink);
            if (resolved is null) return Array.Empty<AudioTrackInfo>();
            directUrl = resolved;
        }
        else
        {
            try { directUrl = await _debridFactory.Get(provider).UnlockLinkAsync(fileLink, ct); }
            catch { return Array.Empty<AudioTrackInfo>(); }
        }

        try
        {
            var probe = await ProbeAsync(directUrl, ResolveTool("ffprobe.exe"), ct);
            return probe.AudioTracks;
        }
        catch
        {
            return Array.Empty<AudioTrackInfo>();
        }
    }

    // ----------------------------------------------------
    // Sessione di streaming disaccoppiata dalla richiesta HTTP
    // (docs/piano-ottimizzazione-streaming.md, Fasi 1-2)
    // ----------------------------------------------------

    // FailedBeforeAnyBytes guida ancora la stessa catena di fallback di sempre (copy -> transcode
    // forzato -> libx264 software), applicata UNA VOLTA sola in fase di creazione della sessione.
    // Non esiste più "ClientDisconnected" qui: chi scrive sul Pipe (il producer, sotto) non sa più
    // nulla di "client" — la disconnessione di una singola richiesta HTTP è gestita interamente da
    // ConsumeSessionAsync, senza mai innescare un fallback di encoder (che qui non ha più modo di
    // scattare per quel motivo: la distinzione che serviva a evitarlo non serve più).
    private enum RunResult { Completed, FailedBeforeAnyBytes }

    private sealed class StreamSession
    {
        public required string Key { get; init; }
        public required string FileLink { get; init; }
        public required Pipe Pipe { get; init; }
        public required CancellationTokenSource SessionCts { get; init; }
        public required StreamFileLogger StreamLog { get; init; }
        public Process? CurrentProcess { get; set; }
        public Task? ProducerTask { get; set; }
        public bool Attached { get; set; }
        public CancellationTokenSource? DetachGraceCts { get; set; }
        public int RemovedFlag;
    }

    // Chiamato solo da CreateSessionLockedAsync, quindi sempre con _sessionSetupGate già acquisito
    // (niente race con un'altra creazione). RemoveSession è comunque protetta a parte da
    // Interlocked (RemovedFlag) contro una race col timer di grazia di una sessione già in scadenza
    // in background, quindi qui non serve altro lock.
    private void TerminateOtherSessionsForFileLocked(string fileLink, string keepKey)
    {
        foreach (var kvp in _sessions)
        {
            if (kvp.Key == keepKey || !string.Equals(kvp.Value.FileLink, fileLink, StringComparison.Ordinal)) continue;
            kvp.Value.StreamLog.Log("⏹️ terminata subito: una nuova sessione per lo stesso file ha preso il suo posto (niente più attesa dei 30s di grazia)");
            RemoveSession(kvp.Value, "superata da una nuova sessione per lo stesso file");
        }
    }

    private StreamSession? GetReattachableSessionLocked(string key)
    {
        if (_sessions.TryGetValue(key, out var existing) && !existing.Attached)
        {
            existing.DetachGraceCts?.Cancel();
            existing.DetachGraceCts?.Dispose();
            existing.DetachGraceCts = null;
            existing.Attached = true;
            existing.StreamLog.Log("↩️ richiesta riagganciata alla sessione esistente (nessun riavvio di ffmpeg)");
            _log.LogInformation("↩️ Streaming: richiesta riagganciata a una sessione esistente ({Key})", key);
            return existing;
        }
        return null;
    }

    // Chiamato SOLO con _sessionSetupGate già acquisito (niente sessioni duplicate per la stessa
    // chiave in una race tra due richieste quasi simultanee). Riproduce esattamente la stessa
    // catena di fallback di sempre (copy -> transcode forzato -> libx264), solo che l'ultimo
    // tentativo riuscito diventa il producer di una sessione di lunga durata invece di scrivere
    // direttamente sulla risposta di questa singola richiesta.
    private async Task<StreamSession?> CreateSessionLockedAsync(HttpContext http, string key, string fileLink, string mode, bool isCopyMode, string quality, string encoderSetting, bool forceEncode, double startSeconds, DebridProvider provider, bool isLocal, int audioIndex, string reason, CancellationToken ct)
    {
        // Un solo spettatore alla volta per file (vedi commento su _sessionSetupGate più sopra) — se
        // per lo STESSO file esiste già un'altra sessione con una chiave diversa (es. un bucket di
        // partenza diverso dopo un reload per stallo/seek/cambio qualità), quella vecchia è per
        // forza superata, mai un secondo spettatore reale. Terminarla SUBITO invece di lasciarla
        // scadere dopo i SessionDetachGrace (30s) evita che due processi ffmpeg leggano insieme lo
        // stesso file — osservato nei log reali (sessioni sovrapposte su un file locale, "Lioness"
        // S03E01, 2026-09-14): dannoso soprattutto su un disco con una sola testina (HDD USB), dove
        // letture concorrenti si intralciano a vicenda invece di sommarsi.
        TerminateOtherSessionsForFileLocked(fileLink, key);

        string directUrl;
        if (isLocal)
        {
            var resolved = ResolveLocalPath(fileLink);
            if (resolved is null)
            {
                _log.LogError("🎬 Streaming: percorso locale non valido o fuori dalle cartelle configurate: {Path}", fileLink);
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsync("File locale non trovato (o fuori dalle cartelle Movies/TV configurate).", ct);
                return null;
            }
            directUrl = resolved;
        }
        else
        {
            try
            {
                directUrl = await _debridFactory.Get(provider).UnlockLinkAsync(fileLink, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "🎬 Streaming: impossibile sbloccare il link del provider debrid");
                http.Response.StatusCode = StatusCodes.Status502BadGateway;
                await http.Response.WriteAsync($"Impossibile sbloccare il link: {ex.Message}", ct);
                return null;
            }
        }

        var ffmpegPath = ResolveTool("ffmpeg.exe");
        var ffprobePath = ResolveTool("ffprobe.exe");

        // Log dedicato per questo file (richiesta utente): un file per titolo+giorno in
        // logs/streams/. Ora vive quanto la sessione (non più "using" scoped alla singola
        // richiesta) — lo chiude RemoveSession quando la sessione viene chiusa per davvero, così
        // tutti i riagganci successivi finiscono nello stesso file, in ordine cronologico.
        var streamLog = new StreamFileLogger(ExtractFileName(directUrl) ?? "sconosciuto");
        streamLog.Log($"=== /stream mode={mode} quality={quality} start={startSeconds:n1}s forceEncode={forceEncode} reason={reason} — nuova sessione ({key}) ===");

        // L'audio va SEMPRE valutato dal probe, indipendentemente dalla modalità: a differenza del
        // video (dove "prova prima copy" ha senso, perché il supporto HEVC ecc. varia davvero da
        // player a player, vedi settimo prototipo su TV WebOS), AC-3/DTS/TrueHD non sono quasi mai
        // decodificabili in un browser per limiti di licenza dei codec, non del singolo player — un
        // remux "copy" alla cieca produce video muto invece di un errore rilevabile, quindi non può
        // essere lasciato al meccanismo di fallback lato client basato sull'evento "error".
        ProbeInfo probe;
        try
        {
            probe = await ProbeAsync(directUrl, ffprobePath, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "🎬 Streaming: ffprobe fallito, trascodifico video e audio senza informazioni sul sorgente");
            streamLog.Log($"⚠️ ffprobe fallito: {ex.Message} — trascodifico senza informazioni sul sorgente");
            probe = ProbeInfo.Unknown;
        }

        // Un indice richiesto fuori range (client con un elenco tracce ormai stale, o file
        // riprobato con meno tracce del previsto) ricade sulla 0 invece di lasciar passare un
        // "-map 0:a:N?" che ffmpeg accetterebbe comunque (il "?" lo rende opzionale) ma zittirebbe
        // l'audio senza una vera ragione — meglio un fallback esplicito e prevedibile.
        if (audioIndex < 0 || audioIndex >= probe.AudioTracks.Count) audioIndex = 0;

        var encoderName = await ResolveEncoderAsync(encoderSetting, ct);
        _log.LogInformation("▶️ Streaming ({Mode}) da {Start:n1}s — video={Video} audio={Audio} (traccia {AudioIndex}) encoder={Encoder} qualità={Quality}",
            mode, startSeconds, probe.VideoCodec ?? "?", probe.AudioCodec ?? "?", audioIndex, encoderName, quality);
        streamLog.Log($"probe: video={probe.VideoCodec ?? "?"} 10bit={probe.Is10Bit} hdr={probe.IsHdr} audio={probe.AudioCodec ?? "?"} tracce audio={probe.AudioTracks.Count} (scelta {audioIndex}) durata={probe.DurationSeconds?.ToString("n1") ?? "?"}s — encoder={encoderName}");

        // Sessione con vita propria: il CancellationToken della richiesta HTTP che ha innescato la
        // creazione NON viene usato oltre questo punto per il producer — altrimenti la sessione
        // morirebbe insieme a quella singola richiesta, vanificando l'intero scopo della Fase 2.
        //
        // BUG REALE (il tentativo precedente andava nella direzione sbagliata): avevo ridotto
        // questo buffer per copy/DirectPlay pensando che la velocità senza limiti della copia pura
        // (nessun -maxrate: è pura I/O, non un encoder — misurato ~327 Mbit/s in un test isolato)
        // causasse cicli di backpressure verso il player. Ma il sintomo reale osservato (ffmpeg
        // "vivo" con `-progress` che si ripete identico per diversi secondi, sia in copy sia,
        // seppure più raramente, in trascodifica) è coerente con l'opposto: brevi intoppi di
        // rete IN LETTURA dal link AllDebrid, non in scrittura verso il player. La trascodifica li
        // nasconde quasi sempre perché l'encoder ha già i suoi buffer interni (decodifica,
        // riordino B-frame, lookahead del bitrate) che fungono da scorta; la copia pura non ha
        // quasi nessun buffering interno, quindi ogni intoppo si propaga subito in uscita. Un
        // buffer NOSTRO grande in copy serve esattamente a questo: dare a ffmpeg margine per
        // scaricare in anticipo durante i tratti di rete buoni, così un intoppo breve viene
        // assorbito dalla scorta già in memoria invece di essere visibile subito al player — un
        // buffer piccolo (il tentativo precedente) si svuota quasi subito e non lascia scorta.
        // Dimensionato più GRANDE di quello per la trascodifica proprio perché la copia può
        // sostenere bitrate molto più alti (100+ Mbit/s reali, non solo i 3-40 Mbit/s limitati da
        // -maxrate) — serve più spazio per ottenere la stessa scorta in secondi di contenuto.
        var (pipePauseBytes, pipeResumeBytes) = isCopyMode
            ? (128L * 1024 * 1024, 64L * 1024 * 1024)
            : (PipeBufferPauseBytes, PipeBufferResumeBytes);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: pipePauseBytes, resumeWriterThreshold: pipeResumeBytes));
        var sessionCts = new CancellationTokenSource();

        var args = BuildStreamArgs(directUrl, startSeconds, probe, quality, encoderName, tryVideoCopy: isCopyMode, forceEncode, audioIndex);
        streamLog.Log($"ffmpeg {string.Join(' ', args)}");
        var (outcome, process, producerTask) = await StartFfmpegProducerAsync(ffmpegPath, args, pipe.Writer, sessionCts.Token, streamLog);

        if (outcome == RunResult.FailedBeforeAnyBytes && isCopyMode)
        {
            _log.LogWarning("⚠️ Video-copy fallito ad aprirsi, forzo la trascodifica del video");
            streamLog.Log("⚠️ video-copy fallito ad aprirsi, forzo la trascodifica del video");
            var forcedArgs = BuildStreamArgs(directUrl, startSeconds, probe, quality, encoderName, tryVideoCopy: false, forceEncode, audioIndex);
            streamLog.Log($"ffmpeg (retry, trascodifica forzata) {string.Join(' ', forcedArgs)}");
            (outcome, process, producerTask) = await StartFfmpegProducerAsync(ffmpegPath, forcedArgs, pipe.Writer, sessionCts.Token, streamLog);
        }

        if (outcome == RunResult.FailedBeforeAnyBytes && !string.Equals(encoderName, "libx264", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("⚠️ Encoder {Encoder} fallito ad aprirsi, ripiego su libx264 (software)", encoderName);
            streamLog.Log($"⚠️ encoder {encoderName} fallito ad aprirsi, ripiego su libx264 (software)");
            var fallbackArgs = BuildStreamArgs(directUrl, startSeconds, probe, quality, "libx264", tryVideoCopy: false, forceEncode, audioIndex);
            streamLog.Log($"ffmpeg (retry, libx264) {string.Join(' ', fallbackArgs)}");
            (outcome, process, producerTask) = await StartFfmpegProducerAsync(ffmpegPath, fallbackArgs, pipe.Writer, sessionCts.Token, streamLog);
        }

        if (outcome == RunResult.FailedBeforeAnyBytes)
        {
            streamLog.Log("⚠️ tutti i tentativi falliti prima di produrre byte, sessione non creata");
            try { await pipe.Writer.CompleteAsync(); } catch { }
            streamLog.Dispose();
            sessionCts.Dispose();
            return null;
        }

        var session = new StreamSession
        {
            Key = key,
            FileLink = fileLink,
            Pipe = pipe,
            SessionCts = sessionCts,
            StreamLog = streamLog,
            CurrentProcess = process,
            Attached = true,
        };
        // Qualunque sia il motivo per cui il producer vincente termina in futuro (fine naturale del
        // file, crash a metà, kill per scadenza della finestra di grazia), il Pipe va completato UNA
        // volta sola a quel punto — non prima, altrimenti i tentativi di fallback sopra non
        // potrebbero più scrivere sullo stesso Pipe.
        session.ProducerTask = CompleteWriterWhenDoneAsync(producerTask, pipe.Writer);
        _sessions[key] = session;
        return session;
    }

    private static async Task CompleteWriterWhenDoneAsync(Task producerTask, PipeWriter writer)
    {
        try { await producerTask; }
        finally { try { await writer.CompleteAsync(); } catch { } }
    }

    // Avvia un singolo tentativo ffmpeg e ritorna non appena si sa se è "riuscito" (primo byte
    // scritto sul Pipe — da quel momento in poi continua a girare in background, tracciato dal Task
    // ritornato) oppure "fallito prima di produrre byte" (il chiamante può tentare il prossimo
    // fallback riusando lo stesso PipeWriter, perché non vi è stato scritto nulla).
    private async Task<(RunResult Outcome, Process Process, Task ProducerTask)> StartFfmpegProducerAsync(string ffmpegPath, List<string> args, PipeWriter writer, CancellationToken sessionCt, StreamFileLogger streamLog)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _log.LogDebug("ffmpeg {Args}", string.Join(' ', args));

        var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            throw new InvalidOperationException("Impossibile avviare ffmpeg.");

        var stderrTask = LogStderrAsync(proc, sessionCt, streamLog);
        var firstByteTcs = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var copyTask = Task.Run(async () =>
        {
            long bytesWritten = 0;
            // Diagnostica gemella di quella in ConsumeSessionAsync: distingue ffmpeg lento a
            // PRODURRE (stdout.ReadAsync lento — coerente con un vero stallo in lettura da
            // AllDebrid) da NOI lenti a mettere in coda (writer.WriteAsync lento — il nostro Pipe è
            // pieno, backpressure genuina perché il consumo a valle non tiene il passo).
            var producerStopwatch = System.Diagnostics.Stopwatch.StartNew();
            const int ProducerSlowThresholdMs = 2000;
            try
            {
                var buffer = new byte[81920];
                var stdout = proc.StandardOutput.BaseStream;
                while (true)
                {
                    int read;
                    producerStopwatch.Restart();
                    try
                    {
                        read = await stdout.ReadAsync(buffer, sessionCt);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    if (producerStopwatch.ElapsedMilliseconds > ProducerSlowThresholdMs)
                    {
                        streamLog.Log($"🐌 lettura da ffmpeg lenta: {producerStopwatch.ElapsedMilliseconds}ms (probabile stallo IN LETTURA da AllDebrid)");
                    }
                    if (read <= 0) break;

                    producerStopwatch.Restart();
                    await writer.WriteAsync(buffer.AsMemory(0, read), sessionCt);
                    if (producerStopwatch.ElapsedMilliseconds > ProducerSlowThresholdMs)
                    {
                        streamLog.Log($"🐌 scrittura nel buffer lenta: {producerStopwatch.ElapsedMilliseconds}ms (buffer pieno — il consumo verso la TV non tiene il passo)");
                    }
                    var wasFirstByte = bytesWritten == 0;
                    bytesWritten += read;
                    if (wasFirstByte) firstByteTcs.TrySetResult(RunResult.Completed);
                }
            }
            catch (OperationCanceledException)
            {
                // Sessione chiusa (grace period scaduto e processo killato): fine attesa, non un
                // fallimento da segnalare.
            }
            finally
            {
                if (!proc.HasExited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* già terminato nel frattempo */ }
                }
                try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
                await stderrTask;

                var outcome = bytesWritten == 0 && proc.ExitCode != 0 ? RunResult.FailedBeforeAnyBytes : RunResult.Completed;
                streamLog.Log($"fine: esito={outcome} bytes={bytesWritten} exitCode={proc.ExitCode}");
                // No-op se già risolto con successo al primo byte: qui serve solo per il caso in cui
                // il processo sia morto SENZA mai scrivere nulla.
                firstByteTcs.TrySetResult(outcome);
                proc.Dispose();
            }
        });

        var outcome = await firstByteTcs.Task;
        return (outcome, proc, copyTask);
    }

    // Copia dal buffer della sessione alla risposta HTTP di QUESTA richiesta finché: il client si
    // disconnette (la sessione resta viva, vedi ScheduleDetachGrace), oppure il producer ha finito
    // per davvero (fine naturale del file o crash: qui non c'è più nulla da riagganciare, la
    // sessione viene chiusa subito).
    private async Task ConsumeSessionAsync(StreamSession session, HttpContext http, CancellationToken requestCt)
    {
        var reader = session.Pipe.Reader;
        var producerCompleted = false;
        // Diagnostica (richiesta utente, dopo diversi tentativi di aggiustare le dimensioni dei
        // buffer "alla cieca"): distingue un vero stallo in LETTURA da AllDebrid/ffmpeg (il nostro
        // buffer risulta VUOTO, reader.ReadAsync stesso impiega secondi) da uno stallo in SCRITTURA
        // verso la TV (il buffer ha già dati pronti, ma http.Response.Body.WriteAsync impiega
        // secondi) — le due cause hanno rimedi opposti (più margine di lettura anticipata vs. un
        // problema di rete/ricezione lato TV), non ha senso continuare a indovinare senza saperlo.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        const int SlowThresholdMs = 2000;

        try
        {
            while (true)
            {
                ReadResult result;
                stopwatch.Restart();
                try
                {
                    result = await reader.ReadAsync(requestCt);
                }
                catch (OperationCanceledException)
                {
                    break; // questa richiesta HTTP si è disconnessa
                }
                if (stopwatch.ElapsedMilliseconds > SlowThresholdMs)
                {
                    session.StreamLog.Log($"🐌 lettura dal buffer lenta: {stopwatch.ElapsedMilliseconds}ms (buffer vuoto — probabile stallo IN LETTURA da AllDebrid/ffmpeg)");
                }

                var buffer = result.Buffer;
                var writeOk = true;
                stopwatch.Restart();
                try
                {
                    foreach (var segment in buffer)
                    {
                        try
                        {
                            await http.Response.Body.WriteAsync(segment, requestCt);
                        }
                        catch
                        {
                            writeOk = false;
                            break;
                        }
                    }
                }
                finally
                {
                    // Sempre avanzato fino in fondo, anche in caso di scrittura fallita a metà: i
                    // byte non consegnati a QUESTO client non vengono "rimessi in coda" per un
                    // eventuale riaggancio — semplificazione accettata (vedi piano), un piccolo
                    // buco è preferibile a un riavvio completo.
                    reader.AdvanceTo(buffer.End);
                }
                if (writeOk && stopwatch.ElapsedMilliseconds > SlowThresholdMs)
                {
                    session.StreamLog.Log($"🐌 scrittura verso il player lenta: {stopwatch.ElapsedMilliseconds}ms per {buffer.Length} byte (probabile stallo IN SCRITTURA verso la TV)");
                }

                if (!writeOk) break;
                if (result.IsCompleted) { producerCompleted = true; break; }
            }
        }
        finally
        {
            session.Attached = false;
            if (producerCompleted)
                RemoveSession(session, "stream terminato (fine naturale o crash del producer)");
            else
                ScheduleDetachGrace(session);
        }
    }

    private void ScheduleDetachGrace(StreamSession session)
    {
        var graceCts = new CancellationTokenSource();
        session.DetachGraceCts = graceCts;
        session.StreamLog.Log($"⏸️ client disconnesso, sessione tenuta viva {SessionDetachGrace.TotalSeconds:n0}s in attesa di un riaggancio");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SessionDetachGrace, graceCts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // riagganciato in tempo (GetReattachableSessionLocked ha cancellato questo token)
            }
            session.StreamLog.Log("⏹️ nessun riaggancio entro la finestra di grazia, chiudo la sessione");
            RemoveSession(session, "grace period scaduto senza riaggancio");
        });
    }

    private void RemoveSession(StreamSession session, string reason)
    {
        if (Interlocked.Exchange(ref session.RemovedFlag, 1) != 0) return; // già rimossa (race tra grace-timer e producer)

        _sessions.TryRemove(session.Key, out _);
        session.DetachGraceCts?.Cancel();
        session.DetachGraceCts?.Dispose();
        session.SessionCts.Cancel();
        try { session.CurrentProcess?.Kill(entireProcessTree: true); } catch { /* già terminato */ }
        session.StreamLog.Log($"🧹 sessione chiusa: {reason}");

        // ProducerTask (CompleteWriterWhenDoneAsync) completa da solo il PipeWriter quando il kill
        // sopra fa terminare il ciclo di copia — qui basta aspettarlo per sapere quando è sicuro
        // chiudere il file di log dedicato.
        _ = (session.ProducerTask ?? Task.CompletedTask).ContinueWith(_ => session.StreamLog.Dispose(), TaskScheduler.Default);
        session.SessionCts.Dispose();
    }


    private async Task LogStderrAsync(Process proc, CancellationToken ct, StreamFileLogger? streamLog = null)
    {
        // Righe di "-progress pipe:2": key=value una per riga (frame=, fps=, speed=, out_time=,
        // ...), chiuse da "progress=continue"/"progress=end" — qui si tiene solo speed/out_time,
        // il resto (frame/fps/bitrate/dup_frames/...) non serve a diagnosticare uno stallo.
        string? lastOutTime = null;
        string? lastSpeed = null;

        try
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync(ct)) is not null)
            {
                if (line.StartsWith("out_time=", StringComparison.Ordinal)) { lastOutTime = line[9..]; continue; }
                if (line.StartsWith("speed=", StringComparison.Ordinal)) { lastSpeed = line[6..]; continue; }
                if (line.StartsWith("progress=", StringComparison.Ordinal))
                {
                    if (lastSpeed is not null)
                    {
                        var speedText = lastSpeed.TrimEnd('x');
                        var belowRealtime = double.TryParse(speedText, NumberStyles.Any, CultureInfo.InvariantCulture, out var speedValue) && speedValue < 1.0;
                        if (belowRealtime)
                            _log.LogWarning("🐢 ffmpeg sotto tempo reale: speed={Speed} a {OutTime} — possibile causa di uno stallo lato player", lastSpeed, lastOutTime);
                        else
                            _log.LogDebug("ffmpeg progress: out_time={OutTime} speed={Speed}", lastOutTime, lastSpeed);
                        streamLog?.Log($"{(belowRealtime ? "🐢 SOTTO TEMPO REALE" : "progress")} out_time={lastOutTime} speed={lastSpeed}");
                    }
                    continue;
                }

                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("not supported", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning("ffmpeg: {Line}", line);
                    streamLog?.Log($"⚠️ ffmpeg: {line}");
                }
                else
                {
                    _log.LogDebug("ffmpeg: {Line}", line);
                    streamLog?.Log($"ffmpeg: {line}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Errore leggendo stderr di ffmpeg");
        }
    }

    // ----------------------------------------------------
    // Risoluzione ffmpeg/ffprobe
    // ----------------------------------------------------

    private static string ResolveTool(string exeName)
    {
        var knownDir = Path.GetDirectoryName(KnownFfmpegPath)!;
        var knownPath = Path.Combine(knownDir, exeName);
        return File.Exists(knownPath) ? knownPath : Path.GetFileNameWithoutExtension(exeName);
    }
}
