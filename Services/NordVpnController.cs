using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Automatizza la connessione/disconnessione di NordVPN via CLI (nordvpn.exe -c / -d),
/// così la VPN resta attiva solo per cercare sui siti che la richiedono e viene disattivata
/// prima di ogni download (che passa dai server di AllDebrid, non serve la VPN e va più
/// veloce sulla connessione diretta). Se <see cref="VpnSettings.Enabled"/> è false o
/// l'eseguibile non è configurato/trovato, ogni chiamata è un no-op che non blocca nulla.
/// </summary>
public class NordVpnController
{
    private readonly ConfigStore _configStore;
    private readonly ILogger<NordVpnController> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Letta ad ogni chiamata: abilitazione/percorso NordVPN possono cambiare a caldo da
    // Impostazioni, senza richiedere un riavvio dell'app (Punto 2).
    private VpnSettings _cfg => _configStore.Current.Vpn;

    public NordVpnController(ConfigStore configStore, ILogger<NordVpnController> log)
    {
        _configStore = configStore;
        _log = log;
    }

    /// <summary>True se la gestione automatica è attiva in configurazione (indipendentemente dal file .exe).</summary>
    public bool IsEnabled => _cfg.Enabled;

    /// <summary>True se nordvpn.exe è configurato e trovato — indipendente da <see cref="IsEnabled"/>,
    /// serve per lo switch manuale in UI che deve funzionare anche a gestione automatica disattivata.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_cfg.NordVpnExePath) && File.Exists(_cfg.NordVpnExePath);

    /// <summary>Stato live dell'adattatore NordLynx, per mostrare connesso/disconnesso in UI.</summary>
    public bool IsConnected => IsInterfaceUp();

    /// <summary>Notificato dopo ogni tentativo di connessione/disconnessione (manuale o automatico),
    /// così lo switch in UI resta sincronizzato anche quando è una ricerca/download a muovere la VPN.</summary>
    public event Action? StatusChanged;

    private bool IsUsable => _cfg.Enabled && IsConfigured;

    /// <summary>Connette la VPN per un uso automatico interno (ricerca su un sito che la richiede):
    /// no-op se la gestione automatica è disattivata in Impostazioni.</summary>
    public Task<bool> ConnectAsync(CancellationToken ct) => IsUsable ? ConnectCoreAsync(ct) : Task.FromResult(true);

    /// <summary>Disconnette la VPN per un uso automatico interno (prima di un download):
    /// no-op se la gestione automatica è disattivata in Impostazioni.</summary>
    public Task<bool> DisconnectAsync(CancellationToken ct) => IsUsable ? DisconnectCoreAsync(ct) : Task.FromResult(true);

    /// <summary>
    /// Accende/spegne la VPN su comando esplicito dell'utente (switch in barra in alto):
    /// funziona anche se la gestione automatica (VpnSettings.Enabled) è disattivata, perché qui
    /// è l'utente stesso a decidere — richiede solo che nordvpn.exe sia configurato e trovato.
    /// </summary>
    public Task<bool> ManualToggleAsync(CancellationToken ct)
    {
        if (!IsConfigured) return Task.FromResult(false);
        return IsConnected ? DisconnectCoreAsync(ct) : ConnectCoreAsync(ct);
    }

    /// <summary>
    /// Connette la VPN e attende una verifica REALE di connettività (non solo lo stato
    /// dell'adattatore di rete): l'adattatore NordLynx può risultare "Up" qualche secondo
    /// prima che il tunnel instradi davvero il traffico, e partire subito con la ricerca
    /// in quella finestra produce falsi "nessun risultato".
    /// </summary>
    private async Task<bool> ConnectCoreAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (IsInterfaceUp() && await VerifyConnectivityAsync(ct))
            {
                _log.LogDebug("🔒 NordVPN già connessa e funzionante");
                return true;
            }

            if (!IsInterfaceUp())
            {
                _log.LogInformation("🔒 Connessione NordVPN in corso...");
                await RunCommandAsync("-c", ct);

                var upOk = await WaitForInterfaceStateAsync(up: true, _cfg.ConnectTimeoutSeconds, ct);
                if (!upOk)
                {
                    _log.LogWarning("⚠️ Adattatore NordVPN non risulta \"Up\" dopo il timeout");
                    return false;
                }
            }

            // L'adattatore è "Up": verifica che il tunnel instradi davvero il traffico
            // (può richiedere qualche secondo in più dopo che l'interfaccia è salita).
            var deadline = DateTime.UtcNow.AddSeconds(_cfg.ConnectTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (await VerifyConnectivityAsync(ct))
                {
                    _log.LogInformation("🔒 NordVPN connessa e verificata");
                    return true;
                }
                await Task.Delay(1000, ct);
            }

            _log.LogWarning("⚠️ NordVPN: adattatore \"Up\" ma nessuna connettività verificata entro il timeout");
            return false;
        }
        finally
        {
            _lock.Release();
            StatusChanged?.Invoke();
        }
    }

    private async Task<bool> DisconnectCoreAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!IsInterfaceUp())
            {
                _log.LogDebug("🔓 NordVPN già disconnessa");
                return true;
            }

            _log.LogInformation("🔓 Disconnessione NordVPN in corso (per scaricare alla velocità diretta)...");
            await RunCommandAsync("-d", ct);
            var interfaceDown = await WaitForInterfaceStateAsync(up: false, _cfg.ConnectTimeoutSeconds, ct);
            if (!interfaceDown)
            {
                _log.LogWarning("⚠️ NordVPN risulta ancora connessa dopo il timeout");
                return false;
            }

            // Come per la connessione: l'adattatore può risultare "down" qualche istante prima
            // che la connessione diretta torni davvero utilizzabile (routing/DNS che si
            // riassestano). Senza questa verifica, la prima richiesta subito dopo (es. sblocco
            // magnet su AllDebrid) può fallire con un errore di rete generico.
            var deadline = DateTime.UtcNow.AddSeconds(_cfg.ConnectTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (await VerifyConnectivityAsync(ct))
                {
                    _log.LogInformation("🔓 NordVPN disconnessa, connessione diretta verificata");
                    return true;
                }
                await Task.Delay(1000, ct);
            }

            _log.LogWarning("⚠️ NordVPN disconnessa ma nessuna connettività diretta verificata entro il timeout");
            return false;
        }
        finally
        {
            _lock.Release();
            StatusChanged?.Invoke();
        }
    }

    private static bool IsInterfaceUp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(ni => ni.Name.Equals("NordLynx", StringComparison.OrdinalIgnoreCase) &&
                           ni.OperationalStatus == OperationalStatus.Up);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Prova una richiesta HTTP reale per confermare che il traffico esca davvero (tunnel su o giù che sia).</summary>
    private static async Task<bool> VerifyConnectivityAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var resp = await http.GetAsync("https://api.ipify.org", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunCommandAsync(string arg, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(_cfg.NordVpnExePath, arg)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is not null)
                await proc.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore eseguendo nordvpn.exe {Arg}", arg);
        }
    }

    private static async Task<bool> WaitForInterfaceStateAsync(bool up, int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            if (IsInterfaceUp() == up) return true;
            await Task.Delay(1000, ct);
        }
        return IsInterfaceUp() == up;
    }
}
