using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using MongoDB.Driver;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Mongo;

/// <summary>Append-only time series of resource observations in MongoDB — the Phase 5c forecasting feed (§2).</summary>
public sealed class MongoResourceHistoryStore : IResourceHistoryStore
{
    public const string CollectionName = "ResourceHistory";

    private readonly IMongoCollection<ResourceHistoryDocument> _collection;

    public MongoResourceHistoryStore(IMongoDatabase database) =>
        _collection = database.GetCollection<ResourceHistoryDocument>(CollectionName);

    public Task AppendAsync(WalletResourceSnapshot snapshot, CancellationToken cancellationToken = default) =>
        _collection.InsertOneAsync(ResourceDocumentMapper.ToHistory(snapshot), cancellationToken: cancellationToken);
}
