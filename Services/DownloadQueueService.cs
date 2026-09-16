using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

public enum QueuedDownloadStatus { Queued, Downloading, Done, Failed, Cancelled }

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

    // Se valorizzato, scarica solo questi file invece di tutti quelli del magnet (scelti
    // dall'utente prima di accodare, sia dalla tab AllDebrid sia da Cerca dopo aver risolto il
    // magnet con "Scegli file").
    public HashSet<string>? SelectedFileLinks { get; set; }

    // Provider debrid da usare per questo elemento (docs/piano-multi-provider-debrid.md) — scelto
    // per riga al momento di accodare, resta legato all'elemento per tutto il suo ciclo di vita.
    public DebridProvider Provider { get; set; } = DebridProvider.AllDebrid;

    // Valorizzati solo quando l'elemento arriva dalla card episodio/film dell'app webOS: permette
    // di mostrare una barra di progresso anche nella pagina di dettaglio del titolo (non solo nella
    // sezione Download dedicata), filtrando gli elementi di questa coda per tmdbId.
    public int? TmdbId { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }

    public QueuedDownloadStatus Status { get; set; } = QueuedDownloadStatus.Queued;
    public string? Message { get; set; }

    // Non esposto in UI: permette a Stop(id) di annullare solo il download di QUESTO elemento,
    // senza toccare gli altri in corso.
    internal CancellationTokenSource? Cts { get; set; }

    // Avanzamento del file attualmente in trasferimento (come mostrato su Telegram) — valorizzati
    // solo mentre Status == Downloading e solo per il file che sta effettivamente scaricando ora.
    public string? CurrentFileName { get; set; }
    public int CurrentFileIndex { get; set; }
    public int TotalFiles { get; set; }
    public double Percent { get; set; }
    public string? Speed { get; set; }
    public string? Eta { get; set; }
}

/// <summary>
/// Coda di download in memoria (Punto 9): l'utente accoda più risultati mentre cerca, poi li fa
/// partire tutti insieme. La risoluzione magnet/attesa "ready" procede in parallelo per tutti gli
/// elementi in coda; solo il trasferimento vero e proprio rispetta Download.MaxParallel (vedi
/// Downloader.SaveOneAsync), così un magnet lento non blocca gli altri elementi in coda. Non
/// persiste tra riavvii per scelta — è un'accoda-e-avvia per la sessione corrente, non un archivio.
/// </summary>
public class DownloadQueueService
{
    private readonly DownloadOrchestrator _orchestrator;
    private readonly object _lock = new();
    private readonly List<QueuedDownloadItem> _items = new();
    private int _activeRuns;

    public event Action? Changed;

    public DownloadQueueService(DownloadOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public bool IsRunning => Volatile.Read(ref _activeRuns) > 0;

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
        lock (_lock) _items.RemoveAll(i => i.Status is QueuedDownloadStatus.Done or QueuedDownloadStatus.Failed or QueuedDownloadStatus.Cancelled);
        Changed?.Invoke();
    }

    /// <summary>Ferma il download in corso per un singolo elemento, senza toccare gli altri in
    /// coda o già in esecuzione. Il file parziale eventualmente scaricato viene scartato (stesso
    /// comportamento di un annullamento esplicito in Downloader.SaveOneAsync) — non è una pausa
    /// con ripresa successiva.</summary>
    public void Stop(Guid id)
    {
        QueuedDownloadItem? item;
        lock (_lock) item = _items.FirstOrDefault(i => i.Id == id);
        item?.Cts?.Cancel();
    }

    /// <summary>
    /// Avvia tutti gli elementi attualmente in coda. Non blocca in attesa di elementi aggiunti
    /// dopo: se richiami questo metodo mentre un giro precedente è ancora attivo (es. perché un
    /// magnet sta impiegando tempo a diventare pronto su AllDebrid), il nuovo giro processa solo
    /// quelli con stato Queued in quel momento, senza toccare quelli già in corso — così accodare
    /// e avviare nuovi elementi resta possibile anche con un download bloccato in attesa.
    /// Il limite Download.MaxParallel si applica solo al trasferimento vero e proprio dentro
    /// Downloader.SaveOneAsync, non a questa fase di orchestrazione (risoluzione magnet/attesa
    /// "ready" su AllDebrid non consuma banda né disco, quindi qui non ha senso serializzarla).
    /// </summary>
    public async Task StartAllAsync(CancellationToken ct)
    {
        var pending = Items.Where(i => i.Status == QueuedDownloadStatus.Queued).ToList();
        if (pending.Count == 0) return;

        Interlocked.Increment(ref _activeRuns);
        Changed?.Invoke();
        try
        {
            await Task.WhenAll(pending.Select(item => RunOneAsync(item, ct)));
        }
        finally
        {
            Interlocked.Decrement(ref _activeRuns);
            Changed?.Invoke();
        }
    }

    private async Task RunOneAsync(QueuedDownloadItem item, CancellationToken ct)
    {
        item.Status = QueuedDownloadStatus.Downloading;
        item.Message = "In corso…";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        item.Cts = cts;
        var itemCt = cts.Token;
        Changed?.Invoke();

        void OnFileProgress(DownloadFileProgress p)
        {
            item.CurrentFileName = p.FileName;
            item.CurrentFileIndex = p.FileIndex;
            item.TotalFiles = p.FileCount;
            item.Percent = p.Percent;
            item.Speed = p.Speed;
            item.Eta = p.Eta;
            Changed?.Invoke();
        }

        try
        {
            var result = item.SearchResult is not null
                ? await _orchestrator.DownloadFromSearchResultAsync(
                    item.SearchResult, item.RequiresVpn, item.ToTv, item.TimeoutSeconds, itemCt,
                    onStatus: msg => { item.Message = msg; Changed?.Invoke(); },
                    sectionId: item.SectionId,
                    onFileProgress: OnFileProgress,
                    selectedFileLinks: item.SelectedFileLinks,
                    provider: item.Provider)
                : await _orchestrator.DownloadReadyMagnetAsync(
                    item.MagnetId!, item.Title, item.ToTv, item.SourceName, itemCt,
                    onStatus: msg => { item.Message = msg; Changed?.Invoke(); },
                    sectionId: item.SectionId,
                    onFileProgress: OnFileProgress,
                    selectedFileLinks: item.SelectedFileLinks,
                    provider: item.Provider);

            item.Status = result.Success ? QueuedDownloadStatus.Done : QueuedDownloadStatus.Failed;
            item.Message = result.Message;
        }
        catch (OperationCanceledException) when (itemCt.IsCancellationRequested)
        {
            item.Status = QueuedDownloadStatus.Cancelled;
            item.Message = "⏹️ Fermato dall'utente.";
        }
        catch (Exception ex)
        {
            item.Status = QueuedDownloadStatus.Failed;
            item.Message = $"❌ Errore: {ex.Message}";
        }
        finally
        {
            item.Percent = 0;
            item.CurrentFileName = null;
            item.Cts = null;
            cts.Dispose();
            Changed?.Invoke();
        }
    }
}
