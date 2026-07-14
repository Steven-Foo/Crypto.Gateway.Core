namespace CryptoPaymentEngine.Infrastructure.IntegrationTests;

/// <summary>
/// Integration tests run against a real SQL Server. Locally that's LocalDB; CI can point
/// <c>CPE_TEST_SQL</c> at a Testcontainers/hosted instance. The money mapping cannot be verified
/// against an in-memory provider — <c>decimal(38,0)</c> semantics only exist in SQL Server.
/// </summary>
public static class SqlServerTestDatabase
{
    public static string ConnectionString(string databaseName) =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", databaseName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True";
}
