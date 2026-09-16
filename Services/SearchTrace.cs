namespace SendToPlex.Bot.Services;

/// <summary>
/// Log dedicato, dettagliato e in chiaro (non tramite Serilog, per evitare il rumore del
/// log generale) per la pipeline di ricerca torrent (TorrentSearchService).
/// Utile per il debug: registra ogni fetch ed esito passo-passo in un file leggibile.
/// </summary>
internal static class SearchTrace
{
    private static readonly object Lock = new();
    private static readonly string LogPath = Path.Combine(AppPaths.Logs, "torrent_search_trace.log");

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // solo diagnostica: un fallimento qui non deve mai interrompere il flusso principale
        }
    }
}
