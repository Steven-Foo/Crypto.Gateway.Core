using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;

/// <summary>
/// The Blockchain module's public asset catalog. Other modules depend on this shape, never on the
/// Blockchain module's internals. <see cref="AssetDto.Decimals"/> is for edge display conversion
/// only — never for money math.
/// </summary>
public sealed record AssetDto(
    Guid AssetId,
    Chain Chain,
    string Symbol,
    string? ContractAddress,
    int Decimals,
    bool IsNative);

public interface IAssetCatalog
{
    Task<AssetDto?> FindByIdAsync(Guid assetId, CancellationToken cancellationToken = default);

    Task<AssetDto?> FindAsync(Chain chain, string symbol, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetDto>> GetActiveAsync(CancellationToken cancellationToken = default);
}
