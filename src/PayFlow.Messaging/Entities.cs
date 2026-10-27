using Microsoft.EntityFrameworkCore;

namespace PayFlow.Messaging;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }                 // == EventId, used as Service Bus MessageId
    public required string Type { get; init; }    // e.g. "OrderCreated", used as message Subject
    public required string Payload { get; init; }
    public required string CorrelationId { get; init; }
    public string? TraceParent { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Inbox record: proves a given message was already handled by a given consumer.</summary>
public sealed class ProcessedMessage
{
    public required string MessageId { get; init; }
    public required string Consumer { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
}

public static class MessagingModelExtensions
{
    /// <summary>Adds the OutboxMessages and ProcessedMessages tables to a service's DbContext.</summary>
    public static ModelBuilder AddMessagingTables(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.Property(x => x.TraceParent).HasMaxLength(100);
            // Partial index: the publisher only ever scans unprocessed rows.
            e.HasIndex(x => x.OccurredAt).HasFilter("\"ProcessedAt\" IS NULL");
        });

        modelBuilder.Entity<ProcessedMessage>(e =>
        {
            e.ToTable("ProcessedMessages");
            e.HasKey(x => new { x.MessageId, x.Consumer });
            e.Property(x => x.MessageId).HasMaxLength(100);
            e.Property(x => x.Consumer).HasMaxLength(100);
        });

        return modelBuilder;
    }
}
