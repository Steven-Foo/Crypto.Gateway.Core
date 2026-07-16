using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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

    private static MerchantRegistrar NewRegistrar(MerchantDbContext context) =>
        new(new MerchantRepository(context), new ApiCredentialGenerator(), NewHasher(), NewCipher(), TimeProvider.System);

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
            var result = await NewRegistrar(context).RegisterAsync("ACME-1", "Acme Payments", "https://acme.test/hook", Ct);
            result.IsSuccess.ShouldBeTrue();
            registration = result.Value;
        }

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants
                .Include(m => m.Configuration)
                .Include(m => m.Credentials)
                .SingleAsync(m => m.Id == registration.MerchantId, Ct);

            merchant.MerchantCode.ShouldBe("ACME-1");
            merchant.Status.ShouldBe(MerchantStatus.Pending);
            merchant.Configuration.ShouldNotBeNull();
            merchant.Credentials.Count.ShouldBe(1);
            merchant.Credentials[0].ApiKey.ShouldBe(registration.ApiKey);
            merchant.Credentials[0].HashVersion.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Activating_a_registered_merchant_makes_it_transactable()
    {
        Guid merchantId;
        await using (var context = NewContext())
            merchantId = (await NewRegistrar(context).RegisterAsync("ACTIVATE-1", "Acme", null, Ct)).Value.MerchantId;

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
            registration = (await NewRegistrar(context).RegisterAsync("SECRET-1", "Acme", null, Ct)).Value;
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
        await using (var context = NewContext())
        {
            (await NewRegistrar(context).RegisterAsync("DUPE", "First", null, Ct)).IsSuccess.ShouldBeTrue();
        }

        // Bypass the application's friendly pre-check to prove the UNIQUE index is the real arbiter.
        await using (var context = NewContext())
        {
            var second = MerchantEntity.Create("DUPE", "Second", null).Value;
            context.Merchants.Add(second);
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task Registrar_returns_a_conflict_rather_than_throwing_on_duplicate_code()
    {
        await using (var context = NewContext())
        {
            (await NewRegistrar(context).RegisterAsync("TAKEN", "First", null, Ct)).IsSuccess.ShouldBeTrue();
        }

        await using (var context = NewContext())
        {
            var result = await NewRegistrar(context).RegisterAsync("taken", "Second", null, Ct);

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
            var registration = (await NewRegistrar(context).RegisterAsync("KEYDUP", "Acme", null, Ct)).Value;
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
            merchantId = (await NewRegistrar(context).RegisterAsync("MONEY-1", "Acme", null, Ct)).Value.MerchantId;
        }

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants.Include(m => m.AssetPolicies).SingleAsync(m => m.Id == merchantId, Ct);
            merchant.SetAssetPolicy(assetId, huge, BigInteger.Zero, null, new BigInteger(1_000_000), DateTimeOffset.UtcNow)
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
            merchantId = (await NewRegistrar(context).RegisterAsync("POLICY-1", "Acme", null, Ct)).Value.MerchantId;
        }

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants.Include(m => m.AssetPolicies).SingleAsync(m => m.Id == merchantId, Ct);
            merchant.SetAssetPolicy(assetId, 1, 0, null, 0, DateTimeOffset.UtcNow);
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
            merchantId = (await NewRegistrar(context).RegisterAsync("CHECK-1", "Acme", null, Ct)).Value.MerchantId;
        }

        await using (var context = NewContext())
        {
            var merchant = await context.Merchants.Include(m => m.AssetPolicies).SingleAsync(m => m.Id == merchantId, Ct);
            merchant.SetAssetPolicy(assetId, 0, 100, 1000, 0, DateTimeOffset.UtcNow);
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
            merchantId = (await NewRegistrar(context).RegisterAsync("CHECK-2", "Acme", null, Ct)).Value.MerchantId;
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

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_updates_to_the_same_merchant_are_caught_by_rowversion()
    {
        Guid merchantId;
        await using (var context = NewContext())
        {
            merchantId = (await NewRegistrar(context).RegisterAsync("CONC-1", "Acme", null, Ct)).Value.MerchantId;
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
            registration = (await NewRegistrar(context).RegisterAsync("AUTH-1", "Acme", null, Ct)).Value;
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
    public async Task A_pending_merchant_cannot_transact_even_with_valid_credentials()
    {
        MerchantRegistrationResult registration;
        await using (var context = NewContext())
        {
            registration = (await NewRegistrar(context).RegisterAsync("AUTH-2", "Acme", null, Ct)).Value;
        }

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
            registration = (await NewRegistrar(context).RegisterAsync("AUTH-3", "Acme", null, Ct)).Value;
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
            registration = (await NewRegistrar(context).RegisterAsync("AUTH-4", "Acme", null, Ct)).Value;
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
            merchantId = (await NewRegistrar(context).RegisterAsync("DIR-1", "Acme Payments", "https://acme.test/h", Ct)).Value.MerchantId;
            var merchant = await context.Merchants.SingleAsync(m => m.Id == merchantId, Ct);
            merchant.Activate(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            var summary = await new MerchantDirectory(context).FindByCodeAsync("dir-1", Ct);

            summary.ShouldNotBeNull();
            summary.MerchantId.ShouldBe(merchantId);
            summary.MerchantCode.ShouldBe("DIR-1");
            summary.CanTransact.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Merchant_directory_returns_null_for_an_unknown_merchant()
    {
        await using var context = NewContext();
        (await new MerchantDirectory(context).FindByIdAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();
    }
}
