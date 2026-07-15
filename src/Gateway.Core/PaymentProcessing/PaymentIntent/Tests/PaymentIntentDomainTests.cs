using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
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
            expected ?? BigInteger.Parse("1000000"), null, Now.AddMinutes(30), Now).Value;

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
            BigInteger.Parse(amount), null, Now.AddMinutes(30), Now)
            .Error!.Code.ShouldBe(PaymentIntentErrors.AmountNotPositive.Code);

    [Fact]
    public void Create_rejects_expiry_in_the_past() =>
        PaymentIntentEntity.Create(
            Guid.CreateVersion7(), "tx", Chain.Tron, Guid.CreateVersion7(), Guid.CreateVersion7(), "TAddr",
            BigInteger.One, null, Now.AddMinutes(-1), Now)
            .Error!.Code.ShouldBe(PaymentIntentErrors.ExpiryInPast.Code);

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
}
