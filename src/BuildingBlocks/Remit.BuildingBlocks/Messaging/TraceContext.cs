using System.Diagnostics;
using System.Text;

namespace Remit.BuildingBlocks.Messaging;

/// <summary>
/// Carries W3C trace context across the broker so a consumer's span is a child of the
/// producer's, and one trace shows a deposit from HTTP request to ledger posting (ADR-0007).
/// </summary>
public static class TraceContext
{
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";

    public static void Inject(Activity? activity, IDictionary<string, object?> headers)
    {
        if (activity is null)
        {
            return;
        }

        headers[TraceParentHeader] = Encoding.UTF8.GetBytes(activity.Id ?? string.Empty);
        if (!string.IsNullOrEmpty(activity.TraceStateString))
        {
            headers[TraceStateHeader] = Encoding.UTF8.GetBytes(activity.TraceStateString);
        }
    }

    public static ActivityContext Extract(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue(TraceParentHeader, out var raw))
        {
            return default;
        }

        var traceParent = raw switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string s => s,
            _ => null,
        };

        string? traceState = null;
        if (headers.TryGetValue(TraceStateHeader, out var rawState) && rawState is byte[] stateBytes)
        {
            traceState = Encoding.UTF8.GetString(stateBytes);
        }

        return traceParent is not null && ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var context)
            ? context
            : default;
    }
}
