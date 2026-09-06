namespace SendToPlex.Bot.UI;

public static class ProgressEvents
{
    public static Action<string>? OnDownloadStart;
    public static Action<string, double, string, string>? OnProgressUpdate; // con ETA
    public static Action<string, string>? OnDownloadComplete;

    public static void NotifyDownloadStart(string filename)
    {
        OnDownloadStart?.Invoke(filename);
    }

    public static void NotifyProgressUpdate(string filename, double percent, string speed, string eta)
    {
        OnProgressUpdate?.Invoke(filename, percent, speed, eta);

        // Telegram (solo se attivo per questo file)
        if (TelegramProgressNotifier.IsActive(filename))
            TelegramProgressNotifier.UpdateProgress(filename, percent, speed);
    }

    public static void NotifyDownloadComplete(string filename, string destPath)
    {
        OnDownloadComplete?.Invoke(filename, destPath);
        TelegramProgressNotifier.NotifyComplete(filename, destPath);
    }
}
