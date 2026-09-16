using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendToPlex.Bot.Models;
using SendToPlex.Bot.UI;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SendToPlex.Bot.Services;

public class Downloader
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<Downloader> _log;
    private readonly ConfigStore _configStore;

    // Lette ad ogni chiamata: percorsi e parametri di download possono cambiare a caldo da
    // Impostazioni, senza richiedere un riavvio dell'app (Punto 2).
    private PathSettings _paths => _configStore.Current.Paths;
    private DownloadSettings _cfg => _configStore.Current.Download;

    // Limita a MaxParallel i soli trasferimenti effettivi (quello che satura banda/disco).
    // Ricreato se MaxParallel cambia a caldo da Impostazioni. Deliberatamente NON copre la fase
    // di risoluzione magnet/attesa "ready" su AllDebrid (può durare fino a un'ora mentre AllDebrid
    // processa il torrent) — quella fase non consuma risorse locali, quindi più elementi in coda
    // possono procedere fino a quel punto in parallelo anche con MaxParallel=1, invece di restare
    // bloccati dietro un singolo magnet lento (vedi coda download).
    private SemaphoreSlim? _downloadGate;
    private int _downloadGateSize;
    private readonly object _gateLock = new();

    private SemaphoreSlim GetDownloadGate()
    {
        var desired = Math.Max(1, _cfg.MaxParallel);
        lock (_gateLock)
        {
            if (_downloadGate is null || _downloadGateSize != desired)
            {
                _downloadGate = new SemaphoreSlim(desired, desired);
                _downloadGateSize = desired;
            }
            return _downloadGate;
        }
    }

    // --- Eventi per UI/Telegram ---
    public event Action<string>? DownloadStarted;
    public event Action<string, int, string, string>? DownloadProgress;
    public event Action<string, bool, string>? DownloadCompleted;

    public Downloader(
        IHttpClientFactory httpFactory,
        ConfigStore configStore,
        ILogger<Downloader> log)
    {
        _httpFactory = httpFactory;
        _log = log;
        _configStore = configStore;
    }

    /// <summary>
    /// Scarica un singolo file nella cartella Movies/TV in base a <paramref name="toTv"/>, o nella
    /// cartella dedicata alla sezione Plex <paramref name="sectionId"/> se configurata (l'utente ha
    /// più librerie, es. Film/MCU/Star Wars, ciascuna con la propria cartella di destinazione).
    /// Ritorna il path completo del file salvato.
    /// </summary>
    public async Task<(string path, long size)> SaveOneAsync(string url, bool toTv, CancellationToken ct, int? sectionId = null, Action<double, string, string>? onProgress = null)
    {
        using var http = _httpFactory.CreateClient("DL");
        http.Timeout = TimeSpan.FromMinutes(_cfg.TimeoutMinutes);

        _log.LogInformation("⬇️  Download singolo — URL: {Url}", url);

        // 1) Ricava un filename sensato dall'URL
        string fileName;
        try
        {
            var uri = new Uri(url);
            fileName = Path.GetFileName(uri.AbsolutePath);
        }
        catch
        {
            fileName = null!;
        }
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"file_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}";
        fileName = SanitizeFileName(fileName);

        // 2) Determina cartella di destinazione
        var (folder, kind) = GetTargetFolder(toTv, sectionId, fileName);
        Directory.CreateDirectory(folder);

        // 3) Path finale "logico" e file temporaneo associato. Il nome del .part è deterministico
        // (non reso univoco qui) apposta: se un tentativo precedente per LO STESSO url/nome è
        // rimasto a metà, lo ritroviamo e riprendiamo il download invece di ripartire da zero.
        var finalPath = Path.Combine(folder, fileName);
        var partPath = finalPath + ".part";

        // BUG REALE (file scaricati corrotti): due download per lo STESSO file avviati vicini nel
        // tempo (es. utente che tocca "Scarica" due volte sull'app webOS) correvano sullo stesso
        // .part senza alcuna sincronizzazione. Se il primo veniva cancellato (che elimina il .part,
        // vedi sotto) nell'istante esatto in cui il secondo leggeva resumeFrom e apriva il file, il
        // secondo si ritrovava a scrivere in un file NUOVO (quello vecchio era appena sparito) pur
        // avendo già mandato al server una richiesta Range basata sul vecchio offset — risultato:
        // un file della dimensione "giusta" per il log ma privo per davvero dei primi N byte reali
        // (verificato in un caso concreto: il file finale mancava esattamente dei byte del vecchio
        // offset di ripresa). Il lock per-percorso sotto serializza OGNI accesso allo stesso .part
        // (lettura di resumeFrom, apertura, cancellazione), eliminando la finestra di race.
        var pathLock = GetPathLock(partPath);
        await pathLock.WaitAsync(ct);
        try
        {
            return await SaveOneLockedAsync(http, url, fileName, folder, finalPath, partPath, ct, onProgress);
        }
        finally
        {
            pathLock.Release();
        }
    }

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks = new(StringComparer.OrdinalIgnoreCase);

    private static SemaphoreSlim GetPathLock(string path) =>
        _pathLocks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));

    private async Task<(string path, long size)> SaveOneLockedAsync(HttpClient http, string url, string fileName, string folder, string finalPath, string partPath, CancellationToken ct, Action<double, string, string>? onProgress)
    {
        var resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;

        var sw = Stopwatch.StartNew();
        long bytesWritten = 0;
        var dstPath = finalPath;
        var partFileCreated = false;

        var gate = GetDownloadGate();
        await gate.WaitAsync(ct);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115 Safari/537.36");
            if (resumeFrom > 0)
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            var isResuming = resumeFrom > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (resumeFrom > 0 && !isResuming)
            {
                // Il server non supporta la ripresa (risponde 200 invece di 206): ripartiamo da zero.
                _log.LogWarning("⚠️ Ripresa non supportata dal server per {Url}, riparto da zero", url);
                try { File.Delete(partPath); } catch { }
                resumeFrom = 0;
            }

            resp.EnsureSuccessStatusCode();

            var contentLength = resp.Content.Headers.ContentLength;
            var totalExpected = isResuming && contentLength.HasValue ? contentLength.Value + resumeFrom : contentLength;

            _log.LogDebug("🔍 Content-Length: {Len} bytes ({LenMB} MB)",
                contentLength ?? -1,
                contentLength.HasValue ? (contentLength.Value / 1024d / 1024d).ToString("F2") : "n/d");

            if (contentLength.HasValue && contentLength.Value > 0 && contentLength.Value < 100_000 && !isResuming)
            {
                var preview = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("⚠️ Risposta sospetta (<100KB). Anteprima: {Preview}",
                    preview.Length > 500 ? preview[..500] : preview);
            }

            // Controllo spazio disco disponibile (con margine 10%) prima di iniziare a scrivere.
            if (totalExpected.HasValue)
            {
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(folder) ?? folder);
                    var needed = (long)((totalExpected.Value - resumeFrom) * 1.1);
                    if (needed > 0 && drive.AvailableFreeSpace < needed)
                    {
                        throw new IOException(
                            $"Spazio disco insufficiente su {drive.Name}: servono ~{needed / 1024 / 1024} MB, " +
                            $"disponibili {drive.AvailableFreeSpace / 1024 / 1024} MB.");
                    }
                }
                catch (IOException) { throw; }
                catch { /* DriveInfo può fallire su alcuni percorsi (es. share di rete): non bloccare per questo */ }
            }

            // Timeout dinamico per file grandi: se il tempo stimato (a una velocità minima
            // "accettabile" di 500 KB/s) supera il timeout configurato, estende solo il ciclo
            // di copia — http.Timeout resta quello configurato per l'apertura della connessione.
            using var copyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (totalExpected.HasValue)
            {
                var estimatedSeconds = (totalExpected.Value - resumeFrom) / (500.0 * 1024);
                var configuredSeconds = _cfg.TimeoutMinutes * 60;
                if (estimatedSeconds > configuredSeconds)
                    copyCts.CancelAfter(TimeSpan.FromSeconds(estimatedSeconds * 1.2));
            }

            long bytesThisAttempt;
            await using (var input = await resp.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(partPath, isResuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
            {
                partFileCreated = true;
                DownloadStarted?.Invoke(Path.GetFileName(finalPath));
                _log.LogInformation("▶️ Inizio download: {File} → {Path} (ripresa da {Resume:n0} B)", fileName, finalPath, resumeFrom);

                bytesThisAttempt = await CopyWithProgressAsync(input, output, finalPath, totalExpected, resumeFrom, copyCts.Token, onProgress);
            }

            bytesWritten = resumeFrom + bytesThisAttempt;
            sw.Stop();

            // Rinomina atomica .part -> nome finale, con suffisso univoco solo in caso di collisione
            // reale al momento dello spostamento (niente finestra check-poi-crea: File.Move senza
            // overwrite fallisce atomicamente se la destinazione esiste già).
            dstPath = MoveToUniqueFinalPath(partPath, finalPath);

            _log.LogInformation("✅ Completato: {File} ({Bytes:n0} B) in {Sec:n1}s",
                Path.GetFileName(dstPath), bytesWritten, sw.Elapsed.TotalSeconds);

            DownloadCompleted?.Invoke(Path.GetFileName(dstPath), true, dstPath);
            return (dstPath, bytesWritten);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellazione esplicita dell'utente: elimina il parziale, niente ripresa.
            sw.Stop();
            _log.LogWarning("⏹️ Download cancellato: {Path}", partPath);
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            DownloadCompleted?.Invoke(Path.GetFileName(finalPath), false, "Cancellato");
            throw;
        }
        catch (Exception ex)
        {
            // Altri errori (rete, timeout dinamico, spazio insufficiente, ecc.): il .part
            // viene mantenuto (se creato) per poter riprendere il download al prossimo tentativo.
            sw.Stop();
            _log.LogError(ex, "❌ Errore durante il download di {Url}{Kept}", url,
                partFileCreated ? " — file parziale mantenuto per un successivo tentativo" : "");
            DownloadCompleted?.Invoke(Path.GetFileName(finalPath), false, ex.Message);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sposta <paramref name="sourcePath"/> su <paramref name="finalPath"/>; se la destinazione
    /// esiste già, prova suffissi "(1)", "(2)"... Ogni tentativo è un singolo <see cref="File.Move"/>
    /// senza overwrite: o riesce atomicamente o fallisce, senza la finestra TOCTOU di un
    /// check-poi-crea separato.
    /// </summary>
    private static string MoveToUniqueFinalPath(string sourcePath, string finalPath)
    {
        var dir = Path.GetDirectoryName(finalPath)!;
        var baseName = Path.GetFileNameWithoutExtension(finalPath);
        var ext = Path.GetExtension(finalPath);

        for (var i = 0; i < 1000; i++)
        {
            var candidate = i == 0 ? finalPath : Path.Combine(dir, $"{baseName} ({i}){ext}");
            try
            {
                File.Move(sourcePath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Nome già occupato da un altro file completato: prova il prossimo suffisso.
            }
        }

        throw new IOException("Impossibile trovare un nome file univoco dopo 1000 tentativi.");
    }

    /// <summary>
    /// Scarica più file in parallelo (limite MaxParallel) e ritorna i path salvati.
    /// </summary>
    public async Task<List<string>> SaveAllAsync(IEnumerable<string> urls, bool toTv, CancellationToken ct)
    {
        var (folder, kind) = GetTargetFolder(toTv);
        Directory.CreateDirectory(folder);

        var list = urls.ToList();
        _log.LogInformation("⬇️  Batch download ({Kind}) — {Count} file", kind, list.Count);

        var results = new ConcurrentBag<string>();
        using var throttle = new SemaphoreSlim(Math.Max(1, _cfg.MaxParallel));

        var tasks = list.Select(async url =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var (path, _) = await SaveOneAsync(url, toTv, ct);
                results.Add(path);
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        return results.ToList();
    }

    // ---------------------------
    // Ricerca file già scaricati (app webOS: badge "già presente" + streaming diretto da disco,
    // vedi TvApiEndpoints "/library/status") — pura IO locale sulle cartelle Movies/TV già note,
    // nessuna chiamata a Plex: più immediato di un refresh Plex (che è asincrono) e riusa la stessa
    // convenzione di nomi già applicata da GetTargetFolder/ExtractSeriesName in fase di download.
    // ---------------------------

    private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov", ".webm" };

    private static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // Confronto tollerante (stesso principio di TitlesMatch in Search.razor): solo lettere/numeri,
    // case-insensitive — un nome file spesso ha punti/trattini al posto degli spazi.
    private static string NormalizeForMatch(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Cerca un film già scaricato in una qualunque cartella Movies configurata (default +
    /// per-libreria) il cui nome file contenga il titolo. Torna il percorso completo se trovato.</summary>
    public string? FindLocalMovieFile(string title)
    {
        var norm = NormalizeForMatch(title);
        if (norm.Length == 0) return null;

        var roots = new[] { _paths.Movies }.Concat(_paths.MovieFolders.Select(f => f.Path))
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p));

        foreach (var root in roots)
        {
            var match = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Where(IsVideoFile)
                .FirstOrDefault(f => NormalizeForMatch(Path.GetFileNameWithoutExtension(f)).Contains(norm));
            if (match is not null) return match;
        }
        return null;
    }

    /// <summary>Cerca un episodio già scaricato (cartella serie + pattern SxxExx nel nome file) in
    /// una qualunque cartella TV configurata. Torna il percorso completo se trovato.</summary>
    public string? FindLocalEpisodeFile(string seriesTitle, int season, int episode)
    {
        var normSeries = NormalizeForMatch(seriesTitle);
        if (normSeries.Length == 0) return null;

        var pattern = $"S{season:D2}E{episode:D2}";
        var tvRoots = new[] { _paths.Tv }.Concat(_paths.TvFolders.Select(f => f.Path))
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p));

        foreach (var tvRoot in tvRoots)
        {
            var seriesDirs = Directory.EnumerateDirectories(tvRoot)
                .Where(d => NormalizeForMatch(Path.GetFileName(d)).Contains(normSeries) ||
                            normSeries.Contains(NormalizeForMatch(Path.GetFileName(d))));

            foreach (var seriesDir in seriesDirs)
            {
                var match = Directory.EnumerateFiles(seriesDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsVideoFile)
                    .FirstOrDefault(f => Path.GetFileName(f).Contains(pattern, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
        }
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex SeasonEpisodeRegex =
        new(@"S(\d{1,2})E(\d{1,3})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Tutti gli episodi già scaricati per una serie (cartella + pattern SxxExx nel nome
    /// file), su TUTTE le cartelle TV configurate — usato per il badge "in libreria" della lista
    /// episodi webOS insieme a (non al posto di) la libreria Plex vera e propria: un refresh Plex
    /// fallito (bug pre-esistente, capita spesso) non deve far sembrare "non scaricato" un episodio
    /// che in realtà è già sul disco, solo perché Plex non l'ha ancora indicizzato.</summary>
    public List<(int Season, int Episode)> FindLocalEpisodes(string seriesTitle)
    {
        var normSeries = NormalizeForMatch(seriesTitle);
        if (normSeries.Length == 0) return new();

        var results = new List<(int, int)>();
        var tvRoots = new[] { _paths.Tv }.Concat(_paths.TvFolders.Select(f => f.Path))
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p));

        foreach (var tvRoot in tvRoots)
        {
            var seriesDirs = Directory.EnumerateDirectories(tvRoot)
                .Where(d => NormalizeForMatch(Path.GetFileName(d)).Contains(normSeries) ||
                            normSeries.Contains(NormalizeForMatch(Path.GetFileName(d))));

            foreach (var seriesDir in seriesDirs)
            {
                foreach (var file in Directory.EnumerateFiles(seriesDir, "*", SearchOption.TopDirectoryOnly).Where(IsVideoFile))
                {
                    var match = SeasonEpisodeRegex.Match(Path.GetFileName(file));
                    if (match.Success &&
                        int.TryParse(match.Groups[1].Value, out var s) &&
                        int.TryParse(match.Groups[2].Value, out var e))
                    {
                        results.Add((s, e));
                    }
                }
            }
        }
        return results;
    }

    // ---------------------------
    // Helpers
    // ---------------------------

    private (string folder, string kind) GetTargetFolder(bool toTv, int? sectionId = null, string fileName = "")
    {
        var baseFolder = ResolveBaseFolder(toTv, sectionId);

        if (toTv && !string.IsNullOrEmpty(fileName))
        {
            var seriesName = ExtractSeriesName(fileName);
            return (Path.Combine(baseFolder, seriesName), "TV");
        }
        return (baseFolder, toTv ? "TV" : "Movies");
    }

    // Cartella dedicata alla sezione Plex scelta dall'utente (es. "MCU"), se configurata;
    // altrimenti la cartella di default Movies/TV (comportamento invariato per chi non ha
    // configurato cartelle multiple, incluso il bot Telegram che non passa mai un sectionId).
    private string ResolveBaseFolder(bool toTv, int? sectionId)
    {
        if (sectionId is { } id)
        {
            var folders = toTv ? _paths.TvFolders : _paths.MovieFolders;
            var match = folders.FirstOrDefault(f => f.SectionId == id);
            if (match is not null && !string.IsNullOrWhiteSpace(match.Path))
                return match.Path;
        }
        return toTv ? _paths.Tv : _paths.Movies;
    }

    private static string SanitizeFileName(string name)
    {
        // Il nome può arrivare da fonti non fidate (es. nome file dichiarato da AllDebrid
        // per il contenuto di un magnet): elimina ogni componente di percorso prima di
        // sostituire i caratteri non validi, per evitare traversal tipo "..\..\evil.exe".
        name = Path.GetFileName(name);
        name = name.Replace("..", "_");

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private async Task<long> CopyWithProgressAsync(Stream input, Stream output, string path, long? total, long resumeOffset, CancellationToken ct, Action<double, string, string>? onProgress = null)
    {
        var buffer = new byte[1024 * 64];
        long copied = 0;
        var sw = Stopwatch.StartNew();
        long lastReport = 0;
        long lastProgressReport = 0;

        // Intervallo di reporting adattivo: 1% della dimensione totale (min 1MB), invece di un
        // fisso 1MB che genera troppi update su file piccoli e sembra "lento" su file enormi.
        long progressInterval = total.HasValue
            ? Math.Max(1L * 1024 * 1024, total.Value / 100)
            : 1L * 1024 * 1024;

        while (true)
        {
            var read = await input.ReadAsync(buffer, 0, buffer.Length, ct);
            if (read <= 0) break;

            await output.WriteAsync(buffer, 0, read, ct);
            copied += read;

            if (copied - lastProgressReport >= progressInterval)
            {
                lastProgressReport = copied;

                var elapsedSec = sw.Elapsed.TotalSeconds;
                var speed = copied / Math.Max(1, elapsedSec); // B/s di QUESTO tentativo (non conta il resume)
                var totalCopied = resumeOffset + copied;

                string etaText = "--";
                if (total.HasValue && speed > 0)
                {
                    var remaining = total.Value - totalCopied;
                    var etaSec = remaining / speed;
                    etaText = TimeSpan.FromSeconds(etaSec).ToString(@"hh\:mm\:ss");
                }

                var percent = total.HasValue ? (int)((totalCopied * 100.0) / total.Value) : 0;

                // ✅ usa ProgressEvents invece di DownloadProgress
                ProgressEvents.NotifyProgressUpdate(
                    Path.GetFileName(path),
                    percent,
                    FormatSpeed(speed),
                    etaText
                );

                // Callback diretta (usata dalla coda download): non richiede di far combaciare
                // il nome file come con il bus statico ProgressEvents, ogni chiamante riceve
                // solo gli aggiornamenti del proprio download.
                onProgress?.Invoke(percent, FormatSpeed(speed), etaText);
            }

            // log ogni 50MB
            if (copied - lastReport >= 50L * 1024 * 1024)
            {
                lastReport = copied;
                var sizeMB = copied / 1024.0 / 1024.0;
                var speed = copied / Math.Max(1, sw.Elapsed.TotalSeconds);

                _log.LogInformation("… {File} {SizeMB:n1} MB ({Speed:n0} B/s)",
                    Path.GetFileName(path), sizeMB, speed);
            }
        }

        await output.FlushAsync(ct);
        return copied;
    }



    private string FormatSpeed(double bytesPerSec)
    {
        string[] sizes = { "B/s", "KB/s", "MB/s", "GB/s" };
        int order = 0;
        while (bytesPerSec >= 1024 && order < sizes.Length - 1)
        {
            order++;
            bytesPerSec /= 1024;
        }
        return $"{bytesPerSec:0.##} {sizes[order]}";
    }



    private static string ExtractSeriesName(string fileName)
    {
        // Il nome può arrivare da fonti non fidate (nome file dichiarato da AllDebrid per
        // il contenuto di un magnet): niente componenti di percorso, per evitare che il
        // nome cartella serie risultante esca da Paths.Tv (es. "..\..\Windows\...").
        fileName = Path.GetFileName(fileName);
        if (fileName.Contains(".."))
            return "Unknown Series";

        var name = fileName.Replace('_', '.');
        var match = System.Text.RegularExpressions.Regex.Match(
            name, @"^(.+?)\.S\d{1,2}E\d{1,2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        string seriesName = match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(name);
        seriesName = seriesName.Replace('.', ' ').Trim();
        while (seriesName.Contains("  ")) seriesName = seriesName.Replace("  ", " ");
        foreach (var c in Path.GetInvalidFileNameChars())
            seriesName = seriesName.Replace(c, ' ');
        if (seriesName.Contains("..")) seriesName = seriesName.Replace("..", "_");
        if (string.IsNullOrWhiteSpace(seriesName)) seriesName = "Unknown Series";
        if (seriesName.Length > 100) seriesName = seriesName.Substring(0, 100);
        return seriesName.Trim();
    }
}
