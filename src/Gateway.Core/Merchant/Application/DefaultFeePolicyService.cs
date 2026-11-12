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
    string MinimumWithdrawalFee,
    string? MinimumDeposit = null,
    string? MaximumDeposit = null,
    string? MinimumWithdrawal = null,
    string? MaximumWithdrawal = null,
    string? ApprovalThreshold = null);

/// <summary>Platform-default limits for one asset, base units. Null on a bound = no platform default.</summary>
public sealed record DefaultLimits(
    BigInteger? MinimumDeposit, BigInteger? MaximumDeposit, BigInteger? MinimumWithdrawal,
    BigInteger? MaximumWithdrawal, BigInteger? ApprovalThreshold);

/// <summary>
/// The platform defaults, per asset: what a merchant is charged and limited by wherever it has no value of its own
/// (see <see cref="DefaultFeePolicy"/>). Saving a default never edits any merchant's own values.
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

    /// <summary>Replaces the asset's platform-default limits (every bound at once; null clears one).</summary>
    Task<Result<DefaultFeePolicyView>> SetLimitsAsync(
        Guid assetId, DefaultLimits limits, CancellationToken cancellationToken = default);
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

    public async Task<Result<DefaultFeePolicyView>> SetLimitsAsync(
        Guid assetId, DefaultLimits limits, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var policy = await repository.FindByAssetIdAsync(assetId, cancellationToken);
        var isNew = policy is null;
        policy ??= DefaultFeePolicy.CreateForLimits(assetId, now).Value;

        var set = policy.SetLimits(
            limits.MinimumDeposit, limits.MaximumDeposit, limits.MinimumWithdrawal, limits.MaximumWithdrawal,
            limits.ApprovalThreshold, now);
        if (set.IsFailure)
            return Result.Failure<DefaultFeePolicyView>(set.Error!);

        if (isNew)
            repository.Add(policy);
        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success(ToView(policy));
    }

    private static DefaultFeePolicyView ToView(DefaultFeePolicy p) => new(
        p.AssetId,
        p.DepositFeeFixed.ToString(),
        p.DepositFeeBps,
        p.MinimumDepositFee.ToString(),
        p.WithdrawalFee.ToString(),
        p.WithdrawalFeeBps,
        p.MinimumWithdrawalFee.ToString(),
        p.MinimumDeposit?.ToString(),
        p.MaximumDeposit?.ToString(),
        p.MinimumWithdrawal?.ToString(),
        p.MaximumWithdrawal?.ToString(),
        p.ApprovalThreshold?.ToString());
}
