using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Contratto comune ad AllDebrid e Real-Debrid (docs/piano-multi-provider-debrid.md): stesse 7
/// firme già usate da tutti i chiamanti di <see cref="AllDebridClient"/>, così sostituire il tipo
/// iniettato con questa interfaccia è un cambio meccanico, non una riscrittura di logica.
/// </summary>
public interface IDebridClient
{
    Task<string> UnlockLinkAsync(string url, CancellationToken ct);
    Task<string> UploadMagnetAsync(string magnet, CancellationToken ct);
    Task<List<MagnetInfo>> GetMagnetsAsync(CancellationToken ct);
    Task<bool> WaitReadyAsync(string id, TimeSpan timeout, CancellationToken ct);
    Task<List<string>> GetMagnetLinksAsync(string id, CancellationToken ct);
    Task<List<MagnetFile>> GetMagnetFilesDetailedAsync(string id, CancellationToken ct);
    Task DeleteMagnetAsync(string id, CancellationToken ct);
}
