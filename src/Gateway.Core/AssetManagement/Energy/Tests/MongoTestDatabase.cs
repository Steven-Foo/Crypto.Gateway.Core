using MongoDB.Driver;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

/// <summary>
/// Mongo integration tests run against a real MongoDB — locally the native service on
/// <c>localhost:27017</c>, with <c>CPE_TEST_MONGO</c> pointing CI at a service container or hosted
/// instance. This is deliberately the same shape as <c>SqlServerTestDatabase</c>'s <c>CPE_TEST_SQL</c>
/// convention that 31 test files already follow: local by default, overridable by environment.
///
/// <para>It replaced a Testcontainers-based fixture. Testcontainers needs a Docker daemon, which meant
/// these were the only tests in the solution that silently skipped on a developer machine running Mongo
/// natively — and a test that skips on the machine of the person changing the code is close to no test
/// at all.</para>
/// </summary>
public static class MongoTestDatabase
{
    /// <summary>Never the dev database. Note MongoDB forbids two database names differing only by case, so
    /// this must not be a case-variant of <c>CryptoPaymentEngine</c> either.</summary>
    public const string DatabaseName = "cpe_test_energy";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_MONGO") is { Length: > 0 } configured
            ? configured
            : "mongodb://localhost:27017";

    /// <summary>
    /// A client that gives up quickly when nothing is listening. The driver's default server-selection
    /// timeout is 30s, which would turn "no Mongo on this machine" into a half-minute stall per test class
    /// before the skip.
    /// </summary>
    public static MongoClient CreateClient()
    {
        var settings = MongoClientSettings.FromConnectionString(ConnectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ConnectTimeout = TimeSpan.FromSeconds(3);
        return new MongoClient(settings);
    }

    /// <summary>Pings the server so an unreachable Mongo becomes a skip, not a failure.</summary>
    public static async Task<bool> IsAvailableAsync(MongoClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetDatabase("admin")
                .RunCommandAsync<MongoDB.Bson.BsonDocument>("{ ping: 1 }", cancellationToken: cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
