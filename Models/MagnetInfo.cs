namespace SendToPlex.Bot.Models;

public class MagnetInfo
{
    public string Id { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Status { get; set; } = string.Empty;
    public long UploadDate { get; set; }
    public bool IsReady => string.Equals(Status, "Ready", StringComparison.OrdinalIgnoreCase);
}
