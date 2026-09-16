using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SendToPlex.Bot.Services;

public class TvPreferences
{
    public string? Quality { get; set; }
    public string? PreferredQuality { get; set; }
    public string? PreferredLanguage { get; set; }

    // "allDebrid"/"realDebrid" (docs/piano-multi-provider-debrid.md) — default globale per la TV,
    // stesso pattern di PreferredQuality/PreferredLanguage: pre-seleziona il provider nel toggle di
    // pagina, resta comunque cambiabile lì per lì.
    public string? PreferredProvider { get; set; }
    // "auto" (default, prova diretta poi trascodifica se serve), "direct" (sempre diretta, mai
    // trascodifica automatica) o "transcode" (salta subito la diretta) — vedi webos-app/app.js,
    // openPlayer().
    public string? PlaybackMode { get; set; }

    // Tre nuove preferenze (piano-webos-skip-marker-plex.md, punto 3) — nullable perché un client
    // vecchio/il primo avvio non le manda mai: null vuol dire "usa il default lato client", non
    // "disattivato" (vedi webos-app/app.js, NEW_TV_PREFS/getConfig, che applicano i default
    // effettivi true/10/true quando il valore è assente).
    public bool? AutoNextEpisodeEnabled { get; set; }
    public int? NextEpisodeCountdownSeconds { get; set; }
    public bool? PlexMarkersEnabled { get; set; }
}

/// <summary>
/// Preferenze dell'app WebOS (qualità streaming, qualità/lingua torrent preferite) — SOLO queste,
/// non l'host (che deve comunque restare noto solo al client: serve per raggiungere questo stesso
/// server, un valore lato server non lo sostituirebbe). Persistite qui, non solo in localStorage
/// sulla TV, perché un reinstall dell'app WebOS a volte svuota il localStorage (richiesta utente,
/// dopo essersi ritrovato a reinserirle ad ogni deploy) — stesso schema a oggetto singolo (non un
/// dizionario) di ConfigStore, ma file separato per restare indipendente dalle impostazioni con
/// segreti/chiavi API.
/// </summary>
public class TvPreferencesService
{
    private readonly string _path;
    private readonly ILogger<TvPreferencesService> _log;
    private readonly object _lock = new();
    private TvPreferences _current;

    public TvPreferencesService(ILogger<TvPreferencesService> log)
    {
        _log = log;
        _path = Path.Combine(AppPaths.Data, "tv-preferences.json");
        _current = Load();
    }

    private TvPreferences Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<TvPreferences>(json) ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore leggendo le preferenze TV, riparto vuote");
            return new();
        }
    }

    public TvPreferences Get()
    {
        lock (_lock) return _current;
    }

    // BUG REALE (preferenze qualità/lingua torrent sparite): questo era un rimpiazzo totale
    // dell'oggetto — un salvataggio PARZIALE (es. il player che cambia solo qualità/modalità
    // video mentre guarda, savePlayerPreference in app.js, che non conosce affatto
    // preferredQuality/Language se non erano ancora presenti in localStorage sulla TV) mandava
    // quei due campi come `undefined`, JSON.stringify li OMETTE dal body, il model binding li
    // lasciava quindi null sull'oggetto in arrivo — e "_current = prefs" li azzerava per tutti,
    // cancellando silenziosamente una preferenza impostata in un momento completamente diverso.
    // Ora si aggiorna solo i campi realmente presenti nella richiesta (null = "non toccare"); una
    // stringa vuota resta comunque una scelta esplicita valida ("Nessuna preferenza" dal form di
    // Setup), quindi viene comunque applicata.
    public void Save(TvPreferences prefs)
    {
        lock (_lock)
        {
            if (prefs.Quality is not null) _current.Quality = prefs.Quality;
            if (prefs.PreferredQuality is not null) _current.PreferredQuality = prefs.PreferredQuality;
            if (prefs.PreferredLanguage is not null) _current.PreferredLanguage = prefs.PreferredLanguage;
            if (prefs.PreferredProvider is not null) _current.PreferredProvider = prefs.PreferredProvider;
            if (prefs.PlaybackMode is not null) _current.PlaybackMode = prefs.PlaybackMode;
            if (prefs.AutoNextEpisodeEnabled is not null) _current.AutoNextEpisodeEnabled = prefs.AutoNextEpisodeEnabled;
            if (prefs.NextEpisodeCountdownSeconds is not null) _current.NextEpisodeCountdownSeconds = prefs.NextEpisodeCountdownSeconds;
            if (prefs.PlexMarkersEnabled is not null) _current.PlexMarkersEnabled = prefs.PlexMarkersEnabled;
            try
            {
                var json = JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "⚠️ Errore salvando le preferenze TV");
            }
        }
    }
}
