namespace SendToPlex.Bot.Models;

public class TorrentSearchResult
{
    public string SiteName { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Magnet { get; set; }
    public string? DetailUrl { get; set; }
    public string? SizeText { get; set; }
    public string? SeedsText { get; set; }
    public long SeedsNumeric { get; set; }
    public string? MagnetSelectorOnDetailPage { get; set; }
    public string? Cookie { get; set; } // ereditato dal sito, serve per ri-risolvere il magnet dalla pagina di dettaglio
    public bool UseBrowser { get; set; } // ereditato dal sito: usa WebView2 anche per la pagina di dettaglio
    public string? RevealClickSelector { get; set; } // ereditato dal sito: elemento da cliccare prima di leggere il magnet (es. "Ringrazia")
    public string? SourceListName { get; set; } // nome della pagina indice (lista alfabetica) da cui proviene, se presente
}
