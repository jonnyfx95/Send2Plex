namespace SendToPlex.Bot.Models;

public enum DebridProvider
{
    AllDebrid,
    RealDebrid,
    Premiumize
}

/// <summary>
/// Conversioni condivise verso le stringhe usate dall'API (query string "provider", preferenze
/// TV/generali) e dalla UI (nome visualizzato) — un solo punto invece di ternari sparsi nei vari
/// file che consumano <see cref="DebridProvider"/>, altrimenti aggiungere un terzo provider
/// richiederebbe individuare a mano ogni ternario "A ? RealDebrid : AllDebrid" nel codebase.
/// </summary>
public static class DebridProviderNames
{
    public static string ToApiValue(this DebridProvider provider) => provider switch
    {
        DebridProvider.RealDebrid => "realDebrid",
        DebridProvider.Premiumize => "premiumize",
        _ => "allDebrid"
    };

    public static string ToDisplayName(this DebridProvider provider) => provider switch
    {
        DebridProvider.RealDebrid => "Real-Debrid",
        DebridProvider.Premiumize => "Premiumize",
        _ => "AllDebrid"
    };

    // Nome file in wwwroot/img/provider/ e assets/provider/ (webOS) — "premiumizeme", non
    // "premiumize", per come si chiama il file icona fornito.
    public static string ToIconFileName(this DebridProvider provider) => provider switch
    {
        DebridProvider.RealDebrid => "realdebrid",
        DebridProvider.Premiumize => "premiumizeme",
        _ => "alldebrid"
    };

    public static DebridProvider ParseApiValue(string? value) => value?.ToLowerInvariant() switch
    {
        "realdebrid" => DebridProvider.RealDebrid,
        "premiumize" => DebridProvider.Premiumize,
        _ => DebridProvider.AllDebrid
    };
}
