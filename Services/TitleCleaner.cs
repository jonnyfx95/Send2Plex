using System.Text.RegularExpressions;

namespace SendToPlex.Bot.Services;

// Ripulisce un nome file/torrent grezzo (es. "Spider-Man.Brand.New.Day.2026.iTA.ENG.MD.1080p.HDTS.x264-WRS.mkv")
// in un titolo cercabile su TMDB ("Spider-Man Brand New Day") — usato per recuperare una copertina
// vera per gli elementi della libreria Plex che non hanno ancora un match/poster (appena scaricati).
public static class TitleCleaner
{
    private static readonly Regex SeasonEpisode = new(@"\bS\d{1,2}E\d{1,3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Year = new(@"\b(19\d{2}|20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex QualityTag = new(@"\b(2160p|1080p|720p|480p|4K|UHD|HDR|HDTS|HDCAM|WEBDL|WEB-DL|WEBRip|BluRay|Bluray|BDRip|HEVC|x264|x265|AC3|AAC|MULTI|MD|iTA|ITA|ENG)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Clean(string rawTitle)
    {
        var name = Path.GetFileNameWithoutExtension(rawTitle).Replace('.', ' ').Replace('_', ' ');

        var seMatch = SeasonEpisode.Match(name);
        if (seMatch.Success) name = name[..seMatch.Index];

        var yearMatch = Year.Match(name);
        if (yearMatch.Success) name = name[..yearMatch.Index];

        var qualityMatch = QualityTag.Match(name);
        if (qualityMatch.Success) name = name[..qualityMatch.Index];

        return Regex.Replace(name, @"\s+", " ").Trim(' ', '-', '(', '.');
    }
}
