using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// Resolves a deposit address → its signing-key reference (consumed by Sweep). Proves the reference conveys
/// both the seed and the child index, and that an unknown address returns null so sweeping stays inert (§10).
/// </summary>
public sealed class DepositSigningKeyDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IHdWalletRepository _repository = Substitute.For<IHdWalletRepository>();

    [Fact]
    public async Task Composes_the_key_reference_as_seed_reference_plus_child_index()
    {
        _repository.FindDepositSigningKeyByAddressAsync(Chain.Tron, "TDep", Arg.Any<CancellationToken>())
            .Returns(new DepositSigningKeyInfo(Guid.CreateVersion7(), Chain.Tron, "kms://tron/seed/merchant-1", 7));

        var key = await new DepositSigningKeyDirectory(_repository).FindByAddressAsync(Chain.Tron, "TDep", Ct);

        key.ShouldNotBeNull();
        key.Chain.ShouldBe(Chain.Tron);
        // An HD deposit key needs both the seed and the index — the signer resolves this composite (§10).
        key.KeyReference.ShouldBe("kms://tron/seed/merchant-1#7");
    }

    [Fact]
    public async Task Returns_null_for_an_unknown_address_so_sweeping_stays_inert()
    {
        _repository.FindDepositSigningKeyByAddressAsync(Chain.Tron, "TNobody", Arg.Any<CancellationToken>())
            .Returns((DepositSigningKeyInfo?)null);

        (await new DepositSigningKeyDirectory(_repository).FindByAddressAsync(Chain.Tron, "TNobody", Ct)).ShouldBeNull();
    }
}
