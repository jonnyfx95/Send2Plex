using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Configurazione applicata a caldo (Punto 2): singleton che tiene l'AppConfig corrente in
/// memoria. I servizi che possono ricaricare la configurazione senza side-effect (Plex, AllDebrid,
/// TMDB, percorsi, siti di ricerca, VPN) leggono da qui ad ogni chiamata invece che una volta sola
/// nel costruttore via IOptions&lt;T&gt;. Il Bot Telegram resta l'eccezione concordata: il
/// TelegramBotClient/TelegramWorker è già avviato come BackgroundService, quindi continua a usare
/// lo snapshot statico preso all'avvio (IOptions&lt;TelegramSettings&gt;) — cambiare il token
/// richiede comunque un riavvio dell'app.
/// </summary>
public class ConfigStore
{
    private readonly object _lock = new();
    private AppConfig _current;

    public ConfigStore(AppConfig initial)
    {
        _current = initial;
    }

    public AppConfig Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Salva su disco e sostituisce la configurazione in memoria che i servizi leggono.</summary>
    public void Update(AppConfig config)
    {
        ConfigManager.SaveConfig(config);
        lock (_lock) _current = config;
    }
}
