namespace SendToPlex.Bot.Services;

/// <summary>
/// Radice unica per tutto lo stato persistente dell'app (config, cronologia, log, ecc.) — prima
/// di questa classe ogni servizio calcolava per conto proprio
/// <c>AppDomain.CurrentDomain.BaseDirectory</c>, senza un punto unico da ridirigere altrove.
/// Necessario per il porting Docker (docs/idee-porting-multipiattaforma.md): in un container solo
/// un volume montato sopravvive a un aggiornamento dell'immagine, quindi tutto deve poter finire
/// sotto un'unica cartella esterna invece che accanto all'eseguibile.
/// </summary>
public static class AppPaths
{
    // SEND2PLEX_DATA_DIR non impostata -> comportamento IDENTICO a prima di questa classe
    // (cartella dell'eseguibile) — le due copie Windows già in uso non cambiano percorso. Il
    // Dockerfile imposta SEND2PLEX_DATA_DIR=/data, l'unico volume da montare per persistere tutto.
    public static readonly string Root =
        Environment.GetEnvironmentVariable("SEND2PLEX_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : AppDomain.CurrentDomain.BaseDirectory;

    public static string Data => EnsureDir(Path.Combine(Root, "data"));
    public static string Logs => EnsureDir(Path.Combine(Root, "logs"));
    public static string SearchHistory => EnsureDir(Path.Combine(Root, "search_history"));
    public static string DataProtectionKeys => EnsureDir(Path.Combine(Root, "dp-keys"));
    public static string ConfigFile => Path.Combine(Root, "appsettings.json");

    // File legacy a parte da "data/watchlist.json" (WatchlistService, uso web) — questo è
    // l'elenco link usato solo dal bot Telegram (Models/WatchlistItem.cs). Nomi quasi identici ma
    // file distinti già oggi: portato così com'è, non è oggetto di questo porting.
    public static string LegacyWatchlistFile => Path.Combine(Root, "watchlist.json");

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
