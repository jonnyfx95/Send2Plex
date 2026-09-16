using Microsoft.AspNetCore.DataProtection;
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
    .WriteTo.File(Path.Combine(AppPaths.Logs, "send2plex.log"),
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
CleanupOrphanedHlsSessions();

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

// Persistenza del key ring di Data Protection (antiforgery token, e da questa sessione anche
// CredentialProtector — vedi Services/AppPaths.cs) — prima non era configurata affatto, quindi
// usava il key ring effimero/per-profilo di default, che non è garantito sopravvivere a un
// riavvio. Stessa cartella di AppPaths.DataProtectionKeys così i due meccanismi condividono un
// solo key ring su disco.
builder.Services.AddDataProtection()
    .SetApplicationName("Send2Plex")
    .PersistKeysToFileSystem(new DirectoryInfo(AppPaths.DataProtectionKeys));

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

// CORS permissivo: serve solo per l'app WebOS nativa (docs/piano-webos-app.md), che gira come app
// installata localmente sulla TV (origine diversa da quella del backend) e chiama /api/tv/* e
// /stream via fetch/<video src>. Nessun rischio aggiuntivo: il resto dell'app non ha comunque
// autenticazione (rete domestica fidata, decisione già presa in piano-streaming-diretto.md).
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// HttpClient con resilienza (stesso schema del progetto WinForms)
builder.Services.AddHttpClient("AD").AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient("RD").AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient("PM").AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient("DEFAULT").AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient("PLEX").AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient("DL").AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient<TorrentSearchService>(c =>
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"))
    .AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient<TmdbClient>().AddPolicyHandler(PollyHelper.RetryPolicy());
builder.Services.AddHttpClient<OmdbClient>().AddPolicyHandler(PollyHelper.RetryPolicy());

// Servizi core (stesso schema del progetto WinForms)
builder.Services.AddSingleton<AllDebridClient>();
builder.Services.AddSingleton<RealDebridClient>();
builder.Services.AddSingleton<PremiumizeClient>();
builder.Services.AddSingleton<DebridProviderFactory>();
builder.Services.AddSingleton<PlexClient>();
builder.Services.AddSingleton<Downloader>();
builder.Services.AddSingleton<ArchiveExtractor>();
builder.Services.AddSingleton<NordVpnController>();
builder.Services.AddSingleton<DownloadHistoryService>();
builder.Services.AddSingleton<WatchHistoryService>();
builder.Services.AddSingleton<LibraryAcquisitionService>();
builder.Services.AddHostedService<LibraryRetentionService>();
builder.Services.AddSingleton<WatchlistService>();
builder.Services.AddSingleton<TvPreferencesService>();
builder.Services.AddSingleton<DownloadOrchestrator>();
builder.Services.AddSingleton<DownloadQueueService>();
builder.Services.AddSingleton<StreamingService>();

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
app.UseCors();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Streaming diretto (senza scaricare su disco): endpoint "nudo", non una pagina Blazor, perché
// Blazor Server non è pensato per scrivere byte grezzi (l'output di FFmpeg) sulla risposta HTTP.
// Nessun limite MaxParallel qui: quel semaforo throttla i download permanenti su disco, non ha
// senso applicarlo a un flusso consumato in tempo reale dal player (vedi piano-streaming-diretto.md).
app.MapGet("/stream", StreamingService.HandleAsync);

// Streaming HLS segmentato (docs/piano-streaming-multiutente-hls.md, Fase 2) — endpoint nuovi,
// coesistono con /stream sopra (invariato). Ancora un solo spettatore per file, come /stream;
// la condivisione multi-utente arriva con la Fase 3.
app.MapGet("/stream/hls", StreamingService.HandleHlsStartAsync);
app.MapGet("/stream/hls/{sessionId}/playlist.m3u8", StreamingService.HandleHlsPlaylistAsync);
app.MapGet("/stream/hls/{sessionId}/{file}", StreamingService.HandleHlsFileAsync);
app.MapGet("/stream/hls-test", StreamingService.HandleHlsTestPage); // TEMPORANEO, vedi commento in StreamingService.Hls.cs

// API JSON minima per l'app nativa WebOS (docs/piano-webos-app.md): la TV naviga solo tra i
// magnet già pronti su AllDebrid e li guarda in streaming, non rifà l'intera ricerca/preparazione
// (typing via telecomando è scomodo, resta un passo successivo eventuale) — stessa logica già
// usata da AllDebrid.razor, qui solo proiettata in JSON invece che renderizzata in Blazor.
TvApiEndpoints.Map(app);

app.Run();

// piano-streaming-multiutente-hls.md, Fase 2 — le sessioni HLS non sono pensate per sopravvivere
// a un riavvio del backend (la mappa in memoria riparte sempre vuota), quindi qualunque cartella
// rimasta da una sessione precedente (crash, o un kill del processo senza l'albero dei figli —
// capitato in pratica durante lo sviluppo: l'ffmpeg orfano teneva bloccata la cartella) va tolta
// all'avvio. Best-effort: se un ffmpeg orfano tiene ancora aperta la cartella, la Delete fallisce
// silenziosamente — non è pensata per uccidere processi ffmpeg altrui in modo indiscriminato.
static void CleanupOrphanedHlsSessions()
{
    var dir = Path.Combine(AppPaths.Data, "hls-sessions");
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    catch { /* cartella occupata da un processo ancora vivo — verrà ritentato al prossimo avvio */ }
}

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
