using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using MongoDB.Driver;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Infrastructure.Mongo;

/// <summary>
/// The latest reconciliation snapshot per (chain, asset), in MongoDB — a derived read model, never money
/// truth (§2). Upsert-by-key: one current document per (chain, asset), overwritten each pass.
/// </summary>
public sealed class MongoReconciliationStore : IReconciliationStore
{
    public const string CollectionName = "Reconciliation";

    private readonly IMongoCollection<ReconciliationDocument> _collection;

    public MongoReconciliationStore(IMongoDatabase database) =>
        _collection = database.GetCollection<ReconciliationDocument>(CollectionName);

    public Task UpsertAsync(ReconciliationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var document = ReconciliationDocumentMapper.ToCurrent(snapshot);
        return _collection.ReplaceOneAsync(
            d => d.Id == document.Id,
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<ReconciliationSnapshot?> GetAsync(Chain chain, Guid assetId, CancellationToken cancellationToken = default)
    {
        var id = ReconciliationDocumentMapper.KeyOf(chain, assetId);
        var document = await _collection.Find(d => d.Id == id).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ReconciliationDocumentMapper.FromCurrent(document);
    }

    public async Task<IReadOnlyList<ReconciliationSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var documents = await _collection.Find(FilterDefinition<ReconciliationDocument>.Empty).ToListAsync(cancellationToken);
        return documents.Select(ReconciliationDocumentMapper.FromCurrent).ToList();
    }
}

/// <summary>Append-only reconciliation history (MongoDB) — the custody-drift audit trail. Never money truth (§2).</summary>
public sealed class MongoReconciliationHistoryStore : IReconciliationHistoryStore
{
    public const string CollectionName = "ReconciliationHistory";

    private readonly IMongoCollection<ReconciliationHistoryDocument> _collection;

    public MongoReconciliationHistoryStore(IMongoDatabase database) =>
        _collection = database.GetCollection<ReconciliationHistoryDocument>(CollectionName);

    public Task AppendAsync(ReconciliationSnapshot snapshot, CancellationToken cancellationToken = default) =>
        _collection.InsertOneAsync(ReconciliationDocumentMapper.ToHistory(snapshot), cancellationToken: cancellationToken);
}
