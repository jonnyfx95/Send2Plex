using Microsoft.Extensions.Options;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

public enum QueuedDownloadStatus { Queued, Downloading, Done, Failed }

public class QueuedDownloadItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string? SizeText { get; set; }
    public string? SourceName { get; set; }
    public bool ToTv { get; set; }
    public int? SectionId { get; set; }
    public string? DestinationLabel { get; set; }
    public bool RequiresVpn { get; set; }
    public int TimeoutSeconds { get; set; } = 15;

    // Uno dei due è valorizzato: SearchResult per un elemento accodato da Cerca (il magnet va
    // ancora risolto/caricato su AllDebrid), MagnetId per un elemento già pronto sull'account
    // (accodato dalla tab AllDebrid).
    public TorrentSearchResult? SearchResult { get; set; }
    public string? MagnetId { get; set; }

    public QueuedDownloadStatus Status { get; set; } = QueuedDownloadStatus.Queued;
    public string? Message { get; set; }
}

/// <summary>
/// Coda di download in memoria (Punto 9): l'utente accoda più risultati mentre cerca, poi li fa
/// partire tutti insieme, uno alla volta (rispettando Download.MaxParallel). Non persiste tra
/// riavvii per scelta — è un'accoda-e-avvia per la sessione corrente, non un archivio.
/// </summary>
public class DownloadQueueService
{
    private readonly DownloadOrchestrator _orchestrator;
    private readonly ConfigStore _configStore;
    private readonly object _lock = new();
    private readonly List<QueuedDownloadItem> _items = new();
    private bool _running;

    private DownloadSettings _cfg => _configStore.Current.Download;

    public event Action? Changed;

    public DownloadQueueService(DownloadOrchestrator orchestrator, ConfigStore configStore)
    {
        _orchestrator = orchestrator;
        _configStore = configStore;
    }

    public bool IsRunning => _running;

    public List<QueuedDownloadItem> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public int QueuedCount
    {
        get { lock (_lock) return _items.Count(i => i.Status == QueuedDownloadStatus.Queued); }
    }

    public void Enqueue(QueuedDownloadItem item)
    {
        lock (_lock) _items.Add(item);
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        lock (_lock) _items.RemoveAll(i => i.Id == id);
        Changed?.Invoke();
    }

    public void ClearCompleted()
    {
        lock (_lock) _items.RemoveAll(i => i.Status is QueuedDownloadStatus.Done or QueuedDownloadStatus.Failed);
        Changed?.Invoke();
    }

    /// <summary>
    /// Avvia tutti gli elementi in coda, rispettando Download.MaxParallel. Se già in esecuzione,
    /// non fa nulla (evita di avviare due volte la stessa coda da click multipli).
    /// </summary>
    public async Task StartAllAsync(CancellationToken ct)
    {
        if (_running) return;
        _running = true;
        Changed?.Invoke();

        try
        {
            var pending = Items.Where(i => i.Status == QueuedDownloadStatus.Queued).ToList();
            using var throttle = new SemaphoreSlim(Math.Max(1, _cfg.MaxParallel));

            var tasks = pending.Select(async item =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    await RunOneAsync(item, ct);
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        finally
        {
            _running = false;
            Changed?.Invoke();
        }
    }

    private async Task RunOneAsync(QueuedDownloadItem item, CancellationToken ct)
    {
        item.Status = QueuedDownloadStatus.Downloading;
        item.Message = "In corso…";
        Changed?.Invoke();

        try
        {
            var result = item.SearchResult is not null
                ? await _orchestrator.DownloadFromSearchResultAsync(
                    item.SearchResult, item.RequiresVpn, item.ToTv, item.TimeoutSeconds, ct,
                    onStatus: msg => { item.Message = msg; Changed?.Invoke(); },
                    sectionId: item.SectionId)
                : await _orchestrator.DownloadReadyMagnetAsync(
                    item.MagnetId!, item.Title, item.ToTv, item.SourceName, ct,
                    onStatus: msg => { item.Message = msg; Changed?.Invoke(); },
                    sectionId: item.SectionId);

            item.Status = result.Success ? QueuedDownloadStatus.Done : QueuedDownloadStatus.Failed;
            item.Message = result.Message;
        }
        catch (Exception ex)
        {
            item.Status = QueuedDownloadStatus.Failed;
            item.Message = $"❌ Errore: {ex.Message}";
        }
        finally
        {
            Changed?.Invoke();
        }
    }
}
