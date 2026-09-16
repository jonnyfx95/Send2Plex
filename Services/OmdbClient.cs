using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

public class OmdbRating
{
    public double? ImdbRating { get; set; }
    public int? ImdbVotes { get; set; }
}

// Client minimale per OMDb (omdbapi.com): unica fonte del voto IMDb vero (distinto dal
// vote_average di TMDB, già mostrato nella pagina di dettaglio WebOS). Non fornisce backdrop —
// solo un poster, di qualità inferiore a quello già preso da TMDB — quindi qui si legge solo il
// voto/numero voti tramite l'IMDb ID già risolto da TmdbClient.GetImdbIdAsync.
public class OmdbClient
{
    private readonly HttpClient _http;
    private readonly ConfigStore _configStore;
    private readonly ILogger<OmdbClient> _log;

    private OmdbSettings _cfg => _configStore.Current.Omdb;

    public OmdbClient(HttpClient http, ConfigStore configStore, ILogger<OmdbClient> log)
    {
        _http = http;
        _configStore = configStore;
        _log = log;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_cfg.ApiKey);

    public async Task<OmdbRating?> GetRatingAsync(string imdbId, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(imdbId)) return null;

        var url = $"https://www.omdbapi.com/?i={Uri.EscapeDataString(imdbId)}&apikey={Uri.EscapeDataString(_cfg.ApiKey)}";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var payload = await response.Content.ReadFromJsonAsync<OmdbResponse>(cancellationToken: ct);
            if (payload is null || string.Equals(payload.Response, "False", StringComparison.OrdinalIgnoreCase))
                return null;

            var rating = double.TryParse(payload.ImdbRating, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : (double?)null;
            var votes = int.TryParse(payload.ImdbVotes?.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;

            return new OmdbRating { ImdbRating = rating, ImdbVotes = votes };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ Errore chiamando OMDb per {ImdbId}", imdbId);
            return null;
        }
    }

    private class OmdbResponse
    {
        [JsonPropertyName("imdbRating")]
        public string? ImdbRating { get; set; }

        [JsonPropertyName("imdbVotes")]
        public string? ImdbVotes { get; set; }

        [JsonPropertyName("Response")]
        public string? Response { get; set; }
    }
}
