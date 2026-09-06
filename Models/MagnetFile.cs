namespace SendToPlex.Bot.Models;

public class MagnetFile
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Link { get; set; } = string.Empty;
}
