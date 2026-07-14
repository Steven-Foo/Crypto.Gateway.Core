namespace CryptoPaymentEngine.Infrastructure.Persistence.Money;

/// <summary>
/// The SQL Server storage type for base-unit amounts. The *bounds* themselves live in
/// <see cref="SharedKernel.MoneyLimits"/> so the Domain can enforce them without referencing
/// Infrastructure.
/// </summary>
public static class MoneySqlTypes
{
    /// <summary>Scale 0 — integer base units, never a scaled display value.</summary>
    public const string StoreType = "decimal(38,0)";
}
