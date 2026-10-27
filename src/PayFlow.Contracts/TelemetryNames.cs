namespace PayFlow.Contracts;

/// <summary>Names shared by the code that emits telemetry and the code that subscribes to it.</summary>
public static class TelemetryNames
{
    public const string MessagingSource = "PayFlow.Messaging";
    public const string MessagingMeter = "PayFlow.Messaging";

    /// <summary>Service Bus application property carrying the W3C trace context.</summary>
    public const string TraceParentProperty = "traceparent";

    /// <summary>Property the Azure Functions Service Bus extension reads to parent its invocation span.</summary>
    public const string DiagnosticIdProperty = "Diagnostic-Id";
}
