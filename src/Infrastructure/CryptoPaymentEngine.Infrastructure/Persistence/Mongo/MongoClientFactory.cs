using MongoDB.Driver;

namespace CryptoPaymentEngine.Infrastructure.Persistence.Mongo;

/// <summary>
/// Builds the one shared <see cref="IMongoClient"/>. Everything kept in MongoDB is derived observability, never
/// money truth (§2), so a MongoDB outage must cost a page a few seconds, not half a minute: the driver's own
/// default waits <b>30 seconds</b> for a reachable server before failing each call, which made the back-office
/// dashboard take over 40 seconds whenever MongoDB was down.
///
/// <para>Here the wait is <see cref="DefaultServerSelectionTimeout"/> (5 s), and a connection attempt is cut off
/// at the same. A deployment that wants longer (for example to ride out a replica-set election without a single
/// failed snapshot write) sets <c>serverSelectionTimeoutMS</c> / <c>connectTimeoutMS</c> in the connection
/// string, which always wins.</para>
/// </summary>
public static class MongoClientFactory
{
    public static readonly TimeSpan DefaultServerSelectionTimeout = TimeSpan.FromSeconds(5);

    public static IMongoClient Create(string connectionString) => new MongoClient(Settings(connectionString));

    public static MongoClientSettings Settings(string connectionString)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);

        if (!connectionString.Contains("serverSelectionTimeoutMS", StringComparison.OrdinalIgnoreCase))
            settings.ServerSelectionTimeout = DefaultServerSelectionTimeout;
        if (!connectionString.Contains("connectTimeoutMS", StringComparison.OrdinalIgnoreCase))
            settings.ConnectTimeout = DefaultServerSelectionTimeout;

        return settings;
    }
}
