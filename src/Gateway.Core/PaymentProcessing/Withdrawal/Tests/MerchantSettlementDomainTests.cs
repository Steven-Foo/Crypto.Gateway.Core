using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

/// <summary>
/// The merchant-settlement path: this system does not pay a merchant cash-out. An admin audits the request,
/// finance pays it from a company wallet OUTSIDE platform custody, and the transaction is recorded here after
/// being verified on-chain.
///
/// <para>The reserve is held from request to settlement, so the merchant's money can never be spent twice —
/// and a rejection at either stage must return it, never strand it.</para>
/// </summary>
public sealed class MerchantSettlementDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger Amount = BigInteger.Parse("3000000");
    private static readonly BigInteger Fee = BigInteger.Parse("100000");

    private static WithdrawalEntity CashOut(WithdrawalKind kind = WithdrawalKind.Merchant) =>
        WithdrawalEntity.Request(
            Merchant, Asset, Chain.Tron, "TSettlementWallet", Amount, Fee, "cash-1", null, Now, kind).Value;

    /// <summary>A reserved cash-out sitting in the admin audit queue.</summary>
    private static WithdrawalEntity AwaitingAudit()
    {
        var w = CashOut();
        w.ConfirmReserved(requiresApproval: false, Now);
        return w;
    }

    private static WithdrawalEntity AwaitingFinance()
    {
        var w = AwaitingAudit();
        w.AdminAuditApprove("admin", Now);
        return w;
    }

    [Fact]
    public void A_reserved_cash_out_waits_for_an_admin_audit_not_the_signer()
    {
        // The divert that keeps merchant settlements out of the automated build/sign/broadcast pipeline.
        AwaitingAudit().Status.ShouldBe(WithdrawalStatus.PendingAdminAudit);
    }

    [Fact]
    public void A_reserved_user_payout_still_goes_straight_to_the_automated_pipeline()
    {
        // The regression guard: user payouts must be completely unaffected by the settlement path.
        var w = CashOut(WithdrawalKind.User);
        w.ConfirmReserved(requiresApproval: false, Now);
        w.Status.ShouldBe(WithdrawalStatus.Approved);
    }

    [Fact]
    public void Platform_approval_of_a_cash_out_hands_it_to_audit_rather_than_the_signer()
    {
        var w = CashOut();
        w.ConfirmReserved(requiresApproval: true, Now);
        w.Status.ShouldBe(WithdrawalStatus.PendingApproval);

        w.Approve("staff", Now).IsSuccess.ShouldBeTrue();
        w.Status.ShouldBe(WithdrawalStatus.PendingAdminAudit);
    }

    [Fact]
    public void An_audited_cash_out_waits_for_finance_to_pay_it()
    {
        var w = AwaitingFinance();
        w.Status.ShouldBe(WithdrawalStatus.PendingFinanceTransfer);
        w.AuditedBy.ShouldBe("admin");
        w.AuditedAt.ShouldBe(Now);
    }

    [Fact]
    public void Recording_the_finance_payment_settles_it_externally()
    {
        var w = AwaitingFinance();

        w.RecordFinanceSettlement("finance", "0xabc", "TCompanyWallet", Now).IsSuccess.ShouldBeTrue();

        w.Status.ShouldBe(WithdrawalStatus.FinanceSettled);
        w.TransactionHash.ShouldBe("0xabc");
        w.SettlementSourceAddress.ShouldBe("TCompanyWallet");
        w.CompletedBy.ShouldBe("finance");

        // THE money-critical assertion: the settle event must say it was paid externally, or the Ledger will
        // credit TreasuryAsset and decrement custody that never actually moved.
        var confirmed = w.DomainEvents.OfType<WithdrawalConfirmed>().ShouldHaveSingleItem();
        confirmed.ExternallySettled.ShouldBeTrue();
        confirmed.AmountBaseUnits.ShouldBe(Amount.ToString());
    }

    [Fact]
    public void An_automated_payout_confirmation_is_never_marked_externally_settled()
    {
        var w = CashOut(WithdrawalKind.User);
        w.ConfirmReserved(requiresApproval: false, Now);
        w.BeginSigning(Guid.CreateVersion7(), Now);
        w.RecordSigned(w.SigningRequestId!.Value, Guid.CreateVersion7(), [1, 2, 3], Now);
        w.MarkBroadcast("0xdead", Now);
        w.Confirm(Now);

        w.DomainEvents.OfType<WithdrawalConfirmed>().ShouldHaveSingleItem().ExternallySettled.ShouldBeFalse();
    }

    [Fact]
    public void An_audit_rejection_returns_the_merchant_their_money()
    {
        var w = AwaitingAudit();

        w.AdminAuditReject("admin", "failed KYC review", Now).IsSuccess.ShouldBeTrue();

        w.Status.ShouldBe(WithdrawalStatus.Rejected);
        // A decline must release the reserve — otherwise the merchant's balance is stranded in clearing.
        w.DomainEvents.OfType<WithdrawalFailed>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Finance_can_still_reject_after_the_audit_passed()
    {
        // If finance discovers a problem before paying, the reserve must remain releasable rather than stuck
        // in PendingFinanceTransfer with no way out.
        var w = AwaitingFinance();

        w.AdminAuditReject("finance", "merchant asked to cancel", Now).IsSuccess.ShouldBeTrue();

        w.Status.ShouldBe(WithdrawalStatus.Rejected);
        w.DomainEvents.OfType<WithdrawalFailed>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Settlement_cannot_be_recorded_before_the_audit_passes()
    {
        AwaitingAudit().RecordFinanceSettlement("finance", "0xabc", "TCompany", Now)
            .Error!.Code.ShouldBe(WithdrawalErrors.InvalidStateTransition.Code);
    }

    [Fact]
    public void Settlement_cannot_be_recorded_twice()
    {
        var w = AwaitingFinance();
        w.RecordFinanceSettlement("finance", "0xabc", "TCompany", Now).IsSuccess.ShouldBeTrue();

        // Guards against a second settle event, which would discharge the same reserve twice.
        w.RecordFinanceSettlement("finance", "0xdef", "TCompany", Now)
            .Error!.Code.ShouldBe(WithdrawalErrors.InvalidStateTransition.Code);
    }

    [Fact]
    public void A_settlement_requires_a_transaction_hash_and_an_operator()
    {
        AwaitingFinance().RecordFinanceSettlement("finance", "  ", "TCompany", Now)
            .Error!.Code.ShouldBe(WithdrawalErrors.TransactionHashRequired.Code);

        AwaitingFinance().RecordFinanceSettlement("", "0xabc", "TCompany", Now)
            .Error!.Code.ShouldBe(WithdrawalErrors.OwnerRequired.Code);
    }

    [Fact]
    public void A_user_payout_never_enters_the_audit_queue()
    {
        var w = CashOut(WithdrawalKind.User);
        w.ConfirmReserved(requiresApproval: false, Now);

        w.AdminAuditApprove("admin", Now).Error!.Code.ShouldBe(WithdrawalErrors.InvalidStateTransition.Code);
    }
}
