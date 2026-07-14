namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// Non-generic so persistence-layer code (e.g. an EF Core SaveChanges interceptor) can find
/// every entity with pending domain events without knowing each entity's <c>TId</c>.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}
