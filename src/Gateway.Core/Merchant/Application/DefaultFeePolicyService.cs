using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application;

/// <summary>
/// One asset's default deposit/withdrawal pricing template — read back for the create-merchant form's
/// pre-fill. Amounts are exact base-unit integer strings (§14); the host converts to/from display at the edge.
/// </summary>
public sealed record DefaultFeePolicyView(
    Guid AssetId,
    string DepositFeeFixed,
    int DepositFeeBps,
    string MinimumDepositFee,
    string WithdrawalFee,
    int WithdrawalFeeBps,
    string MinimumWithdrawalFee);

/// <summary>
/// Staff-editable default fee template, per asset — pure storage for the create-merchant screen's pre-fill.
/// Setting a value here does not touch any merchant's own pricing, and does not change how an already-unpriced
/// merchant's fee is resolved at charge time (that stays <c>MerchantDefaultFee</c>, untouched).
/// </summary>
public interface IDefaultFeePolicyService
{
    Task<Result<IReadOnlyList<DefaultFeePolicyView>>> ListAsync(CancellationToken cancellationToken = default);

    Task<Result<DefaultFeePolicyView>> SetAsync(
        Guid assetId,
        BigInteger depositFeeFixed,
        int depositFeeBps,
        BigInteger minimumDepositFee,
        BigInteger withdrawalFee,
        int withdrawalFeeBps,
        BigInteger minimumWithdrawalFee,
        CancellationToken cancellationToken = default);
}

public sealed class DefaultFeePolicyService(
    IDefaultFeePolicyRepository repository, TimeProvider timeProvider) : IDefaultFeePolicyService
{
    public async Task<Result<IReadOnlyList<DefaultFeePolicyView>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var policies = await repository.ListAsync(cancellationToken);
        IReadOnlyList<DefaultFeePolicyView> views = [.. policies.Select(ToView)];
        return Result.Success(views);
    }

    public async Task<Result<DefaultFeePolicyView>> SetAsync(
        Guid assetId,
        BigInteger depositFeeFixed,
        int depositFeeBps,
        BigInteger minimumDepositFee,
        BigInteger withdrawalFee,
        int withdrawalFeeBps,
        BigInteger minimumWithdrawalFee,
        CancellationToken cancellationToken = default)
    {
        // FeeSchedule.Create is reused purely for its validation (bps bounds, non-negative, storable) — top-up
        // is fixed at zero and never persisted here, a default template has no top-up concept.
        var fees = FeeSchedule.Create(
            depositFeeFixed, depositFeeBps, withdrawalFee, withdrawalFeeBps,
            BigInteger.Zero, 0, minimumDepositFee, minimumWithdrawalFee);
        if (fees.IsFailure)
            return Result.Failure<DefaultFeePolicyView>(fees.Error!);

        var now = timeProvider.GetUtcNow();
        var existing = await repository.FindByAssetIdAsync(assetId, cancellationToken);

        if (existing is null)
        {
            var created = DefaultFeePolicy.Create(assetId, fees.Value, now);
            if (created.IsFailure)
                return Result.Failure<DefaultFeePolicyView>(created.Error!);

            repository.Add(created.Value);
            await repository.SaveChangesAsync(cancellationToken);
            return Result.Success(ToView(created.Value));
        }

        var updated = existing.Update(fees.Value, now);
        if (updated.IsFailure)
            return Result.Failure<DefaultFeePolicyView>(updated.Error!);

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success(ToView(existing));
    }

    private static DefaultFeePolicyView ToView(DefaultFeePolicy p) => new(
        p.AssetId,
        p.DepositFeeFixed.ToString(),
        p.DepositFeeBps,
        p.MinimumDepositFee.ToString(),
        p.WithdrawalFee.ToString(),
        p.WithdrawalFeeBps,
        p.MinimumWithdrawalFee.ToString());
}
