using System.Collections.Concurrent;
using SendToPlex.Bot.Helpers;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

public static class TelegramProgressNotifier
{
    private class Tracker
    {
        public long ChatId;
        public int MessageId;
        public string FileName = "";
        public ITelegramBotClient? Bot;
        public DateTime LastUpdate = DateTime.MinValue;
        public readonly object Lock = new object();
    }

    private static readonly ConcurrentDictionary<string, Tracker> _trackers = new();

    public static void Setup(long chatId, int messageId, string fileName, ITelegramBotClient bot)
    {
        var tracker = new Tracker
        {
            ChatId = chatId,
            MessageId = messageId,
            FileName = fileName,
            Bot = bot,
            LastUpdate = DateTime.MinValue
        };
        _trackers[fileName] = tracker;
    }

    public static async void UpdateProgress(string fileName, double progressValue, string speed)
    {
        if (!_trackers.TryGetValue(fileName, out var tracker) || tracker.Bot == null) return;

        lock (tracker.Lock)
        {
            if ((DateTime.Now - tracker.LastUpdate).TotalSeconds < 2) return;
            tracker.LastUpdate = DateTime.Now;
        }

        try
        {
            var progressBar = ProgressBarHelper.CreateBar(progressValue);
            var progressText = $"📥 **Download in corso:**\n`{tracker.FileName}`\n\n{progressBar} {progressValue:F1}%\n💨 **Velocità:** {speed}";

            await tracker.Bot.EditMessageText(
                new ChatId(tracker.ChatId),
                tracker.MessageId,
                progressText,
                parseMode: ParseMode.Markdown);
        }
        catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 429)
        {
            Log.Logger.Warning("Telegram rate limit (429) durante l'update di progress per {FileName}, salto questo aggiornamento", fileName);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Errore aggiornando il progress Telegram per {FileName}", fileName);
        }
    }

    public static string CreateProgressBar(double percent) => ProgressBarHelper.CreateBar(percent);

    public static void Reset(string fileName)
    {
        _trackers.TryRemove(fileName, out _);
    }

    public static async void NotifyComplete(string fileName, string destPath)
    {
        if (!_trackers.TryGetValue(fileName, out var tracker) || tracker.Bot == null)
        {
            Reset(fileName);
            return;
        }

        try
        {
            var msg =
                $"✅ **Download completato!**\n\n" +
                $"📁 **File:** `{fileName}`\n" +
                $"📂 **Percorso:** `{destPath}`";

            await tracker.Bot.EditMessageText(
                new ChatId(tracker.ChatId),
                tracker.MessageId,
                msg,
                parseMode: ParseMode.Markdown);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Errore inviando la notifica finale Telegram per {FileName}", fileName);
        }
        finally
        {
            Reset(fileName);
        }
    }

    public static bool IsActive(string fileName)
    {
        return _trackers.ContainsKey(fileName);
    }
}
