namespace SendToPlex.Bot.Helpers;

public static class ProgressBarHelper
{
    public static string CreateBar(double percent, int totalBars = 10)
    {
        var filledBars = (int)Math.Round(percent / (100.0 / totalBars));
        var emptyBars = totalBars - filledBars;

        var filled = string.Concat(Enumerable.Repeat("🟩", Math.Max(0, filledBars)));
        var empty = string.Concat(Enumerable.Repeat("⬜", Math.Max(0, emptyBars)));

        return filled + empty;
    }
}
