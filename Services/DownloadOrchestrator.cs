using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

public class DownloadPipelineResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>Avanzamento del file attualmente in trasferimento — usato dalla coda download per
/// mostrare una barra di progresso per elemento, come già succede su Telegram.</summary>
public class DownloadFileProgress
{
    public string FileName { get; set; } = "";
    public int FileIndex { get; set; }
    public int FileCount { get; set; }
    public double Percent { get; set; }
    public string Speed { get; set; } = "";
    public string Eta { get; set; } = "";
}

/// <summary>Esito della sola fase di risoluzione/caricamento magnet (senza scaricare i file):
/// serve a mostrare la lista file di un risultato di ricerca PRIMA di avviare il download, così
/// come già succede per i magnet già pronti nella tab AllDebrid.</summary>
public class MagnetReadyResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? MagnetId { get; set; }
}

/// <summary>
/// Pipeline di download condivisa: risoluzione magnet → upload AllDebrid → attesa "ready" →
/// sblocco file → salvataggio su disco → refresh Plex → cronologia. Prima viveva solo dentro
/// Search.razor; è stata estratta qui perché la stessa logica serve anche alla tab AllDebrid
/// (Punto 1), alla coda download (Punto 9) e alla cronologia (Punto 10) — evita di copiarla
/// tre volte.
/// </summary>
public class DownloadOrchestrator
{
    private readonly DebridProviderFactory _debridFactory;
    private readonly Downloader _downloader;
    private readonly PlexClient _plex;
    private readonly TorrentSearchService _torrentSearch;
    private readonly NordVpnController _vpn;
    private readonly DownloadHistoryService _history;
    private readonly LibraryAcquisitionService _libraryAcquisitions;

    public DownloadOrchestrator(
        DebridProviderFactory debridFactory,
        Downloader downloader,
        PlexClient plex,
        TorrentSearchService torrentSearch,
        NordVpnController vpn,
        DownloadHistoryService history,
        LibraryAcquisitionService libraryAcquisitions)
    {
        _debridFactory = debridFactory;
        _downloader = downloader;
        _plex = plex;
        _torrentSearch = torrentSearch;
        _vpn = vpn;
        _history = history;
        _libraryAcquisitions = libraryAcquisitions;
    }

    /// <summary>
    /// Percorso completo a partire da un risultato di ricerca torrent: risolve il magnet (se
    /// serve, dalla pagina di dettaglio), lo carica su AllDebrid, aspetta che sia pronto, poi
    /// delega a <see cref="DownloadReadyMagnetAsync"/> per lo scaricamento vero e proprio.
    /// </summary>
    public async Task<DownloadPipelineResult> DownloadFromSearchResultAsync(
        TorrentSearchResult result,
        bool requiresVpn,
        bool toTv,
        int timeoutSeconds,
        CancellationToken ct,
        Action<string>? onStatus = null,
        int? sectionId = null,
        Action<DownloadFileProgress>? onFileProgress = null,
        IReadOnlySet<string>? selectedFileLinks = null,
        DebridProvider provider = DebridProvider.AllDebrid)
    {
        var prep = await PrepareSearchResultAsync(result, requiresVpn, toTv, timeoutSeconds, ct, onStatus, provider);
        if (!prep.Success)
            return Fail(prep.Message);

        return await DownloadReadyMagnetAsync(prep.MagnetId!, result.Title, toTv, result.SourceListName ?? result.SiteName, ct, onStatus, sectionId, onFileProgress, selectedFileLinks, provider);
    }

    /// <summary>
    /// Risolve il magnet di un risultato di ricerca e lo carica su AllDebrid, aspettando che sia
    /// pronto, SENZA scaricare nulla. Serve per poter mostrare all'utente l'elenco file (checkbox
    /// di selezione, come nella tab AllDebrid) prima di avviare il download vero — cosa che
    /// <see cref="DownloadFromSearchResultAsync"/> da solo non permette perché scarica subito
    /// tutti i file non appena il magnet è pronto.
    /// </summary>
    public async Task<MagnetReadyResult> PrepareSearchResultAsync(
        TorrentSearchResult result,
        bool requiresVpn,
        bool toTv,
        int timeoutSeconds,
        CancellationToken ct,
        Action<string>? onStatus = null,
        DebridProvider provider = DebridProvider.AllDebrid)
    {
        var debrid = _debridFactory.Get(provider);
        var magnet = result.Magnet;
        if (magnet is null && result.DetailUrl is not null && !string.IsNullOrWhiteSpace(result.MagnetSelectorOnDetailPage))
        {
            // Se il sito richiede la VPN per essere raggiungibile, deve restare connessa anche
            // per risolvere il magnet dalla pagina di dettaglio, non solo per la ricerca: la
            // disconnessione va fatta DOPO, solo prima della vera chiamata ad AllDebrid.
            if (requiresVpn)
                await _vpn.ConnectAsync(ct);

            onStatus?.Invoke("🔗 Risolvo il magnet dalla pagina di dettaglio…");
            var magnets = await _torrentSearch.ResolveMagnetsFromDetailPageAsync(
                result.DetailUrl, result.MagnetSelectorOnDetailPage!, result.Cookie, timeoutSeconds, ct);
            magnet = magnets.FirstOrDefault();
        }

        if (magnet is null)
        {
            await LogFailureAsync(result.Title, toTv, result.SourceListName ?? result.SiteName, "Impossibile ottenere un magnet per questo risultato.");
            return new MagnetReadyResult { Success = false, Message = "❌ Impossibile ottenere un magnet per questo risultato." };
        }

        // Il download vero passa da AllDebrid, non dal sito di origine: disconnette la VPN (se
        // gestita in automatico) prima di scaricare, per la piena velocità diretta.
        await _vpn.DisconnectAsync(ct);

        onStatus?.Invoke("🧲 Magnet caricato, controllo disponibilità…");
        var id = await debrid.UploadMagnetAsync(magnet, ct);

        var ready = await debrid.WaitReadyAsync(id, TimeSpan.FromMinutes(60), ct);
        if (!ready)
        {
            await debrid.DeleteMagnetAsync(id, ct);
            await LogFailureAsync(result.Title, toTv, result.SourceListName ?? result.SiteName, "Magnet non pronto in tempo (timeout 60 minuti).");
            return new MagnetReadyResult { Success = false, Message = "⚠️ Magnet non pronto in tempo. Annullato." };
        }

        return new MagnetReadyResult { Success = true, MagnetId = id, Message = "✅ Magnet pronto." };
    }

    /// <summary>
    /// Scarica tutti i file di un magnet già presente e pronto su AllDebrid (sblocco + salvataggio
    /// + refresh Plex + cronologia). Usato sia a valle di <see cref="DownloadFromSearchResultAsync"/>
    /// sia direttamente dalla tab AllDebrid per un magnet già sull'account.
    /// </summary>
    public async Task<DownloadPipelineResult> DownloadReadyMagnetAsync(
        string magnetId,
        string displayTitle,
        bool toTv,
        string? sourceName,
        CancellationToken ct,
        Action<string>? onStatus = null,
        int? sectionId = null,
        Action<DownloadFileProgress>? onFileProgress = null,
        IReadOnlySet<string>? selectedFileLinks = null,
        DebridProvider provider = DebridProvider.AllDebrid)
    {
        var debrid = _debridFactory.Get(provider);
        var files = await debrid.GetMagnetFilesDetailedAsync(magnetId, ct);
        if (selectedFileLinks is not null)
            files = files.Where(f => selectedFileLinks.Contains(f.Link)).ToList();

        if (files.Count == 0)
        {
            await LogFailureAsync(displayTitle, toTv, sourceName, "Nessun file trovato per questo magnet.");
            return Fail("❌ Nessun file trovato per questo magnet.");
        }

        onStatus?.Invoke(files.Count == 1 ? "🔓 Trovato 1 file, inizio download…" : $"🔓 Trovati {files.Count} file, scarico tutti in sequenza…");

        var savedAny = false;
        var plexRefreshOk = true;
        string? archiveWarning = null;
        var fileIndex = 0;

        foreach (var file in files)
        {
            fileIndex++;
            var thisFileIndex = fileIndex;

            var direct = await debrid.UnlockLinkAsync(file.Link, ct);
            var (savedPath, sizeBytes) = await _downloader.SaveOneAsync(direct, toTv, ct, sectionId,
                onProgress: (percent, speed, eta) => onFileProgress?.Invoke(new DownloadFileProgress
                {
                    FileName = file.Name,
                    FileIndex = thisFileIndex,
                    FileCount = files.Count,
                    Percent = percent,
                    Speed = speed,
                    Eta = eta
                }));

            var ext = Path.GetExtension(savedPath).ToLowerInvariant();
            if (ext is ".zip" or ".rar")
            {
                archiveWarning = $"⚠️ {Path.GetFileName(savedPath)} è un archivio: l'estrazione automatica da qui non è ancora supportata, il file resta compresso nella cartella.";
                onStatus?.Invoke(archiveWarning);
                continue;
            }

            savedAny = true;
            var refreshOk = await _plex.RefreshAsync(toTv, ct, sectionId);
            if (!refreshOk) plexRefreshOk = false;

            await _history.AppendAsync(new DownloadHistoryEntry
            {
                When = DateTimeOffset.UtcNow,
                Title = Path.GetFileName(savedPath),
                SizeBytes = sizeBytes,
                ToTv = toTv,
                Source = sourceName,
                Success = true
            });
        }

        var message = !savedAny
            ? (archiveWarning ?? "✅ Download completato.")
            : plexRefreshOk
                ? "✅ Download completato. 🔄 Plex: aggiornamento avviato."
                : "✅ Download completato. ⚠️ Refresh Plex non riuscito: aggiorna la libreria manualmente da Plex.";

        return new DownloadPipelineResult { Success = savedAny, Message = message };
    }

    // Pattern SxxExx nel nome file (duplicato volutamente da Downloader.SeasonEpisodeRegex, stesso
    // principio, chiamante separato: qui serve sui nomi restituiti da Premiumize, non su file già
    // sul disco) — usato per dedurre quali episodi contiene un pacco stagione al momento
    // dell'acquisizione, così la retention (LibraryRetentionService) sa quando può considerarlo
    // "tutto visto".
    private static readonly Regex SeasonEpisodeRegex = new(@"S(\d{1,2})E(\d{1,3})", RegexOptions.IgnoreCase);

    /// <summary>
    /// "Acquisisci in libreria" (docs/piano-premiumize-libreria.md): a differenza di
    /// <see cref="DownloadReadyMagnetAsync"/>, non scarica nulla su disco — il magnet è già stato
    /// caricato su Premiumize (che lo salva nel suo cloud, mount WebDAV già configurato come
    /// cartella della sezione Plex indicata da <paramref name="sectionId"/>) — qui si registra solo
    /// il tracking (<see cref="LibraryAcquisitionService"/>, serve alla retention automatica) e si
    /// chiede a Plex di scansionare la sezione per accorgersi del nuovo file.
    /// </summary>
    public async Task<DownloadPipelineResult> AcquireReadyMagnetToLibraryAsync(
        string magnetId,
        string displayTitle,
        bool toTv,
        int tmdbId,
        int sectionId,
        CancellationToken ct,
        Action<string>? onStatus = null)
    {
        var debrid = _debridFactory.Get(DebridProvider.Premiumize);
        var files = await debrid.GetMagnetFilesDetailedAsync(magnetId, ct);
        if (files.Count == 0)
        {
            await LogFailureAsync(displayTitle, toTv, "Premiumize", "Nessun file trovato per questo magnet.");
            return Fail("❌ Nessun file trovato per questo magnet.");
        }

        List<AcquiredEpisode>? episodes = null;
        if (toTv)
        {
            episodes = files
                .Select(f => SeasonEpisodeRegex.Match(f.Name))
                .Where(m => m.Success)
                .Select(m => new AcquiredEpisode { Season = int.Parse(m.Groups[1].Value), Episode = int.Parse(m.Groups[2].Value) })
                .DistinctBy(e => (e.Season, e.Episode))
                .ToList();

            if (episodes.Count == 0)
            {
                // Nessun nome file ha combaciato col pattern SxxExx: non possiamo sapere quali
                // episodi contiene, quindi la retention automatica non potrebbe mai considerarlo
                // "tutto visto" — meglio fallire qui con un messaggio chiaro che lasciare
                // un'acquisizione che non verrà mai ripulita da sola.
                await LogFailureAsync(displayTitle, toTv, "Premiumize", "Impossibile riconoscere gli episodi (pattern SxxExx non trovato nei nomi file) — la retention automatica non potrebbe funzionare per questo pacco.");
                return Fail("❌ Impossibile riconoscere gli episodi in questo pacco. Usa lo streaming/download normale per questo file.");
            }
        }

        _libraryAcquisitions.Add(new LibraryAcquisition
        {
            TransferId = magnetId,
            SectionId = sectionId,
            ToTv = toTv,
            Title = displayTitle,
            TmdbId = tmdbId,
            Episodes = episodes,
            AcquiredAt = DateTimeOffset.UtcNow
        });

        onStatus?.Invoke("📚 Aggiunto alla libreria, aggiorno Plex…");
        var refreshOk = await _plex.RefreshAsync(toTv, ct, sectionId);

        await _history.AppendAsync(new DownloadHistoryEntry
        {
            When = DateTimeOffset.UtcNow,
            Title = displayTitle,
            SizeBytes = files.Sum(f => f.Size),
            ToTv = toTv,
            Source = "Premiumize (libreria)",
            Success = true
        });

        return new DownloadPipelineResult
        {
            Success = true,
            Message = refreshOk
                ? "✅ Acquisito in libreria. 🔄 Plex: aggiornamento avviato."
                : "✅ Acquisito in libreria. ⚠️ Refresh Plex non riuscito: aggiorna la libreria manualmente da Plex."
        };
    }

    /// <summary>
    /// Rimuove un'acquisizione Premiumize: cancella il transfer, aggiorna/svuota il cestino Plex
    /// sulla sezione, toglie il tracking. Usato sia da <see cref="LibraryRetentionService"/>
    /// (automatico, dopo la visione) sia dalla sezione "Premiumize" della pagina Libreria
    /// (manuale, richiesta esplicita dell'utente) — un solo punto invece di duplicare le stesse 4
    /// chiamate in due posti.
    /// </summary>
    public async Task RemoveLibraryAcquisitionAsync(LibraryAcquisition acquisition, CancellationToken ct)
    {
        var debrid = _debridFactory.Get(DebridProvider.Premiumize);
        await debrid.DeleteMagnetAsync(acquisition.TransferId, ct);
        await _plex.RefreshAsync(acquisition.ToTv, ct, acquisition.SectionId);
        await _plex.EmptyTrashAsync(acquisition.SectionId, ct);
        _libraryAcquisitions.Remove(acquisition.TransferId);
    }

    private async Task LogFailureAsync(string title, bool toTv, string? source, string error)
    {
        await _history.AppendAsync(new DownloadHistoryEntry
        {
            When = DateTimeOffset.UtcNow,
            Title = title,
            SizeBytes = 0,
            ToTv = toTv,
            Source = source,
            Success = false,
            Error = error
        });
    }

    private static DownloadPipelineResult Fail(string message) => new() { Success = false, Message = message };
}
