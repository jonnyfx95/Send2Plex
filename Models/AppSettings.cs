using System.Text.Json;
using System.Text.Json.Serialization;
using SendToPlex.Bot.Services;

namespace SendToPlex.Bot.Models;

public class TelegramSettings
{
    public string BotToken { get; set; } = string.Empty;
    public long[] AllowedChatIds { get; set; } = Array.Empty<long>();
    public int MaxConcurrentJobs { get; set; } = 2;
}

public class AllDebridSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Agent { get; set; } = "SendToPlexBot";
}

public class PlexSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public List<int> MovieSectionIds { get; set; } = new();
    public List<int> TvSectionIds { get; set; } = new();
}

// Cartella di destinazione per una specifica libreria Plex (es. "MCU" -> "I:\Plex\MCU") — usata
// quando l'utente ha più sezioni Movies/TV e vuole scegliere dove salvare invece di finire
// sempre nell'unica cartella di default (Paths.Movies/Tv).
public class LibraryFolder
{
    public int SectionId { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public class PathSettings
{
    // Cartelle di default: usate dal bot Telegram (non toccato dal Punto 2/cartelle multiple) e
    // come fallback quando una sezione non ha una cartella dedicata configurata.
    public string Movies { get; set; } = string.Empty;
    public string Tv { get; set; } = string.Empty;

    public List<LibraryFolder> MovieFolders { get; set; } = new();
    public List<LibraryFolder> TvFolders { get; set; } = new();
}

public class DownloadSettings
{
    public int MaxParallel { get; set; } = 2;
    public int TimeoutMinutes { get; set; } = 120;
}

public class GeneralSettings
{
    public bool StartMinimized { get; set; } = false;
    public bool AutoStartBot { get; set; } = false;
}

public class TmdbSettings
{
    public string ApiKey { get; set; } = string.Empty;
}

public class VpnSettings
{
    // Gestione automatica NordVPN: connessa solo per cercare sui siti che la richiedono
    // (TorrentSiteConfig.RequiresVpn), disconnessa prima di ogni download (che passa per
    // i server di AllDebrid, non serve la VPN e va più veloce sulla connessione diretta).
    public bool Enabled { get; set; } = false;
    public string NordVpnExePath { get; set; } = @"C:\Program Files\NordVPN\nordvpn.exe";
    public int ConnectTimeoutSeconds { get; set; } = 20;
}

public class TorrentIndexPage
{
    public string Name { get; set; } = ""; // titolo leggibile della lista (es. "LISTA TITOLI SEZIONE FILM - HEVC/H.265 0/9-A/H")
    public string Url { get; set; } = "";
}

public class TorrentSiteConfig
{
    public string Name { get; set; } = "";
    public string SearchUrlTemplate { get; set; } = ""; // usa {query} come placeholder
    public string ResultSelector { get; set; } = "";     // selettore CSS della riga risultato
    public string TitleSelector { get; set; } = "";
    public string LinkSelector { get; set; } = "";
    public string LinkAttribute { get; set; } = "href";
    public string? SizeSelector { get; set; }
    public string? SeedsSelector { get; set; }
    public bool NeedsDetailPageForMagnet { get; set; }
    public string? MagnetSelectorOnDetailPage { get; set; }
    public string? Cookie { get; set; } // sessione per siti che richiedono login (es. forum privati)
    public bool UseBrowser { get; set; } // usa un browser reale (WebView2) invece di HttpClient: per siti con Cloudflare/JS
    public string? LoginUrl { get; set; } // URL da aprire per il login manuale nel browser integrato
    public string? SearchFormPageUrl { get; set; } // pagina che contiene il form di ricerca (per siti con ricerca via POST)
    public string? SearchFormFieldName { get; set; } // attributo name del campo di ricerca nel form (es. "search")
    public string? RevealClickSelector { get; set; } // selettore di un elemento da cliccare sulla pagina di dettaglio prima di leggere il magnet (es. pulsante "Ringrazia")

    // Selettore CSS di un elemento presente solo se l'utente è loggato (es. un link "Logout"),
    // usato dalla pagina Impostazioni per mostrare lo stato di login (Punto 5). Se vuoto, si usa
    // un'euristica generica sul testo della pagina (cerca parole come "logout"/"esci").
    public string? LoggedInIndicatorSelector { get; set; }
    public bool RequiresVpn { get; set; } // se true e Vpn.Enabled, connette NordVPN prima di cercare su questo sito
    public bool UseThePirateBayApi { get; set; } // caso speciale: usa l'API JSON pubblica di apibay.org invece dello scraping HTML

    // Caso speciale: usa l'API pubblica di Torrentio (torrentio.strem.fun), l'addon Stremio.
    // A differenza degli altri siti non fa una ricerca testuale libera: vuole un IMDb ID
    // (risolto da TMDB a partire dal titolo) e, per le serie, stagione+episodio specifici.
    public bool UseTorrentioApi { get; set; }

    // Se true, dopo la scelta di stagione/risoluzione su Telegram viene proposto anche un filtro
    // per lingua (ITA/MULTI/SUB ITA/ENG), dedotto dal titolo. Da disattivare sui siti dove è inutile
    // perché tutti i contenuti sono già in una sola lingua (es. forum italiani come icv-crew).
    public bool DetectLanguage { get; set; } = true;

    // Numero massimo di pagine da scaricare per una ricerca live. Ha effetto solo se
    // SearchUrlTemplate contiene il segnaposto {page}; altrimenti si scarica sempre una sola pagina
    // (comportamento invariato per i siti esistenti). Più pagine = ricerca più lenta ma più completa.
    public int MaxPages { get; set; } = 1;

    // Modalità alternativa alla ricerca live: scansiona un elenco fisso di pagine "indice"
    // (es. liste alfabetiche) e filtra i titoli che contengono la query, invece di usare
    // action=search2 (che su alcuni forum matcha anche i commenti, non solo i post con link).
    // Se IndexPages non è vuoto, ha priorità sulla ricerca live per questo sito.
    public List<TorrentIndexPage> IndexPages { get; set; } = new();
    public string? IndexLinkSelector { get; set; } // selettore CSS dei link titolo dentro le pagine indice (es. "div.post div.inner a")

    // Vista comoda per la griglia UI: elenco IndexPages come singola cella, "Nome|Url" separati da ";".
    [JsonIgnore]
    public string IndexPageUrlsText
    {
        get => string.Join(" ; ", IndexPages.Select(p => $"{p.Name}|{p.Url}"));
        set => IndexPages = (value ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s =>
            {
                var parts = s.Split('|', 2);
                return parts.Length == 2
                    ? new TorrentIndexPage { Name = parts[0].Trim(), Url = parts[1].Trim() }
                    : new TorrentIndexPage { Name = parts[0].Trim(), Url = parts[0].Trim() };
            })
            .ToList();
    }

    public bool Enabled { get; set; } = true;
}

public class TorrentSearchSettings
{
    public List<TorrentSiteConfig> Sites { get; set; } = new();
    public int MaxResultsPerSite { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 15;
}

public class AppConfig
{
    public GeneralSettings General { get; set; } = new();
    public TelegramSettings Telegram { get; set; } = new();
    public AllDebridSettings AllDebrid { get; set; } = new();
    public PlexSettings Plex { get; set; } = new();
    public PathSettings Paths { get; set; } = new();
    public DownloadSettings Download { get; set; } = new();
    public TorrentSearchSettings TorrentSearch { get; set; } = new();
    public VpnSettings Vpn { get; set; } = new();
    public TmdbSettings Tmdb { get; set; } = new();
}

public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "appsettings.json"
    );

    public static AppConfig LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var defaultConfig = CreateDefaultConfig();
                SaveConfig(defaultConfig);
                return defaultConfig;
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            config ??= CreateDefaultConfig();

            // Migrazione: i file scritti prima dell'introduzione di MovieSectionIds/TvSectionIds
            // avevano un singolo MoviesSectionId/TvSectionId (int). Se le nuove liste sono vuote,
            // le popoliamo dai vecchi campi letti dal JSON grezzo, così non si perde la
            // configurazione esistente al primo avvio dopo l'aggiornamento.
            MigrateLegacyPlexSectionIds(config, json);

            // Decifra le credenziali (se protette con DPAPI) per l'uso a runtime.
            // I valori legacy in chiaro (file pre-esistenti) passano invariati.
            config.Telegram.BotToken = CredentialProtector.Unprotect(config.Telegram.BotToken);
            config.AllDebrid.ApiKey = CredentialProtector.Unprotect(config.AllDebrid.ApiKey);
            config.Plex.Token = CredentialProtector.Unprotect(config.Plex.Token);
            config.Tmdb.ApiKey = CredentialProtector.Unprotect(config.Tmdb.ApiKey);

            return config;
        }
        catch (Exception)
        {
            return CreateDefaultConfig();
        }
    }

    public static void SaveConfig(AppConfig config)
    {
        try
        {
            // Scrive su disco una copia con le credenziali cifrate (DPAPI), senza toccare
            // l'oggetto in memoria: il resto dell'app deve continuare a vedere i valori in chiaro.
            var onDisk = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config)) ?? config;
            onDisk.Telegram.BotToken = CredentialProtector.Protect(config.Telegram.BotToken);
            onDisk.AllDebrid.ApiKey = CredentialProtector.Protect(config.AllDebrid.ApiKey);
            onDisk.Plex.Token = CredentialProtector.Protect(config.Plex.Token);
            onDisk.Tmdb.ApiKey = CredentialProtector.Protect(config.Tmdb.ApiKey);

            var json = JsonSerializer.Serialize(onDisk, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Errore salvando configurazione: {ex.Message}");
        }
    }

    private static void MigrateLegacyPlexSectionIds(AppConfig config, string json)
    {
        if (config.Plex.MovieSectionIds.Count > 0 && config.Plex.TvSectionIds.Count > 0)
            return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Plex", out var plexEl))
                return;

            if (config.Plex.MovieSectionIds.Count == 0 &&
                plexEl.TryGetProperty("MoviesSectionId", out var moviesEl) &&
                moviesEl.TryGetInt32(out var moviesId))
            {
                config.Plex.MovieSectionIds = new List<int> { moviesId };
            }

            if (config.Plex.TvSectionIds.Count == 0 &&
                plexEl.TryGetProperty("TvSectionId", out var tvEl) &&
                tvEl.TryGetInt32(out var tvId))
            {
                config.Plex.TvSectionIds = new List<int> { tvId };
            }
        }
        catch (Exception)
        {
            // JSON non parsabile per la migrazione: si prosegue con le liste vuote/di default già presenti.
        }
    }

    private static AppConfig CreateDefaultConfig()
    {
        return new AppConfig
        {
            General = new GeneralSettings
            {
                StartMinimized = false,
                AutoStartBot = false
            },
            Telegram = new TelegramSettings
            {
                BotToken = "",
                AllowedChatIds = Array.Empty<long>(),
                MaxConcurrentJobs = 2
            },
            AllDebrid = new AllDebridSettings
            {
                ApiKey = "",
                Agent = "SendToPlexBot"
            },
            Plex = new PlexSettings
            {
                BaseUrl = "http://localhost:32400",
                Token = "",
                MovieSectionIds = new List<int> { 1 },
                TvSectionIds = new List<int> { 2 }
            },
            Paths = new PathSettings
            {
                Movies = "",
                Tv = ""
            },
            Download = new DownloadSettings
            {
                MaxParallel = 2,
                TimeoutMinutes = 120
            },
            TorrentSearch = new TorrentSearchSettings
            {
                Sites = new List<TorrentSiteConfig>(),
                MaxResultsPerSite = 10,
                TimeoutSeconds = 15
            }
        };
    }

    // Helper per convertire array di ChatIds in stringa per UI
    public static string ChatIdsToString(long[] chatIds)
    {
        return string.Join(", ", chatIds);
    }

    // Helper per convertire stringa da UI in array di ChatIds
    public static long[] StringToChatIds(string chatIdsString)
    {
        if (string.IsNullOrWhiteSpace(chatIdsString))
            return Array.Empty<long>();

        return chatIdsString
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Where(s => long.TryParse(s, out _))
            .Select(long.Parse)
            .ToArray();
    }

    // Validazione configurazione
    public static List<string> ValidateConfig(AppConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Telegram.BotToken))
            errors.Add("Bot Token Telegram è obbligatorio");

        if (config.Telegram.AllowedChatIds.Length == 0)
            errors.Add("Almeno un Chat ID è obbligatorio");

        if (string.IsNullOrWhiteSpace(config.AllDebrid.ApiKey))
            errors.Add("API Key AllDebrid è obbligatoria");

        if (string.IsNullOrWhiteSpace(config.Plex.BaseUrl))
            errors.Add("URL Server Plex è obbligatorio");

        if (string.IsNullOrWhiteSpace(config.Plex.Token))
            errors.Add("Token Plex è obbligatorio");

        if (string.IsNullOrWhiteSpace(config.Paths.Movies))
            errors.Add("Cartella Movies è obbligatoria");

        if (string.IsNullOrWhiteSpace(config.Paths.Tv))
            errors.Add("Cartella TV è obbligatoria");

        return errors;
    }

    public static Dictionary<string, bool> CheckServiceConfig(AppConfig config)
    {
        var results = new Dictionary<string, bool>();

        // Telegram valido se token e almeno un chatId
        results["Telegram"] =
            !string.IsNullOrWhiteSpace(config.Telegram.BotToken) &&
            config.Telegram.AllowedChatIds.Length > 0;

        // AllDebrid valido se apiKey presente
        results["AllDebrid"] =
            !string.IsNullOrWhiteSpace(config.AllDebrid.ApiKey);

        // Plex valido se url e token presenti
        results["Plex"] =
            !string.IsNullOrWhiteSpace(config.Plex.BaseUrl) &&
            !string.IsNullOrWhiteSpace(config.Plex.Token);

        // Percorsi validi se entrambe le cartelle sono definite
        results["Paths"] =
            !string.IsNullOrWhiteSpace(config.Paths.Movies) &&
            !string.IsNullOrWhiteSpace(config.Paths.Tv);

        return results;
    }
}