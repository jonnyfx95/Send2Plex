using SendToPlex.Bot.Models;
using System.Net.Http;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;


namespace SendToPlex.Bot.Services;



public class AllDebridTestResult
{
    public bool Success { get; set; }
    public string Username { get; set; } = string.Empty;
}

public class TelegramTestResult
{
    public bool Success { get; set; }
    public string Username { get; set; } = string.Empty;
}

public static class ConfigTester
{
    public static async Task<(Dictionary<string, bool> Results, string AllDebridUser, string TelegramUser)> TestServicesAsync(AppConfig config)
    {
        var results = new Dictionary<string, bool>();

        // Telegram
        var tgResult = await TestTelegramAsync(config.Telegram);
        results["Telegram"] = tgResult.Success;

        // AllDebrid con username
        var adResult = await TestAllDebridAsync(config.AllDebrid);
        results["AllDebrid"] = adResult.Success;

        // Plex
        results["Plex"] = await TestPlexAsync(config.Plex);

        // Paths
        results["Paths"] = TestPaths(config.Paths);

        return (results, adResult.Username, tgResult.Username);
    }


    private static bool TestPaths(PathSettings paths)
    {
        return
            !string.IsNullOrWhiteSpace(paths.Movies) &&
            !string.IsNullOrWhiteSpace(paths.Tv) &&
            Directory.Exists(paths.Movies) &&
            Directory.Exists(paths.Tv);
    }


    private static async Task<TelegramTestResult> TestTelegramAsync(TelegramSettings tg)
    {
        var result = new TelegramTestResult();

        if (string.IsNullOrWhiteSpace(tg.BotToken))
            return result; // Success = false, Username vuoto

        try
        {
            var bot = new Telegram.Bot.TelegramBotClient(tg.BotToken.Trim());

            // ✅ verifica token e recupera info bot
            var me = await bot.GetMe();
            if (me == null)
                return result;

            result.Success = true;
            result.Username = me.Username ?? "";

            // opzionale: se vuoi testare anche invio a un chatId
            if (tg.AllowedChatIds.Length > 0)
            {
                var chatId = tg.AllowedChatIds[0];
                
            }
        }
        catch
        {
            // lascio Success = false
        }

        return result;
    }



    public static async Task SendTelegramReportAsync(
    AppConfig config,
    (Dictionary<string, bool> Results, string AllDebridUser, string TelegramUser) testResult)
    {
        if (string.IsNullOrWhiteSpace(config.Telegram.BotToken) || config.Telegram.AllowedChatIds.Length == 0)
            return;

        try
        {
            var bot = new TelegramBotClient(config.Telegram.BotToken.Trim());

            var results = testResult.Results;
            var adUser = testResult.AllDebridUser;
            var tgUser = testResult.TelegramUser;

            var report =
                "📊 **Report configurazione Send2Plex**\n\n" +
                $"🤖 Telegram Bot → {(results["Telegram"] ? $"✅ OK (Bot: @{tgUser})" : "❌ Errore")}\n" +
                $"🔓 AllDebrid API → {(results["AllDebrid"] ? $"✅ OK (User: {adUser})" : "❌ Errore")}\n" +
                $"🎬 Plex Server → {(results["Plex"] ? "✅ OK" : "❌ Errore")}\n" +
                $"📂 Percorsi locali → {(results["Paths"] ? "✅ OK" : "❌ Errore")}";

            var chatId = config.Telegram.AllowedChatIds[0];
            await bot.SendMessage(chatId, report);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Errore inviando report Telegram: {ex.Message}");
        }
    }







    private static async Task<AllDebridTestResult> TestAllDebridAsync(AllDebridSettings ad)
    {
        var result = new AllDebridTestResult();

        if (string.IsNullOrWhiteSpace(ad.ApiKey))
            return result; // ritorna Success = false, Username vuoto

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ad.ApiKey);

            var resp = await http.GetAsync("https://api.alldebrid.com/v4/user");
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return result;

            using var doc = System.Text.Json.JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("status", out var status) &&
                status.GetString() == "success")
            {
                var user = doc.RootElement
                              .GetProperty("data")
                              .GetProperty("user");

                result.Success = true;
                result.Username = user.GetProperty("username").GetString() ?? "";
            }
        }
        catch
        {
            // lascio Success = false e Username vuoto
        }

        return result;
    }



    private static async Task<bool> TestPlexAsync(PlexSettings plex)
    {
        if (string.IsNullOrWhiteSpace(plex.BaseUrl) || string.IsNullOrWhiteSpace(plex.Token))
            return false;
        try
        {
            using var http = new HttpClient();
            var url = $"{plex.BaseUrl.Trim()}/identity?X-Plex-Token={plex.Token.Trim()}";
            var resp = await http.GetAsync(url);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }


}
