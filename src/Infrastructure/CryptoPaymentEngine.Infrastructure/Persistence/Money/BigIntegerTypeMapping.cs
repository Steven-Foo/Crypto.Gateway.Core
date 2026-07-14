using System.Data.Common;
using System.Data.SqlTypes;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Microsoft.Data.SqlClient;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore.Storage;

namespace CryptoPaymentEngine.Infrastructure.Persistence.Money;

/// <summary>
/// Maps <see cref="BigInteger"/> base units to a <c>decimal(38,0)</c> column with full 38-digit
/// fidelity, by round-tripping through <see cref="SqlDecimal"/> at the ADO layer.
///
/// A plain <c>ValueConverter&lt;BigInteger, decimal&gt;</c> cannot be used: <see cref="decimal"/>
/// holds only ~28 digits (max 7.92e28) and silently overflows on larger base-unit amounts.
/// A <c>ValueConverter&lt;BigInteger, SqlDecimal&gt;</c> cannot be used either: EF has no store
/// mapping for <see cref="SqlDecimal"/>. Hence this custom mapping.
///
/// Verified by <c>BigIntegerMoneyMappingTests</c> — exact at 27, 34, and 38 digits. Those tests
/// are the guard: if an EF upgrade changes this behaviour, they fail rather than corrupting money.
/// </summary>
public sealed class BigIntegerTypeMapping : RelationalTypeMapping
{
    public static readonly BigIntegerTypeMapping Default = new();

    private static readonly MethodInfo GetSqlDecimalMethod =
        typeof(SqlDataReader).GetMethod(nameof(SqlDataReader.GetSqlDecimal), [typeof(int)])!;

    private static readonly MethodInfo FromSqlDecimalMethod =
        typeof(BigIntegerTypeMapping).GetMethod(nameof(FromSqlDecimal), BindingFlags.Public | BindingFlags.Static)!;

    public BigIntegerTypeMapping()
        : base(MoneySqlTypes.StoreType, typeof(BigInteger), System.Data.DbType.Decimal)
    {
    }

    private BigIntegerTypeMapping(RelationalTypeMappingParameters parameters) : base(parameters)
    {
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BigIntegerTypeMapping(parameters);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        var sqlParameter = (SqlParameter)parameter;
        sqlParameter.SqlDbType = System.Data.SqlDbType.Decimal;
        sqlParameter.Precision = MoneyLimits.MaxPrecision;
        sqlParameter.Scale = 0;

        if (sqlParameter.Value is BigInteger value)
        {
            sqlParameter.Value = SqlDecimal.Parse(MoneyLimits.EnsureStorable(value, nameof(value)).ToString());
        }
    }

    public override MethodInfo GetDataReaderMethod() => GetSqlDecimalMethod;

    public override Expression CustomizeDataReaderExpression(Expression expression) =>
        Expression.Call(FromSqlDecimalMethod, expression);

    public static BigInteger FromSqlDecimal(SqlDecimal value) => BigInteger.Parse(value.ToString());

    protected override string GenerateNonNullSqlLiteral(object value) => ((BigInteger)value).ToString();
}
