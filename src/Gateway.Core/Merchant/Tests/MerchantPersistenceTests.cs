using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using MerchantEntity = CryptoPaymentEngine.Gateway.Core.Merchant.Domain.Merchant;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

public sealed class MerchantPersistenceTests : IAsyncLifetime
{
    private const string DbName = "CpeMerchantPersistenceTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static MerchantDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantDbContext>()
            .UseSqlServer(ConnectionString)
            .UseBigIntegerMoney()
            .Options);

    private static HmacApiSecretHasher NewHasher() =>
        new(Options.Create(new ApiCredentialOptions
        {
            CurrentHashVersion = 1,
            Peppers = new Dictionary<int, string> { [1] = "test-pepper-value" },
        }));

    private static AesGcmSecretCipher NewCipher() =>
        new(Options.Create(new SigningSecretOptions
        {
            CurrentKeyVersion = 1,
            Keys = new Dictionary<int, string> { [1] = Convert.ToBase64String(new byte[32]) },
        }));

    /// <summary>Screening off by default, so these tests keep asserting the behaviour that existed before it —
    /// the regression guard that settlement screening is genuinely opt-in.</summary>
    private static MerchantRegistrar NewRegistrar(
        MerchantDbContext context, IAddressScreeningService? screening = null, bool screenSettlementWallets = false) =>
        new(new MerchantRepository(context), new ApiCredentialGenerator(), NewHasher(), NewCipher(), TimeProvider.System,
            screening ?? new NeverCalledScreening(),
            Options.Create(new MerchantScreeningOptions { ScreenSettlementWallets = screenSettlementWallets }));

    /// <summary>Fails loudly if screening is reached while it is switched off — a silent clean verdict would
    /// hide exactly the regression these defaults exist to catch.</summary>
    private sealed class NeverCalledScreening : IAddressScreeningService
    {
        public Task<ScreeningVerdict> ScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Screening must not be called when it is disabled.");

        public Task<ScreeningVerdict> ReScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Screening must not be called when it is disabled.");

        public Task<ScreeningVerdict?> FindLatestAsync(
            Chain chain, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScreeningVerdict?>(null);

        // Candidate filtering is for the address-sweep passes; these stubs stand in for money-path and
        // settlement callers, which never ask.
        public Task<IReadOnlyList<string>> FindAddressesNeedingScreeningAsync(
            Chain chain, IReadOnlyCollection<string> addresses, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>Returns a fixed verdict, for the settlement-screening tests.</summary>
    private sealed class StubScreening(ScreeningDecision decision) : IAddressScreeningService
    {
        public int Calls { get; private set; }

        public Task<ScreeningVerdict> ScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ScreeningVerdict(
                Guid.CreateVersion7(), decision,
                decision == ScreeningDecision.Unavailable ? null : 95,
                decision == ScreeningDecision.Unavailable ? null : "Severe",
                ["Sanctioned Entity"], AddressLabel: null, ReportUrl: null,
                DateTimeOffset.UtcNow, FromCache: false));
        }

        // A money path must never re-screen: bypassing the cache spends a provider call per payout, which
        // is exactly what the rate limit cannot absorb. Re-screening is a deliberate staff action only.
        public Task<ScreeningVerdict> ReScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A money path must not force a re-screen.");

        // Stands in for a stored evidence row, so the admin read-back can be tested without a real
        // evidence table. It deliberately does NOT touch Calls: reading a merchant must never reach the
        // provider, and a test asserting that needs the counter to mean provider calls only.
        public Task<ScreeningVerdict?> FindLatestAsync(
            Chain chain, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScreeningVerdict?>(new ScreeningVerdict(
                StoredScreeningId, decision,
                decision == ScreeningDecision.Unavailable ? null : 95,
                decision == ScreeningDecision.Unavailable ? null : "Severe",
                ["Sanctioned Entity"], AddressLabel: null, ReportUrl: null,
                DateTimeOffset.UtcNow, FromCache: true));

        public static readonly Guid StoredScreeningId = Guid.CreateVersion7();

        // Candidate filtering is for the address-sweep passes; these stubs stand in for money-path and
        // settlement callers, which never ask.
        public Task<IReadOnlyList<string>> FindAddressesNeedingScreeningAsync(
            Chain chain, IReadOnlyCollection<string> addresses, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    public async ValueTask InitializeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Registering_a_merchant_persists_it_with_configuration_and_one_credential()
    {
        MerchantRegistrationResult registration;
        await using (var context = NewContext())
        {
            var result = await NewRegistrar(context).RegisterAsync("Acme Payments", "https://acme.test/hook", Ct);
            result.IsSuccess.ShouldBeTrue();
            registration = result.Value;
        }

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants
                .Include(m => m.Configuration)
                .Include(m => m.Credentials)
                .SingleAsync(m => m.Id == registration.MerchantId, Ct);

            // First registration against a freshly-created (empty) database, so the generated
            // sequence deterministically starts at 1.
            merchant.MerchantCode.ShouldBe("ME00001");
            merchant.Status.ShouldBe(MerchantStatus.Active);
            merchant.Configuration.ShouldNotBeNull();
            merchant.Credentials.Count.ShouldBe(1);
            merchant.Credentials[0].ApiKey.ShouldBe(registration.ApiKey);
            merchant.Credentials[0].HashVersion.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Activating_a_frozen_merchant_makes_it_transactable_again()
    {
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
            (await NewRegistrar(context).FreezeAsync(merchantId, Ct)).IsSuccess.ShouldBeTrue();

        await using (var context = NewContext())
        {
            var frozen = await context.Merchants.SingleAsync(m => m.Id == merchantId, Ct);
            frozen.CanTransact.ShouldBeFalse();
        }

        await using (var context = NewContext())
            (await NewRegistrar(context).ActivateAsync(merchantId, Ct)).IsSuccess.ShouldBeTrue();

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants.SingleAsync(m => m.Id == merchantId, Ct);
            merchant.Status.ShouldBe(MerchantStatus.Active);
            merchant.CanTransact.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Closing_a_merchant_blocks_transacting_and_it_can_be_reopened()
    {
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
            (await NewRegistrar(context).CloseAsync(merchantId, Ct)).IsSuccess.ShouldBeTrue();

        await using (var context = NewContext())
        {
            var closed = await context.Merchants.SingleAsync(m => m.Id == merchantId, Ct);
            closed.Status.ShouldBe(MerchantStatus.Closed);
            closed.CanTransact.ShouldBeFalse();
        }

        // Reopening a closed merchant is a real, supported path (status is never terminal).
        await using (var context = NewContext())
            (await NewRegistrar(context).ActivateAsync(merchantId, Ct)).IsSuccess.ShouldBeTrue();

        await using (var context = NewContext())
        {
            var reopened = await context.Merchants.SingleAsync(m => m.Id == merchantId, Ct);
            reopened.Status.ShouldBe(MerchantStatus.Active);
            reopened.CanTransact.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Closing_an_unknown_merchant_fails()
    {
        await using var context = NewContext();
        (await NewRegistrar(context).CloseAsync(Guid.CreateVersion7(), Ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Activating_an_unknown_merchant_fails()
    {
        await using var context = NewContext();
        (await NewRegistrar(context).ActivateAsync(Guid.CreateVersion7(), Ct)).IsSuccess.ShouldBeFalse();
    }

    /// <summary>The central security property: the plaintext secret must exist nowhere in the DB.</summary>
    [Fact]
    public async Task The_plaintext_api_secret_is_never_persisted()
    {
        MerchantRegistrationResult registration;
        await using (var context = NewContext())
        {
            registration = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value;
        }

        await using (var context = NewContext())
        {
            var credential = await context.Credentials.SingleAsync(Ct);

            credential.SecretHash.ShouldNotBe(registration.ApiSecret);
            credential.SecretHash.ShouldNotContain(registration.ApiSecret);

            // ...and the stored hash actually verifies the secret we handed back exactly once.
            NewHasher().Verify(registration.ApiSecret, credential.SecretHash, credential.HashVersion).ShouldBeTrue();
        }
    }

    /// <summary>
    /// Regression guard for §10: no column may ever exist to hold key material. If someone adds a
    /// `Secret`/`PrivateKey`/`Mnemonic` column to this module, this test fails.
    /// </summary>
    [Fact]
    public async Task No_column_in_the_merchant_schema_can_hold_key_material()
    {
        await using var context = NewContext();

        var offending = await context.Database
            .SqlQueryRaw<string>(
                """
                SELECT c.name AS Value
                FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'merchant'
                  AND (c.name IN ('Secret','ApiSecret','PrivateKey','Mnemonic','Seed','WalletPassword','Password')
                       OR c.name LIKE '%Mnemonic%' OR c.name LIKE '%PrivateKey%')
                """)
            .ToListAsync(Ct);

        offending.ShouldBeEmpty();
    }

    [Fact]
    public async Task Duplicate_merchant_code_is_rejected_by_the_database()
    {
        string generatedCode;
        await using (var context = NewContext())
        {
            var result = await NewRegistrar(context).RegisterAsync("First", null, Ct);
            result.IsSuccess.ShouldBeTrue();
            generatedCode = result.Value.MerchantCode;
        }

        // Bypass the application's friendly pre-check to prove the UNIQUE index is the real arbiter.
        // Reuses the code the registrar just generated, since a caller can no longer choose one.
        await using (var context = NewContext())
        {
            var second = MerchantEntity.Create(generatedCode, "Second", null).Value;
            context.Merchants.Add(second);
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        }
    }

    /// <summary>The generator's pre-check (<c>CodeExistsAsync</c>) skips past a candidate that's already
    /// taken rather than failing outright — proven here by manually claiming "ME00001" (the first code the
    /// generator would try against a fresh database) and confirming registration still succeeds, one
    /// candidate further along.</summary>
    [Fact]
    public async Task Registering_skips_a_generated_code_that_is_already_taken()
    {
        await using (var context = NewContext())
        {
            var taken = MerchantEntity.Create("ME00001", "Manually seeded", null).Value;
            context.Merchants.Add(taken);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            var result = await NewRegistrar(context).RegisterAsync("Acme", null, Ct);

            result.IsSuccess.ShouldBeTrue();
            result.Value.MerchantCode.ShouldBe("ME00002");
        }
    }

    /// <summary>The bounded retry (<c>MerchantRegistrar.MaxCodeGenerationAttempts</c>, currently 8) has to
    /// give up eventually rather than loop forever — proven by manually claiming every candidate code it
    /// would try and confirming a friendly conflict comes back instead of an exception.</summary>
    [Fact]
    public async Task Registrar_returns_a_conflict_when_every_retry_candidate_is_already_taken()
    {
        await using (var context = NewContext())
        {
            for (var i = 1; i <= 8; i++)
                context.Merchants.Add(MerchantEntity.Create($"ME{i:D5}", $"Blocker {i}", null).Value);

            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            var result = await NewRegistrar(context).RegisterAsync("Acme", null, Ct);

            result.IsFailure.ShouldBeTrue();
            result.Error!.Code.ShouldBe(MerchantErrors.CodeAlreadyExists.Code);
            result.Error.Type.ShouldBe(ErrorType.Conflict);
        }
    }

    [Fact]
    public async Task Duplicate_api_key_is_rejected_by_the_database()
    {
        Guid merchantId;
        string apiKey;

        await using (var context = NewContext())
        {
            var registration = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value;
            merchantId = registration.MerchantId;
            apiKey = registration.ApiKey;
        }

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants.Include(m => m.Credentials).SingleAsync(m => m.Id == merchantId, Ct);
            merchant.IssueCredential(apiKey, "another-hash", 1, "cipher", DateTimeOffset.UtcNow);

            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        }
    }

    // ── Money ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Asset_policy_round_trips_a_38_digit_threshold_and_a_null_maximum()
    {
        var assetId = Guid.CreateVersion7();
        var huge = MoneyLimits.MaxValue;
        Guid merchantId;

        await using (var context = NewContext())
        {
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;
        }

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants.Include(m => m.AssetPolicies).SingleAsync(m => m.Id == merchantId, Ct);
            merchant.SetAssetPolicy(assetId, huge, BigInteger.Zero, null, FeeSchedule.Create(0, 0, new BigInteger(1_000_000), 0).Value, DateTimeOffset.UtcNow)
                .IsSuccess.ShouldBeTrue();
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            var policy = await context.AssetPolicies.SingleAsync(p => p.AssetId == assetId, Ct);

            policy.SweepThreshold.ShouldBe(huge);
            policy.SweepThreshold.ToString().Length.ShouldBe(38);
            policy.MaximumWithdrawal.ShouldBeNull();
            policy.WithdrawalFee.ShouldBe(new BigInteger(1_000_000));
        }
    }

    [Fact]
    public async Task Duplicate_asset_policy_for_the_same_asset_is_rejected_by_the_database()
    {
        var assetId = Guid.CreateVersion7();
        Guid merchantId;

        await using (var context = NewContext())
        {
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;
        }

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants.Include(m => m.AssetPolicies).SingleAsync(m => m.Id == merchantId, Ct);
            merchant.SetAssetPolicy(assetId, 1, 0, null, FeeSchedule.None, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        // Insert a second policy row for the same (MerchantId, AssetId), bypassing the aggregate's upsert.
        await using (var context = NewContext())
        {
            var exception = await Should.ThrowAsync<SqlException>(() => context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO merchant.MerchantAssetPolicy
                    (Id, MerchantId, AssetId, SweepThreshold, MinimumWithdrawal, MaximumWithdrawal, WithdrawalFee, CreatedAt, UpdatedAt)
                VALUES (NEWID(), {0}, {1}, 1, 0, NULL, 0, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())
                """,
                [merchantId, assetId], Ct));

            exception.Message.ShouldContain("IX_MerchantAssetPolicy_MerchantId_AssetId");
        }
    }

    /// <summary>The domain blocks min &gt; max; the CHECK constraint blocks it even via raw SQL.</summary>
    [Fact]
    public async Task Check_constraint_blocks_a_maximum_below_the_minimum()
    {
        var assetId = Guid.CreateVersion7();
        Guid merchantId;

        await using (var context = NewContext())
        {
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;
        }

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants.Include(m => m.AssetPolicies).SingleAsync(m => m.Id == merchantId, Ct);
            merchant.SetAssetPolicy(assetId, 0, 100, 1000, FeeSchedule.None, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            var exception = await Should.ThrowAsync<SqlException>(() => context.Database.ExecuteSqlRawAsync(
                "UPDATE merchant.MerchantAssetPolicy SET MaximumWithdrawal = 1 WHERE AssetId = {0}", [assetId], Ct));

            exception.Message.ShouldContain("CK_MerchantAssetPolicy_WithdrawalRange");
        }
    }

    [Fact]
    public async Task Check_constraint_blocks_a_negative_amount_written_via_raw_sql()
    {
        Guid merchantId;
        await using (var context = NewContext())
        {
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;
        }

        await using (var context = NewContext())
        {
            var exception = await Should.ThrowAsync<SqlException>(() => context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO merchant.MerchantAssetPolicy
                    (Id, MerchantId, AssetId, SweepThreshold, MinimumWithdrawal, MaximumWithdrawal, WithdrawalFee, CreatedAt, UpdatedAt)
                VALUES (NEWID(), {0}, NEWID(), -1, 0, NULL, 0, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())
                """,
                [merchantId], Ct));

            exception.Message.ShouldContain("CK_MerchantAssetPolicy_NonNegative");
        }
    }

    // ── Platform-default fee (unpriced merchants) ─────────────────────────────

    private static MerchantFeeSchedule NewFeeSchedule(MerchantDbContext context, int depositBps = 0, int withdrawalBps = 0) =>
        new(context, new MerchantDefaultFee(
            Options.Create(new MerchantDefaultFeeOptions { DepositFeeBps = depositBps, WithdrawalFeeBps = withdrawalBps }),
            NullLogger<MerchantDefaultFee>.Instance));

    [Fact]
    public async Task An_unpriced_merchant_is_charged_the_platform_default_fee()
    {
        var asset = Guid.CreateVersion7();
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var verify = NewContext())
        {
            var fees = NewFeeSchedule(verify, withdrawalBps: 50); // 0.5% platform default
            // No policy for the merchant ⇒ the default applies: 0.5% of 1,000,000 = 5,000.
            (await fees.QuoteWithdrawalFeeAsync(merchantId, asset, new BigInteger(1_000_000), Ct)).Fee
                .ShouldBe(new BigInteger(5_000));
        }
    }

    [Fact]
    public async Task An_explicit_fee_overrides_the_platform_default()
    {
        var asset = Guid.CreateVersion7();
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants.Include(m => m.AssetPolicies).SingleAsync(m => m.Id == merchantId, Ct);
            merchant.SetAssetPolicy(asset, BigInteger.Zero, null, null, FeeSchedule.Create(0, 0, 0, 100).Value, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await using (var verify = NewContext())
        {
            var fees = NewFeeSchedule(verify, withdrawalBps: 50); // default 0.5% would give 5,000...
            // ...but the merchant's own 1% fee wins: 1% of 1,000,000 = 10,000.
            (await fees.QuoteWithdrawalFeeAsync(merchantId, asset, new BigInteger(1_000_000), Ct)).Fee
                .ShouldBe(new BigInteger(10_000));
        }
    }

    [Fact]
    public async Task With_no_configured_default_an_unpriced_merchant_stays_free()
    {
        var asset = Guid.CreateVersion7();
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var verify = NewContext())
        {
            var fees = NewFeeSchedule(verify); // 0/0 = no platform default
            (await fees.QuoteWithdrawalFeeAsync(merchantId, asset, new BigInteger(1_000_000), Ct)).Fee
                .ShouldBe(BigInteger.Zero);
        }
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_updates_to_the_same_merchant_are_caught_by_rowversion()
    {
        Guid merchantId;
        await using (var context = NewContext())
        {
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;
        }

        await using var first = NewContext();
        await using var second = NewContext();

        var a = await first.Merchants.SingleAsync(m => m.Id == merchantId, Ct);
        var b = await second.Merchants.SingleAsync(m => m.Id == merchantId, Ct);

        a.UpdateCallbackUrl("https://first.test/hook", DateTimeOffset.UtcNow);
        await first.SaveChangesAsync(Ct);

        b.UpdateCallbackUrl("https://second.test/hook", DateTimeOffset.UtcNow);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
    }

    // ── Authentication ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_active_merchant_authenticates_with_its_issued_credential()
    {
        MerchantRegistrationResult registration;
        await using (var context = NewContext())
        {
            registration = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value;
            var merchant = await context.Merchants.SingleAsync(m => m.Id == registration.MerchantId, Ct);
            merchant.Activate(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            var authenticator = new MerchantAuthenticator(new MerchantRepository(context), NewHasher());
            var result = await authenticator.AuthenticateAsync(registration.ApiKey, registration.ApiSecret, Ct);

            result.IsSuccess.ShouldBeTrue();
            result.Value.ShouldBe(registration.MerchantId);
        }
    }

    [Fact]
    public async Task A_frozen_merchant_cannot_transact_even_with_valid_credentials()
    {
        MerchantRegistrationResult registration;
        await using (var context = NewContext())
        {
            registration = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value;
        }

        await using (var context = NewContext())
            (await NewRegistrar(context).FreezeAsync(registration.MerchantId, Ct)).IsSuccess.ShouldBeTrue();

        await using (var context = NewContext())
        {
            var authenticator = new MerchantAuthenticator(new MerchantRepository(context), NewHasher());
            var result = await authenticator.AuthenticateAsync(registration.ApiKey, registration.ApiSecret, Ct);

            result.Error!.Code.ShouldBe(MerchantErrors.NotTransactable.Code);
        }
    }

    [Fact]
    public async Task A_wrong_secret_and_an_unknown_key_fail_identically()
    {
        MerchantRegistrationResult registration;
        await using (var context = NewContext())
        {
            registration = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value;
        }

        await using (var context = NewContext())
        {
            var authenticator = new MerchantAuthenticator(new MerchantRepository(context), NewHasher());

            var wrongSecret = await authenticator.AuthenticateAsync(registration.ApiKey, "not-the-secret", Ct);
            var unknownKey = await authenticator.AuthenticateAsync("cpe_does_not_exist", registration.ApiSecret, Ct);

            wrongSecret.Error!.Code.ShouldBe(MerchantErrors.InvalidCredentials.Code);
            unknownKey.Error!.Code.ShouldBe(MerchantErrors.InvalidCredentials.Code);
        }
    }

    [Fact]
    public async Task A_revoked_credential_no_longer_authenticates()
    {
        MerchantRegistrationResult registration;
        await using (var context = NewContext())
        {
            registration = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value;
            var merchant = await context.Merchants.Include(m => m.Credentials).SingleAsync(m => m.Id == registration.MerchantId, Ct);
            merchant.Activate(DateTimeOffset.UtcNow);
            merchant.RevokeCredential(merchant.Credentials[0].Id, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            var authenticator = new MerchantAuthenticator(new MerchantRepository(context), NewHasher());
            var result = await authenticator.AuthenticateAsync(registration.ApiKey, registration.ApiSecret, Ct);

            result.Error!.Code.ShouldBe(MerchantErrors.InvalidCredentials.Code);
        }
    }

    // ── Cross-module contract ─────────────────────────────────────────────────

    [Fact]
    public async Task Merchant_directory_exposes_a_summary_without_credentials()
    {
        Guid merchantId;
        await using (var context = NewContext())
        {
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme Payments", "https://acme.test/h", Ct)).Value.MerchantId;
            var merchant = await context.Merchants.SingleAsync(m => m.Id == merchantId, Ct);
            merchant.Activate(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            // First registration against a freshly-created (empty) database, so the generated
            // sequence deterministically starts at 1 — lower-cased here to double as the
            // case-insensitive lookup check this test always carried.
            var summary = await new MerchantDirectory(context).FindByCodeAsync("me00001", Ct);

            summary.ShouldNotBeNull();
            summary.MerchantId.ShouldBe(merchantId);
            summary.MerchantCode.ShouldBe("ME00001");
            summary.CanTransact.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Merchant_directory_returns_null_for_an_unknown_merchant()
    {
        await using var context = NewContext();
        (await new MerchantDirectory(context).FindByIdAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();
    }

    /// <summary>
    /// The rule that makes "top-up defaults to zero" true in practice. Every other quote falls back to the
    /// platform default when a merchant is unpriced, so an unpriced merchant is never silently free — but
    /// applying that here would mean a merchant is silently CHARGED to fund its own float, which is the
    /// opposite of the intent. Asserted against a configured default that provably applies to a deposit on the
    /// very same merchant and asset, so this cannot pass just because no default was configured.
    /// </summary>
    [Fact]
    public async Task A_top_up_never_inherits_the_platform_default_fee()
    {
        var asset = Guid.CreateVersion7();
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var verify = NewContext())
        {
            var fees = NewFeeSchedule(verify, depositBps: 100); // 1% platform default

            // The control: the default genuinely applies to a customer deposit on this merchant/asset.
            (await fees.QuoteDepositFeeAsync(merchantId, asset, new BigInteger(1_000_000), Ct)).Fee
                .ShouldBe(new BigInteger(10_000));

            // ...but a top-up on the same unpriced merchant is free.
            (await fees.QuoteTopUpFeeAsync(merchantId, asset, new BigInteger(1_000_000), Ct))
                .ShouldBe(BigInteger.Zero, "a merchant must not be charged to fund its own float unless staff priced it");
        }
    }

    /// <summary>An explicitly declared top-up rate IS charged — so the zero above is the absence of a rate,
    /// not the feature being inert.</summary>
    [Fact]
    public async Task An_explicitly_declared_top_up_fee_is_charged()
    {
        var asset = Guid.CreateVersion7();
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants.Include(m => m.AssetPolicies).SingleAsync(m => m.Id == merchantId, Ct);
            // Deposit/withdrawal free, top-up 2% — proving the top-up pair round-trips through persistence
            // independently of the other two.
            merchant.SetAssetPolicy(asset, BigInteger.Zero, null, null, FeeSchedule.Create(0, 0, 0, 0, 0, 200).Value, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await using (var verify = NewContext())
        {
            var fees = NewFeeSchedule(verify);
            (await fees.QuoteTopUpFeeAsync(merchantId, asset, new BigInteger(1_000_000), Ct))
                .ShouldBe(new BigInteger(20_000));
        }
    }

    // ── Settlement-wallet screening ──────────────────────────────────────────────────────────────────────

    private const string SettlementAddress = "TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e";

    /// <summary>
    /// The hard rule. This is the destination every one of that merchant's earnings is paid to, so a refused
    /// address must not be whitelistable at all — not a warning a staff member can click past.
    /// </summary>
    [Fact]
    public async Task A_blocked_address_is_kept_on_file_but_never_becomes_the_cash_out_destination()
    {
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
        {
            var result = await NewRegistrar(context, new StubScreening(ScreeningDecision.Block), true)
                .SetSettlementWalletAsync(merchantId, Chain.Tron, SettlementAddress, Ct);

            result.IsFailure.ShouldBeTrue();
            result.Error!.Code.ShouldBe(MerchantErrors.SettlementWalletBlocked.Code);
        }

        // Whitelisting is never refused — the address stays on file with its verdict recorded — but it is
        // NOT active, so no earnings can be paid to it. Refusing to save it too would lose the record of
        // the decision and force the operator to re-enter an address the platform already judged.
        await using (var verify = NewContext())
        {
            var merchant = await verify.Merchants
                .Include(m => m.SettlementWallets)
                .SingleAsync(m => m.Id == merchantId, Ct);

            var wallet = merchant.SettlementWallets.ShouldHaveSingleItem();
            wallet.Address.ShouldBe(SettlementAddress);
            wallet.IsActive.ShouldBeFalse();
        }

        // And the cash-out flow, which reads only the active one, still finds nothing to pay.
        await using (var verify = NewContext())
        {
            var address = await new MerchantSettlementDirectory(verify)
                .FindSettlementAddressAsync(merchantId, Chain.Tron, Ct);
            address.ShouldBeNull();
        }
    }

    /// <summary>
    /// A flagged address is accepted, because a staff member is already exercising judgement here and may hold
    /// context the provider does not — but the verdict comes back so it can be shown, never swallowed.
    /// </summary>
    [Fact]
    public async Task A_flagged_address_is_accepted_but_returns_a_warning()
    {
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
        {
            var result = await NewRegistrar(context, new StubScreening(ScreeningDecision.Review), true)
                .SetSettlementWalletAsync(merchantId, Chain.Tron, SettlementAddress, Ct);

            result.IsSuccess.ShouldBeTrue();
            result.Value.ScreeningDecision.ShouldBe("Review");
            result.Value.Warnings.ShouldNotBeEmpty();
        }

        await using (var verify = NewContext())
        {
            var merchant = await verify.Merchants
                .Include(m => m.SettlementWallets)
                .SingleAsync(m => m.Id == merchantId, Ct);
            merchant.SettlementWallets.Count.ShouldBe(1);
        }
    }

    /// <summary>
    /// A vendor outage must not block merchant onboarding. Unlike an unattended payout — which holds — this is
    /// a staff action with a human watching, so it proceeds and says so.
    /// </summary>
    [Fact]
    public async Task An_unavailable_provider_still_lets_staff_whitelist_but_warns()
    {
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
        {
            var result = await NewRegistrar(context, new StubScreening(ScreeningDecision.Unavailable), true)
                .SetSettlementWalletAsync(merchantId, Chain.Tron, SettlementAddress, Ct);

            result.IsSuccess.ShouldBeTrue();
            result.Value.ScreeningDecision.ShouldBe("Unavailable");
            result.Value.Warnings.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public async Task A_clean_address_is_whitelisted_with_no_warnings()
    {
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
        {
            var result = await NewRegistrar(context, new StubScreening(ScreeningDecision.Allow), true)
                .SetSettlementWalletAsync(merchantId, Chain.Tron, SettlementAddress, Ct);

            result.IsSuccess.ShouldBeTrue();
            result.Value.ScreeningDecision.ShouldBe("Allow");
            result.Value.Warnings.ShouldBeEmpty("a clean address must be silent");
        }
    }

    /// <summary>
    /// The opt-in guard. With settlement screening off the provider is never contacted — <c>NewRegistrar</c>'s
    /// default screening stub throws if it is, so this passing is proof rather than absence of evidence.
    /// </summary>
    [Fact]
    public async Task With_screening_disabled_whitelisting_never_calls_the_provider()
    {
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
        {
            var result = await NewRegistrar(context)
                .SetSettlementWalletAsync(merchantId, Chain.Tron, SettlementAddress, Ct);

            result.IsSuccess.ShouldBeTrue();
            result.Value.ScreeningDecision.ShouldBeNull("null means 'not screened', distinct from 'Unavailable'");
            result.Value.Warnings.ShouldBeEmpty();
        }
    }

    /// <summary>
    /// Regression guard for the SECOND instance of the settlement-wallet loading defect.
    ///
    /// <para>GetByIdAsync was fixed when replacing a wallet through the Ops endpoint was found to return a
    /// 500. GetByCodeAsync had the identical omission and was missed, so the dev seeder — which resolves a
    /// merchant by code and then sets its settlement wallet — failed the (MerchantId, Chain) unique index on
    /// every boot of an already-seeded database. Both methods hand the aggregate back to be MUTATED, so both
    /// have to load all of it.</para>
    /// </summary>
    [Fact]
    public async Task A_merchant_loaded_by_code_carries_its_settlement_wallets()
    {
        const string Replacement = "TQp6K2nHpqdk5d6q8VjAYLvp1ufUKVpsts";

        Guid merchantId;
        string code;
        await using (var context = NewContext())
        {
            var registered = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value;
            merchantId = registered.MerchantId;
            code = registered.MerchantCode;
        }

        await using (var context = NewContext())
            (await NewRegistrar(context).SetSettlementWalletAsync(merchantId, Chain.Tron, SettlementAddress, Ct))
                .IsSuccess.ShouldBeTrue();

        // The dev seeder's exact shape: load by code, then set a wallet on a merchant that already has one.
        await using (var context = NewContext())
        {
            var repository = new MerchantRepository(context);
            var merchant = await repository.GetByCodeAsync(code, Ct);

            merchant.ShouldNotBeNull();
            merchant.SettlementWallets.Count.ShouldBe(1, "the aggregate must arrive whole, or the next line inserts");

            // The seeder's exact shape, including its two-phase save: retire the previous wallet and SAVE,
            // then activate the replacement. One save for both would be rejected by the filtered unique
            // index whenever EF happened to send the activate first — intermittently, which is worse.
            var prepared = merchant.PrepareSettlementWallet(Chain.Tron, Replacement, DateTimeOffset.UtcNow);
            prepared.IsSuccess.ShouldBeTrue();
            await repository.SaveChangesAsync(Ct);

            merchant.ActivateSettlementWallet(prepared.Value.Id, DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
            await repository.SaveChangesAsync(Ct);
        }

        // The previous address is kept, retired: switching destinations must not erase the record of what
        // was whitelisted before. Exactly one is active, and it is the replacement.
        await using (var verify = NewContext())
        {
            var merchant = await verify.Merchants
                .Include(m => m.SettlementWallets)
                .SingleAsync(m => m.Id == merchantId, Ct);

            merchant.SettlementWallets.Count.ShouldBe(2);
            merchant.SettlementWallets.Single(w => w.IsActive).Address.ShouldBe(Replacement);
            merchant.SettlementWallets.Single(w => !w.IsActive).Address.ShouldBe(SettlementAddress);
        }
    }

    /// <summary>
    /// Staff need the STANDING verdict where they manage a merchant, not only at the moment they saved the
    /// wallet. A verdict is a snapshot, so an address approved months ago may have been re-screened since —
    /// and the periodic pass exists precisely to make that happen.
    ///
    /// <para>Read from stored evidence, so opening a merchant spends no quota and a vendor outage cannot
    /// slow it down or break it.</para>
    /// </summary>
    [Fact]
    public async Task The_admin_read_back_carries_the_standing_screening_verdict()
    {
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
            (await NewRegistrar(context).SetSettlementWalletAsync(merchantId, Chain.Tron, SettlementAddress, Ct))
                .IsSuccess.ShouldBeTrue();

        await using (var read = NewContext())
        {
            var screening = new StubScreening(ScreeningDecision.Review);
            var view = await NewRegistrar(read, screening).GetAsync(merchantId, Ct);

            var wallet = view.Value.SettlementWallets.ShouldHaveSingleItem();
            wallet.Address.ShouldBe(SettlementAddress);
            wallet.ScreeningDecision.ShouldBe("Review");
            wallet.ScreeningScore.ShouldBe(95);

            // The id is what lets a UI deep-link to the evidence rather than repeat the indicators inline.
            wallet.ScreeningId.ShouldBe(StubScreening.StoredScreeningId);

            // Reading a merchant must never call the provider.
            screening.Calls.ShouldBe(0);
        }
    }

    /// <summary>A host that composes Merchant without Compliance still reads merchants. Every screening
    /// field is null, which means "not screened" and must never be rendered as a clean result.</summary>
    [Fact]
    public async Task The_admin_read_back_works_with_no_screening_provider_composed()
    {
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
            (await NewRegistrar(context).SetSettlementWalletAsync(merchantId, Chain.Tron, SettlementAddress, Ct))
                .IsSuccess.ShouldBeTrue();

        await using (var read = NewContext())
        {
            var view = await NewRegistrar(read).GetAsync(merchantId, Ct);

            var wallet = view.Value.SettlementWallets.ShouldHaveSingleItem();
            wallet.ScreeningDecision.ShouldBeNull();
            wallet.ScreeningId.ShouldBeNull();
        }
    }

    /// <summary>
    /// Regression: replacing an existing settlement wallet used to throw a unique-index violation, because
    /// the merchant was loaded without its settlement wallets so the domain could not see one to update.
    /// Staff could set a merchant's cash-out destination once and never change it.
    /// </summary>
    [Fact]
    public async Task An_existing_settlement_wallet_can_be_replaced()
    {
        const string Replacement = "TQp6K2nHpqdk5d6q8VjAYLvp1ufUKVpsts";

        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        await using (var context = NewContext())
            (await NewRegistrar(context).SetSettlementWalletAsync(merchantId, Chain.Tron, SettlementAddress, Ct))
                .IsSuccess.ShouldBeTrue();

        await using (var context = NewContext())
            (await NewRegistrar(context).SetSettlementWalletAsync(merchantId, Chain.Tron, Replacement, Ct))
                .IsSuccess.ShouldBeTrue("replacing a wallet on the same chain must switch which one is active");

        await using (var verify = NewContext())
        {
            var merchant = await verify.Merchants
                .Include(m => m.SettlementWallets)
                .SingleAsync(m => m.Id == merchantId, Ct);

            merchant.SettlementWallets.Count.ShouldBe(2, "the replaced address is retired, not deleted");
            merchant.SettlementWallets.Single(w => w.IsActive).Address.ShouldBe(Replacement);
        }

        // What the cash-out flow actually reads: exactly one address, the active one.
        await using (var verify = NewContext())
        {
            var address = await new MerchantSettlementDirectory(verify)
                .FindSettlementAddressAsync(merchantId, Chain.Tron, Ct);
            address.ShouldBe(Replacement);
        }
    }

    /// <summary>
    /// Several addresses on file, one paid. The activate/retire pair is the only way the destination moves,
    /// and the active one can never be retired out from under a merchant — that would fail every cash-out
    /// with "no settlement wallet registered" for a reason nobody intended.
    /// </summary>
    [Fact]
    public async Task Several_addresses_can_be_whitelisted_with_exactly_one_active()
    {
        const string Second = "TQp6K2nHpqdk5d6q8VjAYLvp1ufUKVpsts";

        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("Acme", null, Ct)).Value.MerchantId;

        Guid firstWalletId;
        Guid secondWalletId;

        await using (var context = NewContext())
        {
            var registrar = NewRegistrar(context);
            firstWalletId = (await registrar.AddSettlementWalletAsync(
                merchantId, Chain.Tron, SettlementAddress, "primary", activate: true, Ct)).Value.WalletId;
        }

        await using (var context = NewContext())
        {
            // Added, deliberately NOT activated: on file and screened, but not yet receiving earnings.
            var added = await NewRegistrar(context).AddSettlementWalletAsync(
                merchantId, Chain.Tron, Second, "backup", activate: false, Ct);

            added.IsSuccess.ShouldBeTrue();
            added.Value.Status.ShouldBe("Retired");
            secondWalletId = added.Value.WalletId;
        }

        await using (var context = NewContext())
        {
            var address = await new MerchantSettlementDirectory(context)
                .FindSettlementAddressAsync(merchantId, Chain.Tron, Ct);
            address.ShouldBe(SettlementAddress, "adding must not move the destination");
        }

        await using (var context = NewContext())
        {
            var retired = await NewRegistrar(context).RetireSettlementWalletAsync(merchantId, firstWalletId, Ct);
            retired.IsFailure.ShouldBeTrue("the active destination cannot be retired");
            retired.Error!.Code.ShouldBe(MerchantErrors.CannotRetireActiveSettlementWallet.Code);
        }

        await using (var context = NewContext())
            (await NewRegistrar(context).ActivateSettlementWalletAsync(merchantId, secondWalletId, Ct))
                .IsSuccess.ShouldBeTrue();

        await using (var verify = NewContext())
        {
            var merchant = await verify.Merchants
                .Include(m => m.SettlementWallets)
                .SingleAsync(m => m.Id == merchantId, Ct);

            merchant.SettlementWallets.Count.ShouldBe(2);
            merchant.SettlementWallets.Single(w => w.IsActive).Address.ShouldBe(Second);
        }

        // Swap back and forth. Found live: only one row per (merchant, chain) may be Active, and EF picks its
        // own UPDATE order, so activating a replacement in the same save as retiring the incumbent failed
        // whenever the activate went first — a 500 on some attempts and not others. The registrar now retires
        // and saves before activating, inside one transaction; repeating the swap is what would catch a
        // regression, because a single swap can pass on ordering luck.
        for (var i = 0; i < 3; i++)
        {
            await using (var context = NewContext())
                (await NewRegistrar(context).ActivateSettlementWalletAsync(merchantId, firstWalletId, Ct))
                    .IsSuccess.ShouldBeTrue($"swap {i} back to the first wallet");

            await using (var context = NewContext())
                (await NewRegistrar(context).ActivateSettlementWalletAsync(merchantId, secondWalletId, Ct))
                    .IsSuccess.ShouldBeTrue($"swap {i} to the second wallet");
        }

        await using (var verify = NewContext())
        {
            var merchant = await verify.Merchants
                .Include(m => m.SettlementWallets)
                .SingleAsync(m => m.Id == merchantId, Ct);

            merchant.SettlementWallets.Count(w => w.IsActive).ShouldBe(1, "never two destinations at once");
            merchant.SettlementWallets.Single(w => w.IsActive).Address.ShouldBe(Second);
        }
    }
}
