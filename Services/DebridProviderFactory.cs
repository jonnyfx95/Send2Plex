using SendToPlex.Bot.Models;

namespace SendToPlex.Bot.Services;

/// <summary>
/// Risolve quale <see cref="IDebridClient"/> usare per un dato <see cref="DebridProvider"/>
/// (docs/piano-multi-provider-debrid.md) — un magnet/file resta legato a un solo provider per
/// tutta la sua vita, mai interrogato su entrambi contemporaneamente.
/// </summary>
public class DebridProviderFactory
{
    private readonly AllDebridClient _allDebrid;
    private readonly RealDebridClient _realDebrid;
    private readonly PremiumizeClient _premiumize;

    public DebridProviderFactory(AllDebridClient allDebrid, RealDebridClient realDebrid, PremiumizeClient premiumize)
    {
        _allDebrid = allDebrid;
        _realDebrid = realDebrid;
        _premiumize = premiumize;
    }

    public IDebridClient Get(DebridProvider provider) => provider switch
    {
        DebridProvider.RealDebrid => _realDebrid,
        DebridProvider.Premiumize => _premiumize,
        _ => _allDebrid
    };
}
