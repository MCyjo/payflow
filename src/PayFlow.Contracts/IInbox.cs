namespace PayFlow.Contracts;

/// <summary>Records handled message ids so a redelivered message is not processed twice.</summary>
public interface IInbox
{
    Task<bool> HasProcessedAsync(string messageId, CancellationToken ct);

    /// <summary>Stages the record; it is persisted by the same SaveChanges as the business change.</summary>
    void MarkProcessed(string messageId);
}
