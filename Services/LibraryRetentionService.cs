using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Retention automatica della feature "Acquisisci in libreria" (docs/piano-premiumize-libreria.md):
/// una volta al minuto controlla ogni <see cref="LibraryAcquisition"/> e cancella da Premiumize
/// (più refresh/emptyTrash su Plex) quelle il cui contenuto è stato visto per intero da almeno
/// <see cref="LibrarySettings.RetentionDays"/> giorni. Nessun fallback automatico tra provider qui
/// (fuori tema): questa retention riguarda solo Premiumize, l'unico provider con storage persistente.
/// </summary>
public class LibraryRetentionService : BackgroundService
{
    // Non un minuto vero: un giorno di retention non richiede un tick così frequente, ma un
    // intervallo corto rende il comportamento verificabile in fase di test (impostando
    // RetentionDays a 0) senza dover aspettare un'ora, e il costo di un giro a vuoto è minimo
    // (poche letture da file JSON locali, nessuna chiamata di rete se non c'è nulla da cancellare).
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);

    private readonly LibraryAcquisitionService _acquisitions;
    private readonly WatchHistoryService _watchHistory;
    private readonly DownloadOrchestrator _orchestrator;
    private readonly ConfigStore _configStore;
    private readonly ILogger<LibraryRetentionService> _log;

    public LibraryRetentionService(
        LibraryAcquisitionService acquisitions,
        WatchHistoryService watchHistory,
        DownloadOrchestrator orchestrator,
        ConfigStore configStore,
        ILogger<LibraryRetentionService> log)
    {
        _acquisitions = acquisitions;
        _watchHistory = watchHistory;
        _orchestrator = orchestrator;
        _configStore = configStore;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "⚠️ Errore nel giro di retention libreria, riprovo al prossimo tick");
            }

            try
            {
                await Task.Delay(TickInterval, ct);
            }
            catch (TaskCanceledException)
            {
                // Arresto dell'app durante l'attesa: uscita pulita dal while.
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var retentionDays = _configStore.Current.Library.RetentionDays;

        foreach (var acquisition in _acquisitions.GetAll())
        {
            var lastWatched = FindLastWatchedIfAllSeen(acquisition);
            if (lastWatched is null) continue; // non (ancora tutto) visto: non si tocca

            if (DateTimeOffset.UtcNow - lastWatched.Value < TimeSpan.FromDays(retentionDays))
                continue; // visto, ma non da abbastanza giorni

            _log.LogInformation(
                "🗑️ Retention: rimuovo \"{Title}\" da Premiumize (transfer {Id}, visto il {When}, oltre i {Days} giorni configurati)",
                acquisition.Title, acquisition.TransferId, lastWatched.Value, retentionDays);

            try
            {
                await _orchestrator.RemoveLibraryAcquisitionAsync(acquisition, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "⚠️ Cancellazione da Premiumize fallita per \"{Title}\" (transfer {Id}), riprovo al prossimo giro",
                    acquisition.Title, acquisition.TransferId);
                // Non rimuovere il tracking: verrà ritentato al prossimo tick. Se RemoveLibraryAcquisitionAsync
                // fosse arrivata a metà (transfer cancellato ma refresh Plex fallito), il tracking
                // resterebbe comunque per un giro in più senza causare doppie cancellazioni: una
                // seconda DeleteMagnetAsync su un id già cancellato fallisce in modo innocuo lato
                // Premiumize (errore "non trovato"), non duplica nulla.
            }
        }
    }

    // Torna la data dell'ultima visione SOLO se l'intero contenuto dell'acquisizione risulta
    // guardato (tutti gli episodi per un pacco stagione, l'unica voce per un film) — altrimenti
    // null, che significa "non toccare ancora" sia per "mai guardato" sia per "guardato solo in
    // parte" (granularità decisa in piano-premiumize-libreria.md: a transfer intero).
    private DateTimeOffset? FindLastWatchedIfAllSeen(LibraryAcquisition acquisition)
    {
        if (acquisition.Episodes is { Count: > 0 } episodes)
        {
            var watchedDates = new List<DateTimeOffset>();
            foreach (var ep in episodes)
            {
                var entry = _watchHistory.TryGet("tv", acquisition.TmdbId, ep.Season, ep.Episode);
                if (entry is null) return null; // almeno un episodio mai guardato
                watchedDates.Add(entry.UpdatedAt);
            }
            return watchedDates.Max();
        }

        var movieEntry = _watchHistory.TryGet("movie", acquisition.TmdbId, null, null);
        return movieEntry?.UpdatedAt;
    }
}
