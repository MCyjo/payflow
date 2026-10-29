using System.Diagnostics;
using OpenTelemetry;

namespace PayFlow.ServiceDefaults;

/// <summary>
/// The outbox relay asks the database for pending rows about once a second per replica, and each poll would become a
/// span plus an event, around 4000 telemetry records every quarter of an hour per service while nothing happens.
/// That noise buries real traces and eats the daily ingestion cap. Spans of the poll query are marked as not recorded
/// so no exporter sends them; the publish span and the UPDATE that marks a row as sent are kept.
/// </summary>
/// <remarks>Must be registered before the exporters, because exporters skip activities that are not recorded.</remarks>
public sealed class OutboxPollFilterProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.GetTagItem("db.query.text") is string sql && IsOutboxPoll(sql))
        {
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }

    // The poll is the only statement that both reads the outbox table and locks rows with SKIP LOCKED.
    internal static bool IsOutboxPoll(string sql) =>
        sql.Contains("\"OutboxMessages\"", StringComparison.Ordinal)
        && sql.Contains("SKIP LOCKED", StringComparison.OrdinalIgnoreCase);
}
