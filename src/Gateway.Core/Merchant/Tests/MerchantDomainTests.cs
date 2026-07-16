using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;
using MerchantEntity = CryptoPaymentEngine.Gateway.Core.Merchant.Domain.Merchant;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

public sealed class MerchantDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    private static MerchantEntity NewMerchant(string code = "ACME-1", string? callbackUrl = null) =>
        MerchantEntity.Create(code, "Acme Payments", callbackUrl).Value;

    [Fact]
    public void Create_normalises_code_and_trims_name()
    {
        var merchant = MerchantEntity.Create("  acme-1  ", "  Acme Payments  ", null).Value;

        merchant.MerchantCode.ShouldBe("ACME-1");
        merchant.Name.ShouldBe("Acme Payments");
        merchant.Status.ShouldBe(MerchantStatus.Pending);
        merchant.CanTransact.ShouldBeFalse();
    }

    [Fact]
    public void Create_seeds_a_default_configuration()
    {
        var merchant = NewMerchant();

        merchant.Configuration.ShouldNotBeNull();
        merchant.Configuration.MerchantId.ShouldBe(merchant.Id);
        merchant.Configuration.WebhookRetryCount.ShouldBe(MerchantConfiguration.DefaultWebhookRetryCount);
        merchant.Configuration.IsEnabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]                       // too short
    [InlineData("has space")]
    [InlineData("bad!char")]
    public void Create_rejects_invalid_code(string code) =>
        MerchantEntity.Create(code, "Acme", null).IsFailure.ShouldBeTrue();

    [Fact]
    public void Create_rejects_code_longer_than_64_characters() =>
        MerchantEntity.Create(new string('A', 65), "Acme", null).IsFailure.ShouldBeTrue();

    [Fact]
    public void Create_rejects_missing_name() =>
        MerchantEntity.Create("ACME", "  ", null).Error!.Code.ShouldBe(MerchantErrors.NameRequired.Code);

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("/relative/hook")]
    [InlineData("not-a-url")]
    public void Create_rejects_non_http_callback_url(string url) =>
        MerchantEntity.Create("ACME", "Acme", url).Error!.Code.ShouldBe(MerchantErrors.CallbackUrlInvalid.Code);

    [Theory]
    [InlineData("https://example.com/hook")]
    [InlineData("http://localhost:5000/hook")]
    public void Create_accepts_http_and_https_callback_url(string url) =>
        MerchantEntity.Create("ACME", "Acme", url).IsSuccess.ShouldBeTrue();

    [Fact]
    public void Activate_makes_the_merchant_transactable()
    {
        var merchant = NewMerchant();
        merchant.Activate(Now).IsSuccess.ShouldBeTrue();
        merchant.CanTransact.ShouldBeTrue();

        merchant.Suspend(Now).IsSuccess.ShouldBeTrue();
        merchant.CanTransact.ShouldBeFalse();
    }

    [Fact]
    public void A_closed_merchant_is_terminal()
    {
        var merchant = NewMerchant();
        merchant.Close(Now);

        merchant.Activate(Now).Error!.Code.ShouldBe(MerchantErrors.Closed.Code);
        merchant.UpdateCallbackUrl("https://x.test/h", Now).Error!.Code.ShouldBe(MerchantErrors.Closed.Code);
        merchant.IssueCredential("k", "h", 1, "cipher", Now).Error!.Code.ShouldBe(MerchantErrors.Closed.Code);
        merchant.UpdateConfiguration(true, 3, true, Now).Error!.Code.ShouldBe(MerchantErrors.Closed.Code);
        merchant.SetAssetPolicy(Guid.CreateVersion7(), 1, 1, 2, FeeSchedule.None, Now).Error!.Code.ShouldBe(MerchantErrors.Closed.Code);
        merchant.SetAssetPolicy(Guid.CreateVersion7(), 1, 1, 2, 0, Now).Error!.Code.ShouldBe(MerchantErrors.Closed.Code);
        merchant.UpdateAllowedIps(["1.2.3.4"], Now).Error!.Code.ShouldBe(MerchantErrors.Closed.Code);
    }

    // ── Allowed IPs ────────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_merchant_has_no_allowed_ips()
    {
        NewMerchant().Configuration.AllowedIps.ShouldBeEmpty();
    }

    [Fact]
    public void Setting_allowed_ips_reports_everything_as_added()
    {
        var merchant = NewMerchant();

        var change = merchant.UpdateAllowedIps(["1.1.1.1", "2.2.2.2"], Now).Value;

        change.Added.ShouldBe(["1.1.1.1", "2.2.2.2"], ignoreOrder: true);
        change.Removed.ShouldBeEmpty();
        change.Current.ShouldBe(["1.1.1.1", "2.2.2.2"], ignoreOrder: true);
        merchant.Configuration.AllowedIps.ShouldBe(["1.1.1.1", "2.2.2.2"], ignoreOrder: true);
    }

    [Fact]
    public void Updating_allowed_ips_reports_only_the_delta()
    {
        var merchant = NewMerchant();
        merchant.UpdateAllowedIps(["1.1.1.1", "2.2.2.2"], Now);

        var change = merchant.UpdateAllowedIps(["2.2.2.2", "3.3.3.3"], Now).Value;

        change.Added.ShouldBe(["3.3.3.3"]);
        change.Removed.ShouldBe(["1.1.1.1"]);
        change.Current.ShouldBe(["2.2.2.2", "3.3.3.3"], ignoreOrder: true);
    }

    [Fact]
    public void Clearing_allowed_ips_removes_everything_and_stores_null()
    {
        var merchant = NewMerchant();
        merchant.UpdateAllowedIps(["1.1.1.1"], Now);

        var change = merchant.UpdateAllowedIps([], Now).Value;

        change.Removed.ShouldBe(["1.1.1.1"]);
        merchant.Configuration.AllowedIpsCsv.ShouldBeNull();
        merchant.Configuration.AllowedIps.ShouldBeEmpty();
    }

    [Fact]
    public void Updating_allowed_ips_is_case_insensitive_for_the_diff()
    {
        // IPv4 text has no casing, but IPv6 does — the comparer must not treat 'AB::1' and 'ab::1' as a
        // remove-then-add churn against Cloudflare.
        var merchant = NewMerchant();
        merchant.UpdateAllowedIps(["AB::1"], Now);

        var change = merchant.UpdateAllowedIps(["ab::1"], Now).Value;

        change.Added.ShouldBeEmpty();
        change.Removed.ShouldBeEmpty();
    }

    [Fact]
    public void Credentials_can_be_issued_more_than_once_to_allow_rotation()
    {
        var merchant = NewMerchant();

        merchant.IssueCredential("key-1", "hash-1", 1, "cipher", Now);
        merchant.IssueCredential("key-2", "hash-2", 1, "cipher", Now);

        merchant.Credentials.Count(c => c.IsActive).ShouldBe(2);
    }

    [Fact]
    public void Revoking_a_credential_twice_is_rejected()
    {
        var merchant = NewMerchant();
        var credential = merchant.IssueCredential("key-1", "hash-1", 1, "cipher", Now).Value;

        merchant.RevokeCredential(credential.Id, Now).IsSuccess.ShouldBeTrue();
        credential.IsActive.ShouldBeFalse();
        credential.RevokedAt.ShouldBe(Now);

        merchant.RevokeCredential(credential.Id, Now).Error!.Code.ShouldBe(MerchantErrors.CredentialAlreadyRevoked.Code);
    }

    [Fact]
    public void Revoking_an_unknown_credential_is_rejected() =>
        NewMerchant().RevokeCredential(Guid.CreateVersion7(), Now).Error!.Code
            .ShouldBe(MerchantErrors.CredentialNotFound.Code);

    [Fact]
    public void Set_asset_policy_upserts_rather_than_duplicating()
    {
        var merchant = NewMerchant();
        var assetId = Guid.CreateVersion7();

        merchant.SetAssetPolicy(assetId, 100, 10, 1000, FeeSchedule.Create(0, 0, 1, 0).Value, Now).IsSuccess.ShouldBeTrue();
        merchant.SetAssetPolicy(assetId, 200, 20, 2000, FeeSchedule.Create(0, 0, 2, 0).Value, Now).IsSuccess.ShouldBeTrue();

        merchant.AssetPolicies.Count.ShouldBe(1);
        merchant.AssetPolicies[0].SweepThreshold.ShouldBe(new BigInteger(200));
        merchant.AssetPolicies[0].MaximumWithdrawal.ShouldBe(new BigInteger(2000));
    }

    [Fact]
    public void Set_asset_policy_allows_a_null_maximum_meaning_unlimited()
    {
        var merchant = NewMerchant();
        merchant.SetAssetPolicy(Guid.CreateVersion7(), 100, 10, null, FeeSchedule.Create(0, 0, 1, 0).Value, Now).IsSuccess.ShouldBeTrue();
        merchant.AssetPolicies[0].MaximumWithdrawal.ShouldBeNull();
    }

    [Fact]
    public void Set_asset_policy_rejects_minimum_above_maximum() =>
        NewMerchant().SetAssetPolicy(Guid.CreateVersion7(), 0, 100, 10, FeeSchedule.None, Now)
            .Error!.Code.ShouldBe(MerchantErrors.WithdrawalRangeInvalid.Code);

    [Fact]
    public void Set_asset_policy_rejects_negative_amounts() =>
        NewMerchant().SetAssetPolicy(Guid.CreateVersion7(), BigInteger.MinusOne, 0, null, FeeSchedule.None, Now)
            .Error!.Code.ShouldBe(MerchantErrors.AmountNegative.Code);

    [Fact]
    public void Set_asset_policy_rejects_amounts_beyond_the_38_digit_limit()
    {
        var tooLarge = MoneyLimits.MaxValue + BigInteger.One;

        NewMerchant().SetAssetPolicy(Guid.CreateVersion7(), tooLarge, 0, null, FeeSchedule.None, Now)
            .Error!.Code.ShouldBe(MerchantErrors.AmountTooLarge.Code);
    }

    [Fact]
    public void Set_asset_policy_accepts_the_maximum_storable_amount() =>
        NewMerchant().SetAssetPolicy(Guid.CreateVersion7(), MoneyLimits.MaxValue, 0, null, FeeSchedule.None, Now)
            .IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(-1)]
    [InlineData(21)]
    public void Configuration_rejects_out_of_range_retry_count(int retryCount) =>
        NewMerchant().UpdateConfiguration(true, retryCount, true, Now)
            .Error!.Code.ShouldBe(MerchantErrors.WebhookRetryCountInvalid.Code);

    [Fact]
    public void Webhook_becomes_exhausted_once_retries_are_used_up()
    {
        var webhook = MerchantWebhook.Queue(Guid.CreateVersion7(), "deposit.confirmed", "{}", Now);

        webhook.MarkFailed("500", maxRetries: 2, nextRetryAt: Now.AddMinutes(1), failedAt: Now);
        webhook.Status.ShouldBe(WebhookDeliveryStatus.Failed);
        webhook.NextRetryAt.ShouldNotBeNull();

        webhook.MarkFailed("500", maxRetries: 2, nextRetryAt: Now.AddMinutes(2), failedAt: Now);
        webhook.Status.ShouldBe(WebhookDeliveryStatus.Exhausted);
        webhook.NextRetryAt.ShouldBeNull(); // never retried again
    }

    [Fact]
    public void Webhook_truncates_an_oversized_response_body()
    {
        var webhook = MerchantWebhook.Queue(Guid.CreateVersion7(), "e", "{}", Now);

        webhook.MarkDelivered(new string('x', MerchantWebhook.MaxResponseLength + 500), Now);

        webhook.LastResponse!.Length.ShouldBe(MerchantWebhook.MaxResponseLength);
    }

    [Fact]
    public void Delivered_webhook_clears_its_retry_schedule()
    {
        var webhook = MerchantWebhook.Queue(Guid.CreateVersion7(), "e", "{}", Now);
        webhook.MarkFailed("500", 5, Now.AddMinutes(1), Now);

        webhook.MarkDelivered("200 OK", Now);

        webhook.Status.ShouldBe(WebhookDeliveryStatus.Delivered);
        webhook.NextRetryAt.ShouldBeNull();
    }
}
