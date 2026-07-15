using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Configuration;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Configuration;

/// <summary>
/// A config-backed asset catalog: the canonical <c>AssetId</c> for each listed asset is fixed in config, so
/// the same identity is shared by the API edge (display↔base-unit conversion), the deposit scanner, and the
/// ledger — never derived independently in two places. A DB-backed catalog can replace this later behind the
/// same <see cref="IAssetCatalog"/> Contract with no consumer change. Loaded once at startup; assets are static.
/// </summary>
public sealed class ConfigurationAssetCatalog : IAssetCatalog
{
    public const string SectionName = "Blockchain:Assets";

    private readonly IReadOnlyList<AssetDto> _assets;

    public ConfigurationAssetCatalog(IConfiguration configuration)
    {
        var entries = configuration.GetSection(SectionName).Get<List<AssetEntry>>() ?? [];
        _assets = entries
            .Select(e => new AssetDto(
                e.AssetId,
                e.Chain,
                e.Symbol.Trim().ToUpperInvariant(),
                string.IsNullOrWhiteSpace(e.ContractAddress) ? null : e.ContractAddress.Trim(),
                e.Decimals,
                IsNative: string.IsNullOrWhiteSpace(e.ContractAddress)))
            .ToList();
    }

    public Task<AssetDto?> FindByIdAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_assets.FirstOrDefault(a => a.AssetId == assetId));

    public Task<AssetDto?> FindAsync(Chain chain, string symbol, CancellationToken cancellationToken = default)
    {
        var normalised = symbol.Trim().ToUpperInvariant();
        return Task.FromResult(_assets.FirstOrDefault(a => a.Chain == chain && a.Symbol == normalised));
    }

    public Task<IReadOnlyList<AssetDto>> GetActiveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_assets);

    private sealed class AssetEntry
    {
        public Guid AssetId { get; init; }
        public Chain Chain { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public string? ContractAddress { get; init; }
        public int Decimals { get; init; }
    }
}
