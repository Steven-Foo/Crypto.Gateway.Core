using System.Numerics;
using Microsoft.EntityFrameworkCore.Storage;

namespace CryptoPaymentEngine.Infrastructure.Persistence.Money;

public sealed class BigIntegerTypeMappingPlugin : IRelationalTypeMappingSourcePlugin
{
    public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo) =>
        mappingInfo.ClrType == typeof(BigInteger) ? BigIntegerTypeMapping.Default : null;
}
