using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Tests;

/// <summary>
/// Verification of a transaction an operator recorded by hand. Every rejection here is a case where accepting
/// the claim would move money incorrectly: discharging a merchant's reserved balance, or crediting custody,
/// against a transfer that did not happen as stated. The USDT contract, recipient and Transfer-log shape are
/// the same real vector the deposit scanner's mapping is tested against.
/// </summary>
public sealed class TronTransactionVerifierTests
{
    private const string UsdtBase58 = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
    private const string Recipient = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";
    private const string Hash = "0xdeadbeef";

    private static readonly Guid UsdtAssetId = Guid.CreateVersion7();
    private static readonly BigInteger OneUsdt = BigInteger.Parse("1000000"); // 0xf4240, 6dp

    private static TronLogDto TransferLog(string amountHex, string to = Recipient, string hash = Hash) => new()
    {
        Address = "0x" + TronAddress.ToEvmHex(UsdtBase58),
        Topics =
        [
            TronConstants.TransferEventSignature,
            "0x" + new string('0', 24) + TronAddress.ToEvmHex(UsdtBase58),
            "0x" + new string('0', 24) + TronAddress.ToEvmHex(to),
        ],
        Data = amountHex,
        BlockNumber = "0x64",
        BlockHash = "0xblockhash",
        TransactionHash = hash,
        LogIndex = "0x3",
    };

    private static TronTransactionVerifier Verifier(
        TronTransactionInfoDto? info, long solidified = 200, params TronLogDto[] logs) =>
        new(new FakeRpc(info, solidified, logs), new FakeCatalog(), NullLogger<TronTransactionVerifier>.Instance);

    private static VerifyTransferRequest Request(BigInteger? minimum = null, string to = Recipient) =>
        new(Chain.Tron, Hash, to, UsdtAssetId, minimum ?? OneUsdt);

    private static TronTransactionInfoDto Mined(long block = 100) => new() { Id = Hash, BlockNumber = block };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_confirmed_transfer_of_the_expected_amount_verifies()
    {
        var result = await Verifier(Mined(), 200, TransferLog("0xf4240")).VerifyAsync(Request(), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Amount.ShouldBe(OneUsdt);
        result.Value.To.ShouldBe(Recipient);
        result.Value.BlockNumber.ShouldBe(100);
    }

    [Fact]
    public async Task An_unknown_hash_is_not_found()
    {
        var result = await Verifier(info: null).VerifyAsync(Request(), Ct);
        result.Error!.Code.ShouldBe(TransactionVerificationErrors.NotFound.Code);
    }

    [Fact]
    public async Task A_mined_but_unsolidified_transfer_is_refused_as_unconfirmed()
    {
        // Mined at 100 but the chain has only solidified to 50: still reversible. Settling against it could
        // permanently discharge a merchant's balance for a transfer a reorg then erases.
        var result = await Verifier(Mined(100), solidified: 50, TransferLog("0xf4240")).VerifyAsync(Request(), Ct);

        result.Error!.Code.ShouldBe(TransactionVerificationErrors.NotConfirmed.Code);
    }

    [Fact]
    public async Task A_reverted_contract_call_is_refused()
    {
        var reverted = new TronTransactionInfoDto
        {
            Id = Hash, BlockNumber = 100, Receipt = new TronReceiptDto { Result = "REVERT" },
        };

        var result = await Verifier(reverted, 200, TransferLog("0xf4240")).VerifyAsync(Request(), Ct);
        result.Error!.Code.ShouldBe(TransactionVerificationErrors.Failed.Code);
    }

    [Fact]
    public async Task A_top_level_failure_is_refused_even_with_an_empty_receipt()
    {
        // Native transactions report failure at the top level and leave the receipt empty — checking only the
        // receipt would read this as success.
        var failed = new TronTransactionInfoDto { Id = Hash, BlockNumber = 100, Result = "FAILED" };

        var result = await Verifier(failed, 200, TransferLog("0xf4240")).VerifyAsync(Request(), Ct);
        result.Error!.Code.ShouldBe(TransactionVerificationErrors.Failed.Code);
    }

    [Fact]
    public async Task A_transfer_to_a_different_address_is_refused()
    {
        // The paste-the-wrong-hash case: a real, confirmed transfer that paid somebody else.
        var elsewhere = TransferLog("0xf4240", to: "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t");

        var result = await Verifier(Mined(), 200, elsewhere).VerifyAsync(Request(), Ct);
        result.Error!.Code.ShouldBe(TransactionVerificationErrors.DestinationMismatch.Code);
    }

    [Fact]
    public async Task A_transfer_smaller_than_expected_is_refused()
    {
        // Underpaying must never discharge the full obligation.
        var result = await Verifier(Mined(), 200, TransferLog("0xf4240")).VerifyAsync(
            Request(minimum: BigInteger.Parse("2000000")), Ct);

        result.Error!.Code.ShouldBe(TransactionVerificationErrors.AmountMismatch.Code);
    }

    [Fact]
    public async Task A_larger_transfer_is_accepted()
    {
        // Overpaying is an operational matter, not a correctness failure — the obligation is still met.
        var result = await Verifier(Mined(), 200, TransferLog("0x1e8480")).VerifyAsync(Request(), Ct); // 2 USDT

        result.IsSuccess.ShouldBeTrue();
        result.Value.Amount.ShouldBe(BigInteger.Parse("2000000"));
    }

    [Fact]
    public async Task Several_legs_to_the_same_address_are_summed()
    {
        // A batched payout may pay the same address twice in one transaction; what matters is the total.
        var result = await Verifier(Mined(), 200, TransferLog("0xf4240"), TransferLog("0xf4240"))
            .VerifyAsync(Request(minimum: BigInteger.Parse("2000000")), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Amount.ShouldBe(BigInteger.Parse("2000000"));
    }

    [Fact]
    public async Task Logs_belonging_to_another_transaction_in_the_same_block_are_ignored()
    {
        // The block is read whole, so the transaction hash — not merely the block — must select the transfer.
        var otherTx = TransferLog("0xf4240", hash: "0xsomeoneelse");

        var result = await Verifier(Mined(), 200, otherTx).VerifyAsync(Request(), Ct);
        result.Error!.Code.ShouldBe(TransactionVerificationErrors.AssetMismatch.Code);
    }

    [Fact]
    public async Task A_confirmed_transaction_carrying_no_matching_transfer_is_refused()
    {
        var result = await Verifier(Mined(), 200).VerifyAsync(Request(), Ct);
        result.Error!.Code.ShouldBe(TransactionVerificationErrors.AssetMismatch.Code);
    }

    [Fact]
    public async Task A_non_tron_chain_is_unsupported_rather_than_silently_passing()
    {
        var result = await Verifier(Mined(), 200, TransferLog("0xf4240"))
            .VerifyAsync(new VerifyTransferRequest(Chain.Ethereum, Hash, Recipient, UsdtAssetId, OneUsdt), Ct);

        result.Error!.Code.ShouldBe(TransactionVerificationErrors.Unsupported.Code);
    }

    private sealed class FakeRpc(TronTransactionInfoDto? info, long solidified, TronLogDto[] logs) : ITronRpc
    {
        public Task<long> GetBlockNumberAsync(CancellationToken ct = default) => Task.FromResult(solidified + 20);

        public Task<TronBlockDto?> GetBlockByNumberAsync(long blockNumber, CancellationToken ct = default) =>
            Task.FromResult<TronBlockDto?>(null);

        public Task<IReadOnlyList<TronLogDto>> GetTransferLogsAsync(
            long fromBlock, long toBlock, IReadOnlyCollection<string> contractHexAddresses, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TronLogDto>>(logs);

        public Task<long> GetSolidifiedBlockNumberAsync(CancellationToken ct = default) => Task.FromResult(solidified);

        public Task<IReadOnlyList<TronNativeBlockDto>> GetBlockRangeAsync(long fromBlock, long toBlock, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TronNativeBlockDto>>([]);

        public Task<string> CallContractAsync(string contractHexAddress, string dataHex, CancellationToken ct = default) =>
            Task.FromResult("0x");

        public Task<BigInteger> GetNativeBalanceAsync(string evmHexAddress, CancellationToken ct = default) =>
            Task.FromResult(BigInteger.Zero);

        public Task<TronTransactionInfoDto?> GetTransactionInfoAsync(string transactionId, CancellationToken ct = default) =>
            Task.FromResult(info);
    }

    private sealed class FakeCatalog : IAssetCatalog
    {
        private static readonly AssetDto Usdt = new(UsdtAssetId, Chain.Tron, "USDT", UsdtBase58, 6, false);

        public Task<AssetDto?> FindByIdAsync(Guid assetId, CancellationToken ct = default) =>
            Task.FromResult<AssetDto?>(assetId == UsdtAssetId ? Usdt : null);

        public Task<AssetDto?> FindAsync(Chain chain, string symbol, CancellationToken ct = default) =>
            Task.FromResult<AssetDto?>(Usdt);

        public Task<IReadOnlyList<AssetDto>> GetActiveAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AssetDto>>([Usdt]);
    }
}
