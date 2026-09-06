using System.Net.Http;
using Polly;
using Polly.Extensions.Http;

namespace SendToPlex.Bot.UI;

public static class PollyHelper
{
    public static IAsyncPolicy<HttpResponseMessage> RetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => (int)r.StatusCode == 429)
            .WaitAndRetryAsync(5, retry => TimeSpan.FromSeconds(Math.Pow(2, retry)));
}
