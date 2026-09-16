using System.Text.Json;
using Microsoft.Extensions.Logging;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Persistenza delle acquisizioni Premiumize (docs/piano-premiumize-libreria.md) — stesso schema
/// JSON-su-disco di <see cref="WatchHistoryService"/> (un dizionario riscritto per intero ad ogni
/// salvataggio, dimensione irrilevante per uso personale). Chiave = <see cref="LibraryAcquisition.TransferId"/>,
/// unico id stabile che lega un'acquisizione al transfer Premiumize corrispondente.
/// </summary>
public class LibraryAcquisitionService
{
    private readonly string _path;
    private readonly ILogger<LibraryAcquisitionService> _log;
    private readonly object _lock = new();
    private Dictionary<string, LibraryAcquisition> _entries;

    public LibraryAcquisitionService(ILogger<LibraryAcquisitionService> log)
    {
        _log = log;
        _path = Path.Combine(AppPaths.Data, "library-acquisitions.json");
        _entries = Load();
    }

    private Dictionary<string, LibraryAcquisition> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, LibraryAcquisition>>(json) ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore leggendo le acquisizioni libreria, riparto vuote");
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
            _log.LogWarning(ex, "⚠️ Errore salvando le acquisizioni libreria");
        }
    }

    public void Add(LibraryAcquisition acquisition)
    {
        lock (_lock)
        {
            _entries[acquisition.TransferId] = acquisition;
            SaveToDisk();
        }
    }

    public List<LibraryAcquisition> GetAll()
    {
        lock (_lock) return _entries.Values.ToList();
    }

    public List<LibraryAcquisition> GetForSection(int sectionId)
    {
        lock (_lock) return _entries.Values.Where(a => a.SectionId == sectionId).ToList();
    }

    // Usato dalla pagina Libreria (tab Premiumize/badge) per capire se un elemento della griglia
    // Plex corrisponde a un'acquisizione tracciata — join per TmdbId, non per titolo (a differenza
    // del confronto testuale usato altrove per la libreria Plex, qui l'id è affidabile perché lo
    // decidiamo noi al momento dell'acquisizione).
    public LibraryAcquisition? FindByTmdbId(int tmdbId, bool toTv)
    {
        lock (_lock) return _entries.Values.FirstOrDefault(a => a.TmdbId == tmdbId && a.ToTv == toTv);
    }

    // Usato dallo sfoglio bulk della libreria (/api/tv/library/all, Library.razor): a differenza
    // di FindByTmdbId, un elemento della griglia Plex non porta con sé un TmdbId (PlexLibraryItem
    // non lo ha, vedi PlexClient — Plex non lo espone, solo titolo/anno/poster), quindi qui si
    // confronta per titolo normalizzato, stesso identico principio di
    // TvApiEndpoints.TitlesMatch (duplicato volutamente, chiamante separato).
    public LibraryAcquisition? FindByTitle(string title, bool toTv)
    {
        var norm = NormalizeForMatch(title);
        if (norm.Length == 0) return null;

        lock (_lock)
        {
            return _entries.Values.FirstOrDefault(a =>
                a.ToTv == toTv &&
                NormalizeForMatch(a.Title) is var an && an.Length > 0 &&
                (an == norm || an.Contains(norm) || norm.Contains(an)));
        }
    }

    private static string NormalizeForMatch(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public LibraryAcquisition? Get(string transferId)
    {
        lock (_lock) return _entries.GetValueOrDefault(transferId);
    }

    public void Remove(string transferId)
    {
        lock (_lock)
        {
            if (_entries.Remove(transferId)) SaveToDisk();
        }
    }

    // Condiviso tra Library.razor (tab Premiumize) e TvApiEndpoints (bulk JSON per webOS) — un solo
    // punto invece di due copie della stessa logica di "tutto visto?"/"quanti episodi su quanti".
    public static string DescribeWatchStatus(LibraryAcquisition a, WatchHistoryService watchHistory)
    {
        if (a.Episodes is { Count: > 0 } episodes)
        {
            var watchedCount = episodes.Count(ep => watchHistory.TryGet("tv", a.TmdbId, ep.Season, ep.Episode) is not null);
            if (watchedCount == 0) return "— Mai visto";
            return watchedCount == episodes.Count ? "✅ Tutto visto" : $"👁️ {watchedCount}/{episodes.Count} episodi visti";
        }

        return watchHistory.TryGet("movie", a.TmdbId, null, null) is not null ? "✅ Visto" : "— Mai visto";
    }
}
