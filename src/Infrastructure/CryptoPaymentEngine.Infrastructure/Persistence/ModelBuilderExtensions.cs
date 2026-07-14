using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Infrastructure.Persistence;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Append-heavy tables only. SQL Server orders <c>uniqueidentifier</c> by a byte order that does
    /// NOT match Guid-v7 time order, so a clustered GUID PK fragments on every insert. Here the GUID
    /// PK is non-clustered and a monotonic <c>bigint IDENTITY</c> carries the clustered index.
    /// Low-write tables should keep the default clustered GUID PK — don't pay for a column you don't need.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasSeqClusteredIndex<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        string seqPropertyName = "Seq")
        where TEntity : class
    {
        builder.Property<long>(seqPropertyName).ValueGeneratedOnAdd();
        builder.HasKey(GetKeyName(builder)).IsClustered(false);
        builder.HasIndex(seqPropertyName).IsClustered().IsUnique();
        return builder;
    }

    private static string GetKeyName<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        var key = builder.Metadata.FindPrimaryKey()
            ?? throw new InvalidOperationException(
                $"{typeof(TEntity).Name} must declare its primary key before calling {nameof(HasSeqClusteredIndex)}.");

        return key.Properties.Single().Name;
    }
}
