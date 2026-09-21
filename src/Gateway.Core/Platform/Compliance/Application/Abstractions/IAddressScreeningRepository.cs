using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;

/// <summary>Persistence port for the append-only evidence trail.</summary>
public interface IAddressScreeningRepository
{
    /// <summary>The most recent screening for an address, or null. Address matching is case-insensitive
    /// on the normalised column so a caller cannot accidentally split one address's history in two.</summary>
    Task<AddressScreening?> FindLatestAsync(Chain chain, string address, CancellationToken cancellationToken = default);

    /// <summary>Append one evidence row. Never an update — the aggregate has no mutators.</summary>
    Task AddAsync(AddressScreening screening, CancellationToken cancellationToken = default);

    /// <summary>The addresses among those given that currently hold a FRESH verdict. The caller subtracts
    /// this from its candidate list, so the query returns the small set rather than the large one.</summary>
    Task<IReadOnlyList<string>> FindFreshlyScreenedAsync(
        Chain chain,
        IReadOnlyCollection<string> addresses,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
