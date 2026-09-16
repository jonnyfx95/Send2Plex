namespace SendToPlex.Bot.Models;

/// <summary>Un singolo episodio contenuto in un'acquisizione (pacco stagione) — vuoto/assente per
/// i film. Vedi docs/piano-premiumize-libreria.md, granularità di retention decisa "a transfer
/// intero": si cancella solo quando TUTTI gli episodi qui elencati risultano visti.</summary>
public class AcquiredEpisode
{
    public int Season { get; set; }
    public int Episode { get; set; }
}

/// <summary>
/// Un file/pacco acquisito nel cloud Premiumize e reso disponibile a Plex tramite un mount WebDAV
/// (docs/piano-premiumize-libreria.md, feature "Acquisisci in libreria") — a differenza di un
/// download normale (che finisce su disco locale e va solo in <see cref="Services.DownloadHistoryService"/>),
/// questo resta "vivo" su Premiumize finché <see cref="Services.LibraryRetentionService"/> non lo
/// cancella (automaticamente, dopo la visione) o l'utente non lo rimuove a mano dalla sezione
/// "Premiumize" della pagina Libreria.
/// </summary>
public class LibraryAcquisition
{
    // Id del transfer su Premiumize (da IDebridClient.UploadMagnetAsync) — usato per la
    // cancellazione (IDebridClient.DeleteMagnetAsync), non serve altro id per gestirlo.
    public string TransferId { get; set; } = "";
    public int SectionId { get; set; }
    public bool ToTv { get; set; }
    public string Title { get; set; } = "";
    public int TmdbId { get; set; }

    // null per i film; per le serie, tutti gli episodi effettivamente presenti nel transfer
    // (dedotti dai nomi file al momento dell'acquisizione, pattern SxxExx).
    public List<AcquiredEpisode>? Episodes { get; set; }

    public DateTimeOffset AcquiredAt { get; set; }
}
