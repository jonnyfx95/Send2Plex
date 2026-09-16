using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Cifra/decifra le credenziali sensibili salvate su disco. Fino al porting cross-platform
/// (docs/idee-porting-multipiattaforma.md) usava solo Windows DPAPI (prefisso "dpapi:") — non
/// disponibile su Linux (<see cref="ProtectedData"/> lancia <see cref="PlatformNotSupportedException"/>
/// a runtime fuori da Windows). Ora scrive sempre con ASP.NET Core Data Protection (prefisso
/// "dp:", stesso key ring persistito in Program.cs per gli antiforgery token — vedi
/// AppPaths.DataProtectionKeys), cross-platform per costruzione. Il vecchio formato "dpapi:"
/// resta leggibile SOLO su Windows (le due copie già in uso non perdono le credenziali salvate:
/// il valore migra da solo al nuovo formato al primo salvataggio da Impostazioni) — su qualunque
/// altra piattaforma un valore "dpapi:" non può esistere per davvero (nessuna build precedente
/// girava lì), il fallback esiste solo per non far crashare nulla in un caso limite.
/// </summary>
internal static class CredentialProtector
{
    private const string LegacyDpapiPrefix = "dpapi:";
    private const string Prefix = "dp:";

    // Factory statica invece di un servizio iniettato: ConfigManager.LoadConfig() gira PRIMA che
    // il container DI sia costruito (vedi Program.cs), quindi non può ricevere un
    // IDataProtectionProvider da DI. DataProtectionProvider.Create funziona comunque fuori da un
    // host ASP.NET Core, purché punti alla stessa cartella registrata più tardi in Program.cs con
    // AddDataProtection().PersistKeysToFileSystem(...) — stesso key ring su disco per entrambi.
    private static readonly Lazy<IDataProtector> Protector = new(() =>
        DataProtectionProvider
            .Create(new DirectoryInfo(AppPaths.DataProtectionKeys), o => o.SetApplicationName("Send2Plex"))
            .CreateProtector("Send2Plex.Credentials.v1"));

    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) &&
        (value.StartsWith(Prefix, StringComparison.Ordinal) || value.StartsWith(LegacyDpapiPrefix, StringComparison.Ordinal));

    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText ?? "";
        return Prefix + Protector.Value.Protect(plainText);
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";

        if (value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try { return Protector.Value.Unprotect(value.Substring(Prefix.Length)); }
            catch
            {
                // Key ring mancante/diverso o dati corrotti: meglio un valore vuoto che un
                // crash, l'utente dovrà reinserire la credenziale dalle Impostazioni.
                return "";
            }
        }

        if (value.StartsWith(LegacyDpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) return ""; // non dovrebbe mai capitare in pratica
            try
            {
                var encrypted = Convert.FromBase64String(value.Substring(LegacyDpapiPrefix.Length));
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                // Cifrato con un altro account Windows o dati corrotti: meglio un valore vuoto
                // che un crash, l'utente dovrà reinserire la credenziale dalle Impostazioni.
                return "";
            }
        }

        return value; // già in chiaro (file legacy pre-cifratura, o non ancora salvato)
    }
}
