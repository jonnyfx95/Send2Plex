using System.Text.RegularExpressions;
using AngleSharp;
using Microsoft.Extensions.Logging;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

public enum LoginStatus { LoggedIn, LoggedOut, Unknown, Error }

public class LoginStatusResult
{
    public LoginStatus Status { get; set; }
    public string? Detail { get; set; }
}

/// <summary>
/// Verifica se la sessione salvata nel profilo WebView2 (login manuale fatto una tantum da
/// Impostazioni) è ancora valida per un sito — Punto 5. Riusa lo stesso BrowserFetcher/profilo
/// già usato per la ricerca, quindi i cookie di sessione sono gli stessi.
/// </summary>
public class LoginStatusChecker
{
    private readonly BrowserFetcher _browser;
    private readonly NordVpnController _vpn;
    private readonly ILogger<LoginStatusChecker> _log;

    // Parole che indicano quasi sempre una sessione autenticata sui forum italiani/inglesi tipici
    // (icv-crew, ilCorsaroBlu sono forum SMF/phpBB) — usata solo se il sito non ha un selettore
    // CSS specifico configurato.
    private static readonly Regex LoggedInHeuristic = new(
        @"\b(logout|esci(?:\s+dal\s+forum)?|disconnetti|il mio profilo|my profile)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public LoginStatusChecker(BrowserFetcher browser, NordVpnController vpn, ILogger<LoginStatusChecker> log)
    {
        _browser = browser;
        _vpn = vpn;
        _log = log;
    }

    public async Task<LoginStatusResult> CheckAsync(TorrentSiteConfig site, int timeoutSeconds, CancellationToken ct)
    {
        var checkUrl = site.SearchFormPageUrl ?? site.LoginUrl;
        if (string.IsNullOrWhiteSpace(checkUrl))
            return new LoginStatusResult { Status = LoginStatus.Unknown, Detail = "Nessuna pagina da controllare configurata." };

        try
        {
            if (site.RequiresVpn)
                await _vpn.ConnectAsync(ct);

            var html = await _browser.GetRenderedHtmlAsync(checkUrl, timeoutSeconds, ct);

            if (!string.IsNullOrWhiteSpace(site.LoggedInIndicatorSelector))
            {
                var browsingContext = BrowsingContext.New(Configuration.Default);
                using var document = await browsingContext.OpenAsync(req => req.Content(html).Address(checkUrl), ct);
                var found = document.QuerySelector(site.LoggedInIndicatorSelector) is not null;
                return new LoginStatusResult
                {
                    Status = found ? LoginStatus.LoggedIn : LoginStatus.LoggedOut,
                    Detail = $"Selettore \"{site.LoggedInIndicatorSelector}\""
                };
            }

            var loggedIn = LoggedInHeuristic.IsMatch(html);
            return new LoginStatusResult
            {
                Status = loggedIn ? LoginStatus.LoggedIn : LoginStatus.LoggedOut,
                Detail = "Rilevato con euristica generica (nessun selettore specifico configurato per questo sito)."
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore verificando lo stato di login per {Site}", site.Name);
            return new LoginStatusResult { Status = LoginStatus.Error, Detail = ex.Message };
        }
    }
}
