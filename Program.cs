using Send2Plex.Components;
using SendToPlex.Bot.Models;
using SendToPlex.Bot.Services;
using SendToPlex.Bot.UI;
using Serilog;
using Serilog.Events;

// Configura Serilog (stesso schema del progetto WinForms, senza il sink per la textbox di log
// che qui non esiste).
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("logs\\send2plex.log",
                  rollingInterval: RollingInterval.Day,
                  retainedFileCountLimit: 7,
                  restrictedToMinimumLevel: LogEventLevel.Debug)
    .CreateLogger();

// Config già caricata/decifrata (vedi ConfigManager.LoadConfig, gestisce sia valori DPAPI sia in
// chiaro) e usata come unica fonte di verità per il DI, come nel progetto WinForms.
var startConfig = ConfigManager.LoadConfig();
var errors = ConfigManager.ValidateConfig(startConfig);
if (errors.Count > 0)
    Log.Warning("⚠️ Configurazione incompleta: {Errors}", string.Join(", ", errors));

CleanupOrphanedPartFiles(startConfig);

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// Ascolta su tutte le interfacce (non solo localhost) per essere raggiungibile da telefono/altri
// dispositivi sulla stessa rete. Impostato qui in codice, non in appsettings.json: la pagina
// Impostazioni riscrive l'intero file ad ogni salvataggio e cancellerebbe una chiave "Urls" lì.
builder.WebHost.UseUrls("http://0.0.0.0:5075");

// Bot Telegram: il TelegramBotClient è costruito una sola volta con questo token (vedi
// TelegramWorker) — cambiarlo richiede comunque un riavvio, quindi resta uno snapshot statico
// invece di passare dal ConfigStore come il resto (Punto 2, deciso con l'utente).
builder.Services.Configure<TelegramSettings>(opts =>
{
    opts.BotToken = startConfig.Telegram.BotToken;
    opts.AllowedChatIds = startConfig.Telegram.AllowedChatIds;
    opts.MaxConcurrentJobs = startConfig.Telegram.MaxConcurrentJobs;
});

// Il resto della configurazione (Plex, AllDebrid, TMDB, percorsi, siti di ricerca, VPN) si
// applica a caldo: i servizi la leggono da ConfigStore.Current ad ogni chiamata invece che una
// volta sola nel costruttore, così Impostazioni non richiede più un riavvio per questi valori.
builder.Services.AddSingleton(new ConfigStore(startConfig));

// Usati direttamente da TelegramWorker.cs (bot, resta con lo snapshot preso all'avvio: stesso
// discorso del token, il bot non ricarica la propria configurazione di ricerca/percorsi a caldo).
builder.Services.Configure<PathSettings>(opts =>
{
    opts.Movies = startConfig.Paths.Movies;
    opts.Tv = startConfig.Paths.Tv;
});
builder.Services.Configure<TorrentSearchSettings>(opts =>
{
    opts.Sites = startConfig.TorrentSearch.Sites;
    opts.MaxResultsPerSite = startConfig.TorrentSearch.MaxResultsPerSite;
    opts.TimeoutSeconds = startConfig.TorrentSearch.TimeoutSeconds;
});

// HttpClient con resilienza (stesso schema del progetto WinForms)
builder.Services.AddHttpClient("AD").AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient("DEFAULT").AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient("PLEX").AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient("DL").AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient<TorrentSearchService>(c =>
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"))
    .AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient<TmdbClient>().AddPolicyHandler(PollyHelper.RetryPolicy());

// Servizi core (stesso schema del progetto WinForms)
builder.Services.AddSingleton<AllDebridClient>();
builder.Services.AddSingleton<PlexClient>();
builder.Services.AddSingleton<Downloader>();
builder.Services.AddSingleton<ArchiveExtractor>();
builder.Services.AddSingleton<BrowserFetcher>();
builder.Services.AddSingleton<NordVpnController>();
builder.Services.AddSingleton<DownloadHistoryService>();
builder.Services.AddSingleton<DownloadOrchestrator>();
builder.Services.AddSingleton<DownloadQueueService>();
builder.Services.AddSingleton<LoginStatusChecker>();

// Bot Telegram: parte automaticamente all'avvio dell'app. Registrato anche come singleton
// "normale" (oltre che come IHostedService) così la UI web può iniettarlo direttamente per
// leggere lo stato e mettere in pausa/riprendere, senza dover passare da un comando Telegram.
builder.Services.AddSingleton<TelegramWorker>();
builder.Services.AddHostedService<TelegramWorker>(sp => sp.GetRequiredService<TelegramWorker>());

// Blazor Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Rimuove i file .part rimasti da download interrotti (es. per un crash) da più di 24h:
// quelli recenti restano, potrebbero essere ripresi al prossimo tentativo sullo stesso file.
static void CleanupOrphanedPartFiles(AppConfig config)
{
    foreach (var folder in new[] { config.Paths.Movies, config.Paths.Tv })
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;

        try
        {
            foreach (var partFile in Directory.GetFiles(folder, "*.part", SearchOption.AllDirectories))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(partFile) > TimeSpan.FromHours(24))
                    {
                        File.Delete(partFile);
                        Log.Information("🧹 Rimosso file parziale orfano: {File}", partFile);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Impossibile rimuovere il file parziale {File}", partFile);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Errore scansionando {Folder} per file parziali orfani", folder);
        }
    }
}
