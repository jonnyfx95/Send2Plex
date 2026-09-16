namespace SendToPlex.Bot.Models;

/// <summary>
/// Una riga di <c>folder/list</c> su Premiumize (docs/piano-premiumize-libreria.md, sfoglio diretto
/// delle cartelle organizzate manualmente dall'utente sul cloud Premiumize — nessun mount WebDAV
/// richiesto, i file restano sul cloud e si guardano tramite lo stesso sblocco già usato altrove).
/// </summary>
public class PremiumizeFolderEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }
    public long Size { get; set; }

    // Solo per i file (null per le cartelle) — link diretto, da passare a IDebridClient.UnlockLinkAsync
    // /transfer/directdl esattamente come un file di un magnet, verificato che funzioni identico.
    public string? Link { get; set; }
}
