using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SendToPlex.Bot.Services;

public class WatchProgressEntry
{
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = ""; // "movie" | "tv"
    public string Title { get; set; } = "";
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Completed { get; set; }

    // Link stabile alldebrid.com/f/... (non l'unlock temporaneo, che scade) + nome file — bastano
    // per riaprire direttamente il player da "Continua a guardare" senza rifare la ricerca.
    public string? FileLink { get; set; }
    public string? FileName { get; set; }

    // Provider usato per sbloccare FileLink ("allDebrid"/"realDebrid") — un magnet resta legato a
    // un solo provider per tutta la sua vita (docs/piano-multi-provider-debrid.md): senza questo
    // campo, riprendere un titolo da "Continua a guardare" non saprebbe con quale provider
    // sbloccare il link salvato. Default "allDebrid" per le voci di cronologia scritte prima
    // dell'introduzione di questo campo.
    public string Provider { get; set; } = "allDebrid";
}

/// <summary>
/// Cronologia di visione (Fase 3, docs/piano-restyle-webos-ux.md): un JSON con un dizionario di
/// progressi, chiave "movie:{tmdbId}" o "tv:{tmdbId}:{season}:{episode}". A differenza di
/// DownloadHistoryService (log append-only, adatto a eventi che non cambiano più) qui la STESSA
/// voce va aggiornata molte volte durante una visione (ogni ~20s dal player) — l'intero file viene
/// riscritto ad ogni salvataggio, dimensione irrilevante per uso personale (al più poche centinaia
/// di voci).
/// </summary>
public class WatchHistoryService
{
    private readonly string _path;
    private readonly ILogger<WatchHistoryService> _log;
    private readonly object _lock = new();
    private Dictionary<string, WatchProgressEntry> _entries;

    public WatchHistoryService(ILogger<WatchHistoryService> log)
    {
        _log = log;
        _path = Path.Combine(AppPaths.Data, "watch-history.json");
        _entries = Load();
    }

    private static string KeyFor(string mediaType, int tmdbId, int? season, int? episode) =>
        string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? $"tv:{tmdbId}:{season}:{episode}"
            : $"movie:{tmdbId}";

    private Dictionary<string, WatchProgressEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, WatchProgressEntry>>(json) ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore leggendo la cronologia di visione, riparto vuota");
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
            _log.LogWarning(ex, "⚠️ Errore salvando la cronologia di visione");
        }
    }

    // "Completato" a 90% invece che a fine esatta: i player quasi mai riportano l'ultimo secondo
    // esatto (crediti, buffering finale), 90% è la soglia comune usata anche da altri servizi di
    // streaming per smettere di proporre "riprendi" e considerare un episodio/film visto.
    public void SaveProgress(WatchProgressEntry entry)
    {
        entry.Completed = entry.DurationSeconds > 0 && entry.PositionSeconds >= entry.DurationSeconds * 0.9;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        var key = KeyFor(entry.MediaType, entry.TmdbId, entry.Season, entry.Episode);
        lock (_lock)
        {
            _entries[key] = entry;
            SaveToDisk();
        }
    }

    // Non completati, più recenti prima; per le serie solo l'episodio toccato più di recente per
    // ogni titolo (altrimenti la stessa serie occuperebbe più slot in home con episodi diversi).
    public List<WatchProgressEntry> GetContinueWatching(string mediaType)
    {
        lock (_lock)
        {
            IEnumerable<WatchProgressEntry> candidates = _entries.Values
                .Where(e => string.Equals(e.MediaType, mediaType, StringComparison.OrdinalIgnoreCase) && !e.Completed);

            if (string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
            {
                candidates = candidates
                    .GroupBy(e => e.TmdbId)
                    .Select(g => g.OrderByDescending(e => e.UpdatedAt).First());
            }

            return candidates.OrderByDescending(e => e.UpdatedAt).ToList();
        }
    }

    // Rimozione esplicita da "Continua a guardare" (richiesta utente) — l'utente potrebbe non
    // voler più riprendere un titolo, es. lo ha già finito su un altro dispositivo.
    public void RemoveProgress(string mediaType, int tmdbId, int? season, int? episode)
    {
        var key = KeyFor(mediaType, tmdbId, season, episode);
        lock (_lock)
        {
            if (_entries.Remove(key)) SaveToDisk();
        }
    }

    // Singola voce per chiave esatta (film, o un episodio preciso) — usato da
    // LibraryRetentionService per sapere se un titolo/episodio è stato guardato almeno una volta
    // e, se sì, quando l'ultima volta (per il conteggio dei giorni di retention).
    public WatchProgressEntry? TryGet(string mediaType, int tmdbId, int? season, int? episode)
    {
        var key = KeyFor(mediaType, tmdbId, season, episode);
        lock (_lock)
        {
            return _entries.TryGetValue(key, out var entry) ? entry : null;
        }
    }

    public List<WatchProgressEntry> GetShowHistory(int tmdbId)
    {
        lock (_lock)
        {
            return _entries.Values
                .Where(e => string.Equals(e.MediaType, "tv", StringComparison.OrdinalIgnoreCase) && e.TmdbId == tmdbId)
                .ToList();
        }
    }
}
