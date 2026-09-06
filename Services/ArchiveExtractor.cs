using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace SendToPlex.Bot.Services;

public class ArchiveExtractor
{
    private readonly ILogger<ArchiveExtractor> _log;

    public ArchiveExtractor(ILogger<ArchiveExtractor> log)
    {
        _log = log;
    }

    /// <summary>
    /// Se il file è un archivio (.zip, .rar), lo estrae mostrando progress bar su Telegram
    /// e rimuove l’archivio originale. Ritorna true se non serviva estrazione o è riuscita,
    /// false se l'estrazione è stata tentata ma è fallita (es. per non far scattare un
    /// refresh Plex a valle su un file che potrebbe essere ancora un archivio corrotto).
    /// </summary>
    public async Task<bool> ExtractIfArchiveAsync(
     string filePath,
     ITelegramBotClient bot,
     long chatId,
     int messageId,
     CancellationToken ct)
    {
        if (!File.Exists(filePath)) return true;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".zip" && ext != ".rar")
        {
            _log.LogDebug("Nessuna estrazione necessaria per {File}", filePath);
            return true;
        }

        var folder = Path.GetDirectoryName(filePath)!;
        var fileName = Path.GetFileName(filePath);

        try
        {
            // 📦 Messaggio iniziale
            await bot.EditMessageText(
                new ChatId(chatId),
                messageId,
                $"📦 Estrazione `{fileName}` → `{folder}`\n\n🔄 In corso...",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            int extractedCount = 0;

            using (var archive = ArchiveFactory.OpenArchive(filePath))
            {
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                foreach (var entry in entries)
                {
                    entry.WriteToDirectory(folder, new ExtractionOptions()
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                    extractedCount++;
                }
            }

            File.Delete(filePath);

            // ✅ Messaggio finale
            await bot.EditMessageText(
                new ChatId(chatId),
                messageId,
                $"✅ Estrazione completata\n\n📦 `{fileName}` → `{folder}`\n📄 File estratti: {extractedCount}",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            _log.LogInformation("✅ Estratto ed eliminato archivio: {File} → {Folder} ({Count} file)", filePath, folder, extractedCount);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Errore durante l’estrazione di {File}", filePath);

            await bot.EditMessageText(
                new ChatId(chatId),
                messageId,
                $"❌ Errore estrazione `{fileName}`\n\n🔴 {ex.Message}",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);

            return false;
        }
    }
}