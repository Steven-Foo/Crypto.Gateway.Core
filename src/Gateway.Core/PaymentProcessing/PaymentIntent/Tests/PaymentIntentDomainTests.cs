using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Events;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;
using PaymentIntentEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain.PaymentIntent;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Tests;

public sealed class PaymentIntentDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static PaymentIntentEntity NewWaiting(BigInteger? expected = null) =>
        PaymentIntentEntity.Create(
            Guid.CreateVersion7(), "tx-1", Chain.Tron, Guid.CreateVersion7(), Guid.CreateVersion7(), "TAddr",
            expected ?? BigInteger.Parse("1000000"), null, Now.AddMinutes(30), Now.AddMinutes(40), Now).Value;

    [Fact]
    public void Create_starts_waiting_with_an_unguessable_reference_distinct_from_the_pk()
    {
        var intent = NewWaiting();
        intent.Status.ShouldBe(PaymentIntentStatus.Waiting);
        intent.PublicReference.ShouldNotBe(Guid.Empty);
        intent.PublicReference.ShouldNotBe(intent.Id);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Create_rejects_a_non_positive_amount(string amount) =>
        PaymentIntentEntity.Create(
            Guid.CreateVersion7(), "tx", Chain.Tron, Guid.CreateVersion7(), Guid.CreateVersion7(), "TAddr",
            BigInteger.Parse(amount), null, Now.AddMinutes(30), Now.AddMinutes(40), Now)
            .Error!.Code.ShouldBe(PaymentIntentErrors.AmountNotPositive.Code);

    [Fact]
    public void Create_rejects_expiry_in_the_past() =>
        PaymentIntentEntity.Create(
            Guid.CreateVersion7(), "tx", Chain.Tron, Guid.CreateVersion7(), Guid.CreateVersion7(), "TAddr",
            BigInteger.One, null, Now.AddMinutes(-1), Now.AddMinutes(9), Now)
            .Error!.Code.ShouldBe(PaymentIntentErrors.ExpiryInPast.Code);

    [Fact]
    public void Create_rejects_a_grace_expiry_before_the_display_expiry() =>
        PaymentIntentEntity.Create(
            Guid.CreateVersion7(), "tx", Chain.Tron, Guid.CreateVersion7(), Guid.CreateVersion7(), "TAddr",
            BigInteger.One, null, Now.AddMinutes(30), Now.AddMinutes(20), Now)
            .Error!.Code.ShouldBe(PaymentIntentErrors.GraceExpiryBeforeExpiry.Code);

    [Fact]
    public void A_deposit_confirming_within_the_grace_window_still_matches()
    {
        // The payer would already see "expired" (past ExpiresAt) but the wallet is still reserved and
        // matchable — Status only flips to Expired once ExpireStaleAsync sweeps past GraceExpiresAt.
        var intent = NewWaiting();
        var duringGrace = Now.AddMinutes(35); // past ExpiresAt (30), before GraceExpiresAt (40)

        intent.MatchTo(Guid.CreateVersion7(), "0xtx", BigInteger.Parse("1000000"), duringGrace).IsSuccess.ShouldBeTrue();
        intent.Status.ShouldBe(PaymentIntentStatus.Matched);
    }

    [Fact]
    public void Matching_an_exact_amount_flags_a_match()
    {
        var intent = NewWaiting(BigInteger.Parse("1000000"));
        intent.MatchTo(Guid.CreateVersion7(), "0xtx", BigInteger.Parse("1000000"), Now).IsSuccess.ShouldBeTrue();
        intent.Status.ShouldBe(PaymentIntentStatus.Matched);
        intent.AmountMatched.ShouldBe(true);
    }

    [Fact]
    public void Matching_a_wrong_amount_still_matches_but_flags_the_mismatch()
    {
        var intent = NewWaiting(BigInteger.Parse("1000000"));
        intent.MatchTo(Guid.CreateVersion7(), "0xtx", BigInteger.Parse("999999"), Now);
        intent.Status.ShouldBe(PaymentIntentStatus.Matched);
        intent.AmountMatched.ShouldBe(false);
    }

    [Fact]
    public void Matching_is_idempotent_and_the_first_outcome_wins()
    {
        var intent = NewWaiting();
        var firstDeposit = Guid.CreateVersion7();

        intent.MatchTo(firstDeposit, "0xtx", BigInteger.Parse("1000000"), Now);
        intent.MatchTo(Guid.CreateVersion7(), "0xtx", BigInteger.Parse("500000"), Now); // redelivery — ignored

        intent.MatchedDepositId.ShouldBe(firstDeposit);
        intent.AmountMatched.ShouldBe(true);
    }

    [Fact]
    public void An_expired_intent_cannot_be_matched()
    {
        var intent = NewWaiting();
        intent.Expire(Now);
        intent.MatchTo(Guid.CreateVersion7(), "0xtx", BigInteger.Parse("1000000"), Now);
        intent.Status.ShouldBe(PaymentIntentStatus.Expired);
        intent.MatchedDepositId.ShouldBeNull();
    }

    [Fact]
    public void Failing_a_waiting_intent_succeeds_and_raises_PaymentIntentFailed()
    {
        var intent = NewWaiting();
        var result = intent.Fail("merchant testing", Now);

        result.IsSuccess.ShouldBeTrue();
        intent.Status.ShouldBe(PaymentIntentStatus.Failed);
        intent.DomainEvents.OfType<PaymentIntentFailed>().Single().Reason.ShouldBe("merchant testing");
    }

    [Fact]
    public void Failing_an_already_matched_intent_is_rejected_not_silently_ignored()
    {
        var intent = NewWaiting();
        intent.MatchTo(Guid.CreateVersion7(), "0xtx", BigInteger.Parse("1000000"), Now);

        var result = intent.Fail("too late", Now);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(PaymentIntentErrors.InvalidStateTransition.Code);
        intent.Status.ShouldBe(PaymentIntentStatus.Matched); // unchanged
    }

    [Fact]
    public void Failing_an_already_expired_intent_is_rejected()
    {
        var intent = NewWaiting();
        intent.Expire(Now);

        intent.Fail("too late", Now).IsFailure.ShouldBeTrue();
        intent.Status.ShouldBe(PaymentIntentStatus.Expired);
    }
}
