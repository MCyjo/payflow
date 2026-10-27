namespace PayFlow.Contracts;

/// <summary>Stages an event in the outbox; it is persisted by the same SaveChanges as the aggregate.</summary>
public interface IOutbox
{
    void Enqueue(IDomainEvent domainEvent, string correlationId);
}
