using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;
using CryptoPaymentEngine.SharedKernel;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Tests;

/// <summary>
/// The TRON on-chain balance reader (§8) that Reconciliation sums across controlled addresses. Money-critical:
/// a wrong contract, calldata, or decode would make custody drift look real (or hide a real one). Exercised
/// against an <see cref="ITronRpc"/> fake — the live round-trip is a staging concern like the other adapters.
/// </summary>
public sealed class TronBalanceReaderTests
{
    private const string Usdt = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
    private const string Holder = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";
    private static readonly Guid UsdtAssetId = Guid.CreateVersion7();
    private static readonly Guid TrxAssetId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ITronRpc _rpc = Substitute.For<ITronRpc>();
    private readonly IAssetCatalog _catalog = Substitute.For<IAssetCatalog>();

    private TronBalanceReader Reader => new(_rpc, _catalog);

    [Fact]
    public async Task Reads_a_TRC20_token_balance_via_eth_call_balanceOf_to_the_right_contract()
    {
        _catalog.FindByIdAsync(UsdtAssetId, Arg.Any<CancellationToken>())
            .Returns(new AssetDto(UsdtAssetId, Chain.Tron, "USDT", Usdt, 6, IsNative: false));
        _rpc.CallContractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("0x00000000000000000000000000000000000000000000000000000000004c4b40"); // 5_000_000

        var balance = await Reader.GetBalanceAsync(Chain.Tron, Holder, UsdtAssetId, Ct);

        balance.ShouldBe(BigInteger.Parse("5000000")); // 5 USDT, exact base units
        await _rpc.Received(1).CallContractAsync(
            TronAddress.ToEvmHex(Usdt),        // the token contract, EVM-hex
            TronAbi.EncodeBalanceOf(Holder),   // balanceOf(holder) calldata
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reads_native_TRX_via_eth_getBalance()
    {
        _catalog.FindByIdAsync(TrxAssetId, Arg.Any<CancellationToken>())
            .Returns(new AssetDto(TrxAssetId, Chain.Tron, "TRX", ContractAddress: null, 6, IsNative: true));
        _rpc.GetNativeBalanceAsync(TronAddress.ToEvmHex(Holder), Arg.Any<CancellationToken>())
            .Returns(new BigInteger(123_456)); // sun

        (await Reader.GetBalanceAsync(Chain.Tron, Holder, TrxAssetId, Ct)).ShouldBe(new BigInteger(123_456));
        await _rpc.DidNotReceive().CallContractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unfunded_holder_reads_as_zero_not_an_error()
    {
        _catalog.FindByIdAsync(UsdtAssetId, Arg.Any<CancellationToken>())
            .Returns(new AssetDto(UsdtAssetId, Chain.Tron, "USDT", Usdt, 6, IsNative: false));
        _rpc.CallContractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("0x");

        (await Reader.GetBalanceAsync(Chain.Tron, Holder, UsdtAssetId, Ct)).ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task An_unknown_asset_throws_rather_than_silently_reading_zero()
    {
        _catalog.FindByIdAsync(UsdtAssetId, Arg.Any<CancellationToken>()).Returns((AssetDto?)null);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await Reader.GetBalanceAsync(Chain.Tron, Holder, UsdtAssetId, Ct));
    }

    [Fact]
    public async Task A_non_Tron_chain_is_rejected()
    {
        await Should.ThrowAsync<ArgumentException>(
            async () => await Reader.GetBalanceAsync(Chain.Ethereum, Holder, UsdtAssetId, Ct));
    }
}
