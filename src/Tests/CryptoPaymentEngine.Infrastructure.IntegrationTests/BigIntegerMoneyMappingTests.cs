using System.Numerics;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Infrastructure.IntegrationTests;

/// <summary>
/// The guardrail for money storage. A plain ValueConverter&lt;BigInteger, decimal&gt; silently
/// overflows past ~28 digits, and EF cannot map SqlDecimal directly — so exact 38-digit storage
/// depends on <see cref="BigIntegerTypeMapping"/>. If an EF upgrade breaks that, these tests fail
/// loudly instead of corrupting balances.
/// </summary>
public sealed class BigIntegerMoneyMappingTests : IAsyncLifetime
{
    private const string DbName = "CpeMoneyMappingTests";

    private static MoneyTestDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MoneyTestDbContext>()
            .UseSqlServer(SqlServerTestDatabase.ConnectionString(DbName))
            .UseBigIntegerMoney()
            .Options;

        return new MoneyTestDbContext(options);
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

    public static TheoryData<int> Digits => [1, 18, 27, 28, 29, 34, 38];

    [Theory]
    [MemberData(nameof(Digits))]
    public async Task Base_unit_amount_round_trips_exactly(int digits)
    {
        var expected = BigInteger.Parse(new string('9', digits));
        var id = Guid.CreateVersion7();

        await using (var context = NewContext())
        {
            context.MoneyRows.Add(new MoneyRow { Id = id, Amount = expected });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = NewContext())
        {
            var actual = (await context.MoneyRows.SingleAsync(r => r.Id == id, TestContext.Current.CancellationToken)).Amount;
            actual.ShouldBe(expected);
            actual.ToString().Length.ShouldBe(digits);
        }
    }

    [Fact]
    public async Task Real_world_amounts_round_trip_exactly()
    {
        // 1 wei, total ETH supply in wei (27 digits), and a Shiba-scale 18-decimal supply (33 digits)
        // — the last of which overflows System.Decimal and would break a naive converter.
        var amounts = new[]
        {
            BigInteger.One,
            BigInteger.Parse("120000000000000000000000000"),
            BigInteger.Parse("589000000000000000000000000000000"),
        };

        var ids = amounts.Select(_ => Guid.CreateVersion7()).ToArray();

        await using (var context = NewContext())
        {
            for (var i = 0; i < amounts.Length; i++)
                context.MoneyRows.Add(new MoneyRow { Id = ids[i], Amount = amounts[i] });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var readContext = NewContext())
        {
            for (var i = 0; i < amounts.Length; i++)
                (await readContext.MoneyRows.SingleAsync(r => r.Id == ids[i], TestContext.Current.CancellationToken))
                    .Amount.ShouldBe(amounts[i]);
        }
    }

    [Fact]
    public async Task Amount_exceeding_38_digits_is_rejected_not_truncated()
    {
        var tooLarge = BigInteger.Pow(10, 38); // 39 digits

        await using var context = NewContext();
        context.MoneyRows.Add(new MoneyRow { Id = Guid.CreateVersion7(), Amount = tooLarge });

        var exception = await Should.ThrowAsync<Exception>(() => context.SaveChangesAsync());

        // Surfaces from MoneyLimits.EnsureStorable inside the type mapping.
        var root = exception;
        while (root.InnerException is not null) root = root.InnerException;
        root.ShouldBeOfType<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MoneyLimits_MaxValue_is_38_nines()
    {
        MoneyLimits.MaxValue.ToString().ShouldBe(new string('9', 38));
        MoneyLimits.IsStorable(MoneyLimits.MaxValue).ShouldBeTrue();
        MoneyLimits.IsStorable(MoneyLimits.MaxValue + BigInteger.One).ShouldBeFalse();
        MoneyLimits.IsStorable(BigInteger.MinusOne).ShouldBeFalse();
    }

    [Fact]
    public void Negative_base_units_are_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => { MoneyLimits.EnsureStorable(BigInteger.MinusOne, "amount"); });
    }
}

public sealed class MoneyRow
{
    public Guid Id { get; set; }
    public BigInteger Amount { get; set; }
}

public sealed class MoneyTestDbContext(DbContextOptions<MoneyTestDbContext> options) : DbContext(options)
{
    public DbSet<MoneyRow> MoneyRows => Set<MoneyRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<MoneyRow>(entity =>
        {
            entity.ToTable("MoneyRow");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Amount).HasColumnType(MoneySqlTypes.StoreType);
        });
}
