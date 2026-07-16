using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using MongoDB.Driver;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Mongo;

/// <summary>
/// The latest-observed resource snapshot per wallet, in MongoDB — the codebase's first document store use.
/// A derived read model, never money truth (§2). Upsert-by-wallet: one current document, overwritten each poll.
/// </summary>
public sealed class MongoWalletResourceStore : IWalletResourceStore
{
    public const string CollectionName = "WalletResource";

    private readonly IMongoCollection<WalletResourceDocument> _collection;

    public MongoWalletResourceStore(IMongoDatabase database) =>
        _collection = database.GetCollection<WalletResourceDocument>(CollectionName);

    public Task UpsertAsync(WalletResourceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var document = ResourceDocumentMapper.ToCurrent(snapshot);
        return _collection.ReplaceOneAsync(
            d => d.Id == document.Id,
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<WalletResourceSnapshot?> GetAsync(Guid walletId, CancellationToken cancellationToken = default)
    {
        var id = walletId.ToString();
        var document = await _collection.Find(d => d.Id == id).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ResourceDocumentMapper.FromCurrent(document);
    }
}
