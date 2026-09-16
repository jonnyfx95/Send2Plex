using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Streaming HLS segmentato (docs/piano-streaming-multiutente-hls.md, Fase 2) — ANCORA un solo
/// spettatore per file (come il pipe MP4 esistente in StreamingService.cs, invariato): questa
/// fase riscrive SOLO il trasporto (segmenti su disco invece di un pipe continuo), non ancora la
/// condivisione multi-utente (Fase 3, cambio mirato e separato). Endpoint completamente nuovi
/// (`/stream/hls*`), nessuna modifica al percorso `/stream` esistente — coesistono, l'app webOS
/// non chiama ancora questi endpoint (arriverà con la Fase 4).
/// </summary>
public partial class StreamingService
{
    // Nessuna richiesta di playlist/segmento per questo tanto tempo = sessione considerata
    // abbandonata. Più lungo della finestra di grazia del pipe (30s) perché qui non c'è un
    // "client disconnesso" esplicito da rilevare (nessuna connessione HTTP persistente) — solo
    // richieste discrete, quindi serve più margine prima di scambiare una pausa nella
    // riproduzione per un abbandono vero.
    private static readonly TimeSpan HlsInactivityTimeout = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<string, HlsSession> _hlsSessionsByKey = new();
    private readonly ConcurrentDictionary<string, HlsSession> _hlsSessionsById = new();
    private readonly SemaphoreSlim _hlsSetupGate = new(1, 1);
    private Timer? _hlsCleanupTimer;

    private sealed class HlsSession
    {
        public required string Id { get; init; }
        public required string Key { get; init; }
        public required string FileLink { get; init; }
        public required string SessionDir { get; init; }
        public required double OriginalStartSeconds { get; init; }
        // Quanto contenuto produrrà QUESTA sessione dall'inizio alla fine del file — nota fin da
        // subito (ffprobe), usata per dichiarare una playlist VOD chiusa fin dalla prima
        // richiesta invece di farla crescere segmento per segmento (vedi BuildFullPlaylist).
        public required double RemainingDurationSeconds { get; init; }
        public required StreamFileLogger StreamLog { get; init; }
        public required CancellationTokenSource SessionCts { get; init; }
        public Process? CurrentProcess { get; set; }
        public DateTime LastRequestAtUtc = DateTime.UtcNow;
        public int RemovedFlag;
    }

    // ----------------------------------------------------
    // Endpoint (registrati in Program.cs)
    // ----------------------------------------------------
    public static Task HandleHlsStartAsync(HttpContext http, StreamingService svc) => svc.HlsStartAsync(http, http.RequestAborted);
    public static Task HandleHlsPlaylistAsync(HttpContext http, string sessionId, StreamingService svc) => svc.HlsPlaylistAsync(http, sessionId, http.RequestAborted);
    public static Task<IResult> HandleHlsFileAsync(HttpContext http, string sessionId, string file, StreamingService svc) => svc.HlsFileAsync(http, sessionId, file, http.RequestAborted);

    // TEMPORANEO — pagina di test per verificare la Fase 2 sulla TV vera, stesso principio della
    // pagina dello spike Fase 1 (già rimossa): un <video> nudo che punta a /stream/hls?..., un
    // overlay con currentTime/duration/seekable. Da togliere insieme alla riga corrispondente in
    // Program.cs quando la Fase 2 è confermata (o si passa alla Fase 3/4).
    public static IResult HandleHlsTestPage(HttpContext http)
    {
        // no-store: senza questo il browser della TV ha servito una copia in cache della pagina
        // (con dentro il vecchio "start=" già scaduto) invece di ricaricarla per davvero —
        // scoperto in pratica durante il debug, un "riaggancio" alla sessione vecchia mascherato
        // da falso "stesso risultato" dopo un fix reale sul server.
        http.Response.Headers.CacheControl = "no-store";
        const string testFile = @"I:\Plex\TV Series\Operazione Speciale Lioness\Operazione.Speciale.Lioness.S03E04.Calabroni.assassini.1080p.AMZN.WEB-DL.ITA.ENG.DDP5.1.H.264-G66.mkv";
        var encoded = Uri.EscapeDataString(testFile);
        // Bucket sempre diverso ad ogni caricamento (diagnostica, 2026-09-16): senza questo, un
        // secondo test riusava la STESSA sessione HLS di un test precedente rimasta viva da
        // minuti — la TV la trovava già con decine di segmenti accumulati, un fattore in più (già
        // "vecchia"/con molto contenuto già generato) mai isolato dalla variabile che stiamo
        // davvero testando (playlist ancora aperta, senza ENDLIST). Con lo start che cambia ogni
        // secondo, ogni ricarica crea per forza una sessione nuova e piccola.
        var start = 60 + (DateTime.UtcNow.Second % 50);
        var src = $"/stream/hls?link={encoded}&mode=transcode&quality=alta&forceEncode=true&start={start}&provider=local&audioIndex=0";
        return Results.Content(
            $$"""
            <!doctype html><html><body style="background:#000;margin:0">
            <video id="v" controls autoplay playsinline style="width:100vw;height:100vh"
                   src="{{src}}"></video>
            <div id="info" style="position:fixed;top:0;left:0;color:#0f0;font:16px monospace;background:#000a;padding:8px"></div>
            <script>
              const v = document.getElementById('v'), info = document.getElementById('info');
              v.addEventListener('error', () => console.error('video error', v.error && v.error.code, v.error && v.error.message));
              setInterval(() => {
                const err = v.error ? `code=${v.error.code} msg=${v.error.message||'(nessuno)'}` : '(nessuno)';
                info.textContent = `currentTime=${v.currentTime.toFixed(1)} duration=${v.duration} readyState=${v.readyState} networkState=${v.networkState} seekable=${v.seekable.length ? v.seekable.end(0).toFixed(1) : '(vuoto)'} error=${err}`;
              }, 500);
            </script>
            </body></html>
            """, "text/html");
    }

    // Stessi parametri query di /stream (StreamAsync) — crea/riaggancia una sessione HLS e
    // redirige alla playlist reale, già ritagliata al punto richiesto.
    private async Task HlsStartAsync(HttpContext http, CancellationToken ct)
    {
        EnsureHlsCleanupTimerStarted();

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
        var forceEncode = string.Equals(req.Query["forceEncode"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var startSeconds = double.TryParse(req.Query["start"], NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var quality = req.Query["quality"].ToString();
        if (string.IsNullOrWhiteSpace(quality)) quality = _cfg.Quality;
        var encoderSetting = req.Query["encoder"].ToString();
        if (string.IsNullOrWhiteSpace(encoderSetting)) encoderSetting = _cfg.Encoder;
        var providerRaw = req.Query["provider"].ToString();
        var isLocal = string.Equals(providerRaw, "local", StringComparison.OrdinalIgnoreCase);
        var provider = ParseProvider(providerRaw);
        var audioIndex = int.TryParse(req.Query["audioIndex"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ai) && ai >= 0 ? ai : 0;
        var reason = req.Query["reason"].ToString();
        if (string.IsNullOrWhiteSpace(reason)) reason = "sconosciuto";

        var key = BuildSessionKey(fileLink, mode, quality, forceEncode, startSeconds, isLocal ? "local" : provider.ToString(), audioIndex);

        HlsSession? session;
        await _hlsSetupGate.WaitAsync(ct);
        try
        {
            session = GetReattachableHlsSessionLocked(key);
            session ??= await CreateHlsSessionLockedAsync(http, key, fileLink, mode, isCopyMode, quality, encoderSetting, forceEncode, startSeconds, provider, isLocal, audioIndex, reason, ct);
        }
        finally
        {
            _hlsSetupGate.Release();
        }

        if (session is null) return; // errore già risposto

        session.LastRequestAtUtc = DateTime.UtcNow;
        var offset = Math.Max(0, startSeconds - session.OriginalStartSeconds);
        http.Response.Redirect($"/stream/hls/{session.Id}/playlist.m3u8?offset={offset.ToString(CultureInfo.InvariantCulture)}");
    }

    private HlsSession? GetReattachableHlsSessionLocked(string key)
    {
        if (_hlsSessionsByKey.TryGetValue(key, out var existing) && existing.RemovedFlag == 0)
        {
            existing.StreamLog.Log("↩️ richiesta HLS riagganciata alla sessione esistente (nessun riavvio di ffmpeg)");
            return existing;
        }
        return null;
    }

    private void TerminateOtherHlsSessionsForFileLocked(string fileLink, string keepKey)
    {
        foreach (var kvp in _hlsSessionsByKey)
        {
            if (kvp.Key == keepKey || !string.Equals(kvp.Value.FileLink, fileLink, StringComparison.Ordinal)) continue;
            kvp.Value.StreamLog.Log("⏹️ sessione HLS terminata: una nuova sessione per lo stesso file ha preso il suo posto");
            RemoveHlsSession(kvp.Value, "superata da una nuova sessione HLS per lo stesso file");
        }
    }

    private async Task<HlsSession?> CreateHlsSessionLockedAsync(HttpContext http, string key, string fileLink, string mode, bool isCopyMode, string quality, string encoderSetting, bool forceEncode, double startSeconds, DebridProvider provider, bool isLocal, int audioIndex, string reason, CancellationToken ct)
    {
        // Stessa regola del pipe MP4 (vedi CreateSessionLockedAsync): un solo spettatore per
        // file, niente due processi ffmpeg a leggere lo stesso file in parallelo.
        TerminateOtherHlsSessionsForFileLocked(fileLink, key);

        string directUrl;
        if (isLocal)
        {
            var resolved = ResolveLocalPath(fileLink);
            if (resolved is null)
            {
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
                _log.LogError(ex, "🎬 Streaming HLS: impossibile sbloccare il link del provider debrid");
                http.Response.StatusCode = StatusCodes.Status502BadGateway;
                await http.Response.WriteAsync($"Impossibile sbloccare il link: {ex.Message}", ct);
                return null;
            }
        }

        var ffmpegPath = ResolveTool("ffmpeg.exe");
        var ffprobePath = ResolveTool("ffprobe.exe");

        var id = ComputeSessionId(key);
        var sessionDir = Path.Combine(AppPaths.Data, "hls-sessions", id);
        Directory.CreateDirectory(sessionDir);

        var streamLog = new StreamFileLogger(ExtractFileName(directUrl) ?? "sconosciuto");
        streamLog.Log($"=== /stream/hls mode={mode} quality={quality} start={startSeconds:n1}s forceEncode={forceEncode} reason={reason} — nuova sessione HLS ({key}) ===");

        ProbeInfo probe;
        try
        {
            probe = await ProbeAsync(directUrl, ffprobePath, ct);
        }
        catch (Exception ex)
        {
            streamLog.Log($"⚠️ ffprobe fallito: {ex.Message} — trascodifico senza informazioni sul sorgente");
            probe = ProbeInfo.Unknown;
        }
        if (audioIndex < 0 || audioIndex >= probe.AudioTracks.Count) audioIndex = 0;

        var encoderName = await ResolveEncoderAsync(encoderSetting, ct);
        streamLog.Log($"probe: video={probe.VideoCodec ?? "?"} 10bit={probe.Is10Bit} hdr={probe.IsHdr} audio={probe.AudioCodec ?? "?"} tracce audio={probe.AudioTracks.Count} (scelta {audioIndex}) durata={probe.DurationSeconds?.ToString("n1") ?? "?"}s — encoder={encoderName}");

        var sessionCts = new CancellationTokenSource();

        var args = BuildStreamArgs(directUrl, startSeconds, probe, quality, encoderName, tryVideoCopy: isCopyMode, forceEncode, audioIndex, asHls: true);
        streamLog.Log($"ffmpeg (cwd={sessionDir}) {string.Join(' ', args)}");
        var (outcome, process) = await StartFfmpegForHlsAsync(ffmpegPath, args, sessionDir, sessionCts.Token, streamLog);

        if (outcome == RunResult.FailedBeforeAnyBytes && isCopyMode)
        {
            streamLog.Log("⚠️ video-copy HLS fallito ad aprirsi, forzo la trascodifica del video");
            var forcedArgs = BuildStreamArgs(directUrl, startSeconds, probe, quality, encoderName, tryVideoCopy: false, forceEncode, audioIndex, asHls: true);
            streamLog.Log($"ffmpeg (retry, trascodifica forzata) {string.Join(' ', forcedArgs)}");
            (outcome, process) = await StartFfmpegForHlsAsync(ffmpegPath, forcedArgs, sessionDir, sessionCts.Token, streamLog);
        }
        if (outcome == RunResult.FailedBeforeAnyBytes && !string.Equals(encoderName, "libx264", StringComparison.OrdinalIgnoreCase))
        {
            streamLog.Log($"⚠️ encoder {encoderName} fallito ad aprirsi (HLS), ripiego su libx264 (software)");
            var fallbackArgs = BuildStreamArgs(directUrl, startSeconds, probe, quality, "libx264", tryVideoCopy: false, forceEncode, audioIndex, asHls: true);
            streamLog.Log($"ffmpeg (retry, libx264) {string.Join(' ', fallbackArgs)}");
            (outcome, process) = await StartFfmpegForHlsAsync(ffmpegPath, fallbackArgs, sessionDir, sessionCts.Token, streamLog);
        }
        if (outcome == RunResult.FailedBeforeAnyBytes)
        {
            streamLog.Log("⚠️ tutti i tentativi HLS falliti prima di produrre un segmento, sessione non creata");
            streamLog.Dispose();
            sessionCts.Dispose();
            try { Directory.Delete(sessionDir, recursive: true); } catch { }
            return null;
        }

        var session = new HlsSession
        {
            Id = id,
            Key = key,
            FileLink = fileLink,
            SessionDir = sessionDir,
            OriginalStartSeconds = startSeconds,
            RemainingDurationSeconds = Math.Max(0, (probe.DurationSeconds ?? startSeconds) - startSeconds),
            StreamLog = streamLog,
            SessionCts = sessionCts,
            CurrentProcess = process,
        };
        _hlsSessionsByKey[key] = session;
        _hlsSessionsById[id] = session;
        return session;
    }

    // Come StartFfmpegProducerAsync (pipe MP4), ma il "primo byte" qui è la comparsa del primo
    // segmento sul disco (niente stdout da leggere: ffmpeg scrive i file direttamente nella
    // WorkingDirectory). Poll leggero invece di un FileSystemWatcher — la cartella ha pochi file
    // e la finestra di attesa è breve, non vale la complessità di un watcher per questo.
    private async Task<(RunResult Outcome, Process Process)> StartFfmpegForHlsAsync(string ffmpegPath, List<string> args, string sessionDir, CancellationToken sessionCt, StreamFileLogger streamLog)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            WorkingDirectory = sessionDir,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new InvalidOperationException("Impossibile avviare ffmpeg (HLS).");

        var stderrTask = LogStderrAsync(proc, sessionCt, streamLog);
        var firstSegmentTcs = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Mai il ".tmp" di hls_flags=temp_file (che inizia comunque per "segment_" e darebbe un
        // falso positivo su un segmento ancora a metà scrittura) — solo l'estensione finale.
        int SegmentCount() => Directory.Exists(sessionDir) ? Directory.EnumerateFiles(sessionDir, "segment_*.m4s").Count() : 0;
        bool HasAnySegment() => SegmentCount() > 0;

        // BUG REALE, TENTATIVO FALLITO (2026-09-16): per il "frame che parte e si blocca subito
        // dopo" avevo alzato questa soglia da 1 a 3 segmenti, ragionando che ffmpeg produce a
        // ~11x tempo reale — vero SOLO per contenuto leggero (1080p H.264 8-bit). Su un file 4K
        // HDR/Dolby Vision con tone-mapping (la maggior parte della libreria reale dell'utente),
        // l'encoder gira a soli ~3,3x: 3 segmenti invece di 1 hanno reso l'attesa iniziale
        // proporzionalmente molto più lunga, peggiorando visibilmente sia l'avvio sia ogni
        // reload da seek — confermato nei log (stessa identica sessione HLS, stesso file,
        // filata liscia con la soglia a 1 nel primo test, con uno stall-recovery reale dopo
        // essere passata a 3). Tornato a 1 — un margine di prebuffer fisso non regge la
        // variabilità di velocità tra sorgenti leggere e pesanti; se il problema del primo
        // frame va ripreso, serve una soglia proporzionale alla velocità di encode reale
        // (osservabile da "speed=" nei log), non un numero fisso di segmenti.
        const int PrebufferSegmentCount = 1;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!proc.HasExited)
                {
                    if (SegmentCount() >= PrebufferSegmentCount) { firstSegmentTcs.TrySetResult(RunResult.Completed); return; }
                    await Task.Delay(200, sessionCt);
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        _ = Task.Run(async () =>
        {
            try { await proc.WaitForExitAsync(sessionCt); } catch (OperationCanceledException) { }
            await stderrTask;
            var outcome = !HasAnySegment() && proc.ExitCode != 0 ? RunResult.FailedBeforeAnyBytes : RunResult.Completed;
            streamLog.Log($"fine (HLS): esito={outcome} exitCode={proc.ExitCode}");
            firstSegmentTcs.TrySetResult(outcome);
        }, CancellationToken.None);

        var outcome = await firstSegmentTcs.Task;
        return (outcome, proc);
    }

    // Durata target di ogni segmento richiesta a ffmpeg (-hls_time 2) — la durata REALE oscilla
    // leggermente intorno a questo valore (osservato 2.002s con GOP fisso a 48 frame su sorgenti
    // a 23.976/24fps, mai esattamente 2.0) perché ogni segmento si chiude al keyframe più vicino,
    // non a un timestamp esatto. Usato qui per dichiarare la playlist sintetica sotto.
    private const double HlsSegmentSeconds = 2.002;

    private Task HlsPlaylistAsync(HttpContext http, string sessionId, CancellationToken ct)
    {
        if (!_hlsSessionsById.TryGetValue(sessionId, out var session) || session.RemovedFlag != 0)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        session.LastRequestAtUtc = DateTime.UtcNow;

        var offset = double.TryParse(http.Request.Query["offset"], NumberStyles.Any, CultureInfo.InvariantCulture, out var o) ? o : 0;

        // BUG REALE (2026-09-16): far crescere la playlist un segmento alla volta (come ffmpeg fa
        // di default) genera una playlist "live" (nessun ENDLIST finché la sessione non finisce
        // da sola) — il player nativo webOS/Chromium la accetta ma non avvia mai davvero la
        // riproduzione (readyState resta 0, o fallisce con DEMUXER_ERROR_COULD_NOT_PARSE),
        // riprodotto sia in locale sia sulla TV, indipendentemente da "-hls_playlist_type event".
        // La stessa identica pipeline con una playlist CHIUSA (ENDLIST reale) funziona alla
        // perfezione su entrambi. Poiché la durata totale del file è già nota (ffprobe, non è
        // davvero uno stream live), dichiariamo qui la playlist COMPLETA e già chiusa fin dalla
        // prima richiesta — i segmenti elencati potrebbero non esistere ancora su disco, ma
        // HlsFileAsync aspetta che compaiano invece di dare subito 404.
        var playlist = BuildFullPlaylist(session, offset);
        http.Response.ContentType = "application/vnd.apple.mpegurl";
        http.Response.Headers.CacheControl = "no-cache";
        return http.Response.WriteAsync(playlist, ct);
    }

    private static string BuildFullPlaylist(HlsSession session, double offsetSeconds)
    {
        var totalSegments = session.RemainingDurationSeconds > 0
            ? (int)Math.Ceiling(session.RemainingDurationSeconds / HlsSegmentSeconds)
            : 0;
        var skip = Math.Clamp((int)(offsetSeconds / HlsSegmentSeconds + 0.0001), 0, totalSegments);

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:7\n");
        sb.Append("#EXT-X-TARGETDURATION:2\n");
        sb.Append("#EXT-X-MAP:URI=\"init.mp4\"\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(skip).Append('\n');
        for (var i = skip; i < totalSegments; i++)
        {
            var duration = i == totalSegments - 1
                ? Math.Max(0.1, session.RemainingDurationSeconds - i * HlsSegmentSeconds)
                : HlsSegmentSeconds;
            sb.Append("#EXTINF:").Append(duration.ToString("0.000000", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("segment_").Append(i.ToString("D5")).Append(".m4s\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    private async Task<IResult> HlsFileAsync(HttpContext http, string sessionId, string file, CancellationToken ct)
    {
        if (!_hlsSessionsById.TryGetValue(sessionId, out var session) || session.RemovedFlag != 0)
            return Results.NotFound();
        session.LastRequestAtUtc = DateTime.UtcNow;

        // Nessun nome libero: solo segmenti/init già scritti da ffmpeg in questa cartella —
        // Path.GetFileName scarta qualunque traversal (../..) prima di combinare col percorso
        // base, e l'estensione fissa esclude qualunque altro file (es. la playlist stessa, già
        // servita da un endpoint dedicato con un content-type diverso). Solo fMP4 (.m4s per i
        // segmenti, .mp4 per l'init) — vedi AddHlsOutput in StreamingService.cs sul perché non
        // più MPEG-TS.
        var safeName = Path.GetFileName(file);
        string contentType;
        if (safeName.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) || safeName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) contentType = "video/mp4";
        else return Results.NotFound();
        var path = Path.Combine(session.SessionDir, safeName);

        // BUG REALE (2026-09-16): con la playlist ora dichiarata per intero fin dal principio
        // (BuildFullPlaylist), un segmento elencato potrebbe non esistere ancora su disco — ffmpeg
        // lo sta ancora producendo. Aspettare invece di rispondere subito 404: a ~11x tempo reale
        // (NVENC) un segmento da 2s arriva in meno di 200ms una volta che ffmpeg ci arriva, ma la
        // primissima richiesta di un salto in avanti può comunque richiedere qualche secondo.
        // init.mp4 fa eccezione: scritto una sola volta all'inizio della sessione, se manca c'è
        // un problema reale a monte, non ha senso aspettare.
        if (!safeName.Equals("init.mp4", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < 150 && !File.Exists(path) && session.RemovedFlag == 0; i++)
                await Task.Delay(200, ct);
        }
        if (!File.Exists(path))
            return Results.NotFound();

        // Un segmento, una volta scritto (temp_file lo rinomina solo a fine scrittura), non
        // cambia mai più — cacheable in modo aggressivo, a differenza della playlist.
        //
        // BUG REALE (2026-09-16): `HttpResponse.SendFileAsync` mandava il segmento con
        // "Transfer-Encoding: chunked" e SENZA Content-Length né supporto Range (verificato con
        // `curl -i`) — il demuxer nativo di Chromium (`DEMUXER_ERROR_COULD_NOT_PARSE`, riprodotto
        // sia su Edge desktop sia in locale) sembra intollerante a un segmento servito così,
        // anche se il contenuto stesso decodifica senza il minimo avviso con ffmpeg/ffprobe.
        // `Results.File` con `enableRangeProcessing: true` è il meccanismo standard di ASP.NET
        // Core per file statici — dichiara sempre Content-Length e supporta Range/ETag/
        // If-Modified-Since, esattamente quello che un lettore HLS nativo si aspetta.
        http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.File(path, contentType, enableRangeProcessing: true);
    }

    private void EnsureHlsCleanupTimerStarted()
    {
        if (_hlsCleanupTimer is not null) return;
        lock (_hlsSessionsByKey)
        {
            _hlsCleanupTimer ??= new Timer(_ => SweepInactiveHlsSessions(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    private void SweepInactiveHlsSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _hlsSessionsById)
        {
            var session = kvp.Value;
            if (session.RemovedFlag != 0) continue;
            if (now - session.LastRequestAtUtc > HlsInactivityTimeout)
                RemoveHlsSession(session, $"nessuna richiesta playlist/segmenti da {HlsInactivityTimeout.TotalSeconds:n0}s (inattività)");
        }
    }

    private void RemoveHlsSession(HlsSession session, string reason)
    {
        if (Interlocked.Exchange(ref session.RemovedFlag, 1) != 0) return;

        _hlsSessionsByKey.TryRemove(session.Key, out _);
        _hlsSessionsById.TryRemove(session.Id, out _);
        session.SessionCts.Cancel();
        try { session.CurrentProcess?.Kill(entireProcessTree: true); } catch { }
        session.StreamLog.Log($"🧹 sessione HLS chiusa: {reason}");
        session.StreamLog.Dispose();
        session.SessionCts.Dispose();

        // Cancellazione file su un thread separato: non deve bloccare lo sweep periodico né la
        // richiesta che ha innescato la rimozione (es. una nuova sessione per lo stesso file).
        var dir = session.SessionDir;
        _ = Task.Run(() => { try { Directory.Delete(dir, recursive: true); } catch { } });
    }

    private static string ComputeSessionId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
