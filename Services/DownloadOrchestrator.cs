using Microsoft.Extensions.Options;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

public class DownloadPipelineResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
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
    private readonly AllDebridClient _allDebrid;
    private readonly Downloader _downloader;
    private readonly PlexClient _plex;
    private readonly TorrentSearchService _torrentSearch;
    private readonly NordVpnController _vpn;
    private readonly DownloadHistoryService _history;

    public DownloadOrchestrator(
        AllDebridClient allDebrid,
        Downloader downloader,
        PlexClient plex,
        TorrentSearchService torrentSearch,
        NordVpnController vpn,
        DownloadHistoryService history)
    {
        _allDebrid = allDebrid;
        _downloader = downloader;
        _plex = plex;
        _torrentSearch = torrentSearch;
        _vpn = vpn;
        _history = history;
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
        int? sectionId = null)
    {
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
                result.DetailUrl, result.MagnetSelectorOnDetailPage!, result.Cookie, result.UseBrowser, result.RevealClickSelector, timeoutSeconds, ct);
            magnet = magnets.FirstOrDefault();
        }

        if (magnet is null)
        {
            await LogFailureAsync(result.Title, toTv, result.SourceListName ?? result.SiteName, "Impossibile ottenere un magnet per questo risultato.");
            return Fail("❌ Impossibile ottenere un magnet per questo risultato.");
        }

        // Il download vero passa da AllDebrid, non dal sito di origine: disconnette la VPN (se
        // gestita in automatico) prima di scaricare, per la piena velocità diretta.
        await _vpn.DisconnectAsync(ct);

        onStatus?.Invoke("🧲 Magnet caricato su AllDebrid, controllo disponibilità…");
        var id = await _allDebrid.UploadMagnetAsync(magnet, ct);

        var ready = await _allDebrid.WaitReadyAsync(id, TimeSpan.FromMinutes(60), ct);
        if (!ready)
        {
            await _allDebrid.DeleteMagnetAsync(id, ct);
            await LogFailureAsync(result.Title, toTv, result.SourceListName ?? result.SiteName, "Magnet non pronto in tempo (timeout 60 minuti).");
            return Fail("⚠️ Magnet non pronto in tempo. Annullato.");
        }

        return await DownloadReadyMagnetAsync(id, result.Title, toTv, result.SourceListName ?? result.SiteName, ct, onStatus, sectionId);
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
        int? sectionId = null)
    {
        var files = await _allDebrid.GetMagnetFilesDetailedAsync(magnetId, ct);
        if (files.Count == 0)
        {
            await LogFailureAsync(displayTitle, toTv, sourceName, "Nessun file trovato per questo magnet.");
            return Fail("❌ Nessun file trovato per questo magnet.");
        }

        onStatus?.Invoke(files.Count == 1 ? "🔓 Trovato 1 file, inizio download…" : $"🔓 Trovati {files.Count} file, scarico tutti in sequenza…");

        var savedAny = false;
        var plexRefreshOk = true;
        string? archiveWarning = null;

        foreach (var file in files)
        {
            var direct = await _allDebrid.UnlockLinkAsync(file.Link, ct);
            var (savedPath, sizeBytes) = await _downloader.SaveOneAsync(direct, toTv, ct, sectionId);

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
