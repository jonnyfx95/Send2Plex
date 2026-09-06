using System.Text.Json;

namespace SendToPlex.Bot.Models;

public class WatchlistItem
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string SiteName { get; set; } = ""; // per ritrovare Cookie/UseBrowser/selettori del sito al momento del check
}

/// <summary>
/// Elenco locale e persistente di link diretti (es. topic di una serie TV) salvati dall'utente
/// per ricontrollarli velocemente senza rifare una ricerca — utile per serie con episodi
/// settimanali dove serve rivisitare spesso lo stesso link.
/// </summary>
public static class WatchlistManager
{
    private static readonly string WatchlistPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "watchlist.json");

    public static List<WatchlistItem> Load()
    {
        try
        {
            if (!File.Exists(WatchlistPath)) return new List<WatchlistItem>();

            var json = File.ReadAllText(WatchlistPath);
            var items = JsonSerializer.Deserialize<List<WatchlistItem>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return items ?? new List<WatchlistItem>();
        }
        catch
        {
            return new List<WatchlistItem>();
        }
    }

    public static void Save(List<WatchlistItem> items)
    {
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(WatchlistPath, json);
    }
}
