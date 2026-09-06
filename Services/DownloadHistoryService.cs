using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SendToPlex.Bot.Services;

public class DownloadHistoryEntry
{
    public DateTimeOffset When { get; set; }
    public string Title { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool ToTv { get; set; }
    public string? Source { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Cronologia dei download completati (e falliti) — append-only su un file NDJSON (una riga =
/// un JSON per download), niente database: sopravvive ai riavvii, e il totale scaricato si
/// ricalcola leggendo l'intero file ad ogni apertura della pagina Cronologia (il file non
/// dovrebbe mai crescere abbastanza da rendere il calcolo lento).
/// </summary>
public class DownloadHistoryService
{
    private readonly string _path;
    private readonly ILogger<DownloadHistoryService> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public DownloadHistoryService(ILogger<DownloadHistoryService> log)
    {
        _log = log;
        var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "download-history.jsonl");
    }

    public async Task AppendAsync(DownloadHistoryEntry entry)
    {
        var line = JsonSerializer.Serialize(entry);
        await _writeLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_path, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore scrivendo la cronologia download");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<DownloadHistoryEntry>> ReadAllAsync()
    {
        if (!File.Exists(_path)) return new List<DownloadHistoryEntry>();

        var entries = new List<DownloadHistoryEntry>();
        foreach (var line in await File.ReadAllLinesAsync(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<DownloadHistoryEntry>(line);
                if (entry is not null) entries.Add(entry);
            }
            catch (JsonException)
            {
                // riga corrotta/incompleta (es. scrittura interrotta a metà): la saltiamo.
            }
        }
        return entries;
    }
}
