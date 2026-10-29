using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace PayFlow.ServiceDefaults.UnitTests;

public class OutboxPollFilterProcessorTests
{
    private const string PollSql =
        "SELECT o.\"Id\", o.\"Type\" FROM (SELECT * FROM \"OutboxMessages\" WHERE \"ProcessedAt\" IS NULL " +
        "ORDER BY \"OccurredAt\" LIMIT 20 FOR UPDATE SKIP LOCKED) AS o";

    private static List<string> ExportedQueries(params string[] statements)
    {
        var exported = new List<Activity>();
        using var source = new ActivitySource("Npgsql");
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource("Npgsql")
            .AddProcessor(new OutboxPollFilterProcessor())   // registered before the exporter, as in production
            .AddInMemoryExporter(exported)
            .Build();

        foreach (var sql in statements)
        {
            using var activity = source.StartActivity("postgresql", ActivityKind.Client);
            activity?.SetTag("db.query.text", sql);
        }

        provider.ForceFlush();
        return exported.Select(a => (string)a.GetTagItem("db.query.text")!).ToList();
    }

    [Fact]
    public void OutboxPoll_IsNotExported()
    {
        Assert.Empty(ExportedQueries(PollSql, PollSql, PollSql));
    }

    [Fact]
    public void OtherQueries_AreStillExported()
    {
        var queries = ExportedQueries(
            PollSql,
            "UPDATE \"OutboxMessages\" SET \"ProcessedAt\" = $1 WHERE \"Id\" = $2",   // marks a row as sent: real work
            "SELECT o.\"Id\" FROM \"Orders\" AS o WHERE o.\"Id\" = $1");

        Assert.Equal(2, queries.Count);
        Assert.DoesNotContain(queries, q => q.Contains("SKIP LOCKED", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SELECT 1", false)]
    [InlineData("SELECT * FROM \"OutboxMessages\" WHERE \"Id\" = $1", false)]    // reads the table but does not lock: not the poll
    [InlineData("SELECT * FROM \"Orders\" FOR UPDATE SKIP LOCKED", false)]       // locks, but not the outbox
    [InlineData(PollSql, true)]
    public void IsOutboxPoll_RecognisesOnlyThePollStatement(string sql, bool expected) =>
        Assert.Equal(expected, OutboxPollFilterProcessor.IsOutboxPoll(sql));
}
