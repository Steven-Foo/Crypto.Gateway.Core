namespace CryptoPaymentEngine.Infrastructure.IntegrationTests;

/// <summary>
/// Integration tests run against a real SQL Server. Locally that's LocalDB; CI can point
/// <c>CPE_TEST_SQL</c> at a service container or hosted instance. The money mapping cannot be verified
/// against an in-memory provider — <c>decimal(38,0)</c> semantics only exist in SQL Server.
///
/// <para>This local-by-default, overridable-by-environment convention is the house pattern for every
/// integration test in the solution, Mongo included (see <c>MongoTestDatabase</c> / <c>CPE_TEST_MONGO</c>).
/// Nothing spins up its own container: a test that needs a daemon the developer does not run silently
/// skips, and a test that skips on the machine of the person changing the code is close to no test.</para>
/// </summary>
public static class SqlServerTestDatabase
{
    public static string ConnectionString(string databaseName) =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", databaseName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True";
}
