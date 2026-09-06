using System.Security.Cryptography;
using System.Text;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Cifra/decifra le credenziali sensibili salvate su disco tramite Windows DPAPI
/// (scope CurrentUser): la protezione è legata all'account Windows dell'utente,
/// coerente con un'app desktop mono-utente. Non protegge da processi che girano
/// già come lo stesso utente, ma impedisce che il file, se copiato o condiviso,
/// riveli le credenziali in chiaro.
/// </summary>
internal static class CredentialProtector
{
    private const string Prefix = "dpapi:";

    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText ?? "";

        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        if (!IsProtected(value)) return value; // già in chiaro (file legacy o non ancora salvato)

        try
        {
            var encrypted = Convert.FromBase64String(value.Substring(Prefix.Length));
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
}
