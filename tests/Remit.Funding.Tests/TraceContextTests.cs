using System.Diagnostics;
using Remit.BuildingBlocks.Messaging;

namespace Remit.Funding.Tests;

public class TraceContextTests
{
    [Fact]
    public void Trace_context_survives_the_round_trip_through_message_headers()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource("Remit.Test");
        using var producer = source.StartActivity("produce", ActivityKind.Producer);
        Assert.NotNull(producer);
        producer.TraceStateString = "vendor=remit";

        var headers = new Dictionary<string, object?>();
        TraceContext.Inject(producer, headers);

        var extracted = TraceContext.Extract(headers);

        Assert.Equal(producer.TraceId, extracted.TraceId);
        Assert.Equal(producer.SpanId, extracted.SpanId);
        Assert.Equal("vendor=remit", extracted.TraceState);
        Assert.True(extracted.IsRemote);

        using var consumer = source.StartActivity("consume", ActivityKind.Consumer, extracted);
        Assert.NotNull(consumer);
        Assert.Equal(producer.TraceId, consumer.TraceId);
        Assert.Equal(producer.SpanId, consumer.ParentSpanId);
    }

    [Fact]
    public void Missing_or_garbage_headers_yield_no_context()
    {
        Assert.Equal(default, TraceContext.Extract(null));
        Assert.Equal(default, TraceContext.Extract(new Dictionary<string, object?>()));
        Assert.Equal(default, TraceContext.Extract(new Dictionary<string, object?> { ["traceparent"] = "not-a-traceparent"u8.ToArray() }));
    }
}
