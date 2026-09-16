using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SendToPlex.Bot.Services;

public class WatchlistEntry
{
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = ""; // "movie" | "tv"
    public string Title { get; set; } = "";
    public string? Year { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// Watchlist ("da vedere più tardi"): titoli salvati dall'utente dalla pagina di dettaglio, in
/// attesa di essere cercati/guardati. Stesso schema di persistenza di WatchHistoryService (JSON
/// riscritto per intero ad ogni modifica) ma un file separato — semanticamente diversa dalla
/// cronologia di visione (qui non c'è un file/posizione, solo l'intenzione "voglio vederlo").
/// </summary>
public class WatchlistService
{
    private readonly string _path;
    private readonly ILogger<WatchlistService> _log;
    private readonly object _lock = new();
    private Dictionary<string, WatchlistEntry> _entries;

    public WatchlistService(ILogger<WatchlistService> log)
    {
        _log = log;
        _path = Path.Combine(AppPaths.Data, "watchlist.json");
        _entries = Load();
    }

    private static string KeyFor(string mediaType, int tmdbId) =>
        $"{(string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie")}:{tmdbId}";

    private Dictionary<string, WatchlistEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, WatchlistEntry>>(json) ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore leggendo la watchlist, riparto vuota");
            return new();
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore salvando la watchlist");
        }
    }

    public bool IsSaved(string mediaType, int tmdbId)
    {
        lock (_lock) return _entries.ContainsKey(KeyFor(mediaType, tmdbId));
    }

    public void Add(WatchlistEntry entry)
    {
        entry.AddedAt = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            _entries[KeyFor(entry.MediaType, entry.TmdbId)] = entry;
            SaveToDisk();
        }
    }

    public void Remove(string mediaType, int tmdbId)
    {
        lock (_lock)
        {
            if (_entries.Remove(KeyFor(mediaType, tmdbId))) SaveToDisk();
        }
    }

    public List<WatchlistEntry> GetAll()
    {
        lock (_lock) return _entries.Values.OrderByDescending(e => e.AddedAt).ToList();
    }
}
