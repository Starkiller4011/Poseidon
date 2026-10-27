using Poseidon.Core.Modules;
using Poseidon.Tests.Shared;
using Xunit;

namespace Contracts.Tests;

public sealed class ObservationRelayTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset Measured = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static TrackObservation Observation(string target = "1", double latitude = 25) => new()
    {
        SourceId = "ignored",
        SourceTargetId = target,
        MeasuredUtc = Measured,
        Position = new GeodeticPosition(latitude, 55)
    };

    private static ObservationRelay Create(FakeTrackHost host, int capacity = 1024, int batchSize = 128)
        => new(host, "Core.Test", capacity, batchSize, new FixedTime(Now), TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task DeliversInOrder_WithStampedSourceSequenceAndReceiptTime()
    {
        var host = new FakeTrackHost();
        var relay = Create(host);
        relay.Start();
        for (var i = 1; i <= 5; i++)
            Assert.True(relay.Enqueue(Observation(i.ToString())).Accepted);

        await host.Ingestion.WaitUntilAsync(() => host.Ingestion.Observations.Count == 5, "delivery");
        var delivered = host.Ingestion.Observations;
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, delivered.Select(o => o.SourceTargetId));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, delivered.Select(o => o.Sequence));
        Assert.All(delivered, o => Assert.Equal(("Core.Test", Now), (o.SourceId, o.ReceivedUtc)));
        Assert.All(delivered, o => Assert.Equal(Measured, o.MeasuredUtc));
        await relay.StopAsync();
        Assert.Equal((5L, 5L, 0), (relay.Statistics.Enqueued, relay.Statistics.Delivered, relay.Statistics.Pending));
    }

    [Fact]
    public async Task InvalidObservations_AreRefusedWithoutConsumingASequenceNumber()
    {
        var host = new FakeTrackHost();
        var relay = Create(host);
        relay.Start();
        var refused = relay.Enqueue(Observation(latitude: 123));
        Assert.Equal((false, RejectionReason.Invalid), (refused.Accepted, refused.Reason));
        Assert.True(relay.Enqueue(Observation()).Accepted);
        await host.Ingestion.WaitUntilAsync(() => host.Ingestion.Observations.Count == 1, "delivery");
        Assert.Equal(1, host.Ingestion.Observations[0].Sequence);
        Assert.Equal(1, relay.Statistics.Invalid);
        await relay.StopAsync();
    }

    [Fact]
    public async Task FullQueue_RefusesNewObservationsWithQueueFull_AndRecoversWhenTheHostResumes()
    {
        var release = new TaskCompletionSource();
        var host = new FakeTrackHost { Block = release.Task };
        var relay = Create(host, capacity: 3, batchSize: 1);
        relay.Start();

        var results = Enumerable.Range(1, 5).Select(i => relay.Enqueue(Observation(i.ToString()))).ToList();
        Assert.Equal(new[] { true, true, true, false, false }, results.Select(r => r.Accepted));
        Assert.All(results.Skip(3), r => Assert.Equal(RejectionReason.QueueFull, r.Reason));
        Assert.Equal((3, 2L), (relay.Statistics.Pending, relay.Statistics.Overflowed));

        release.SetResult();
        await host.Ingestion.WaitUntilAsync(() => host.Ingestion.Observations.Count == 3, "delivery");
        await host.Ingestion.WaitUntilAsync(() => relay.Statistics.Pending == 0, "the queue to drain");
        Assert.True(relay.Enqueue(Observation("6")).Accepted);
        await relay.StopAsync();
    }

    [Fact]
    public async Task SourceHealth_IsDeliveredInOrderWithTheObservations()
    {
        var host = new FakeTrackHost();
        var relay = Create(host);
        relay.Start();
        relay.Enqueue(Observation("1"));
        relay.ReportHealth(SourceHealth.Disconnected, "cable");
        relay.Enqueue(Observation("2"));
        await host.Ingestion.WaitUntilAsync(() => host.Ingestion.Calls.Count == 3, "delivery");
        Assert.Equal(new[] { "obs:1", "health:Core.Test:Disconnected", "obs:2" }, host.Ingestion.Calls);
        await relay.StopAsync();
    }

    [Fact]
    public async Task TrackRejections_AreCountedByReason()
    {
        var host = new FakeTrackHost();
        host.Ingestion.Respond = o => o.SourceTargetId == "1"
            ? ObservationResult.Rejected(RejectionReason.Duplicate)
            : new ObservationResult(ObservationOutcome.Updated, "T1");
        var relay = Create(host);
        relay.Start();
        relay.Enqueue(Observation("1"));
        relay.Enqueue(Observation("2"));
        await host.Ingestion.WaitUntilAsync(() => relay.Statistics.Delivered + relay.Statistics.RejectedByTracks == 2,
            "results");
        Assert.Equal((1L, 1L), (relay.Statistics.Delivered, relay.Statistics.RejectedByTracks));
        Assert.Equal(1L, relay.Statistics.RejectedByReason[RejectionReason.Duplicate]);
        await relay.StopAsync();
    }

    [Fact]
    public async Task DeliveryFailure_DropsTheBatchReportsItAndKeepsRunning()
    {
        var host = new FakeTrackHost { Failure = new InvalidOperationException("Feature is unavailable: core.tracks.ingestion") };
        var relay = Create(host);
        relay.Start();
        relay.Enqueue(Observation("1"));
        await host.Ingestion.WaitUntilAsync(() => relay.Statistics.Dropped == 1, "the drop");
        Assert.Contains("unavailable", relay.Statistics.LastFailure);
        Assert.Equal(0, relay.Statistics.Pending);

        host.Failure = null;
        relay.Enqueue(Observation("2"));
        await host.Ingestion.WaitUntilAsync(() => host.Ingestion.Observations.Count == 1, "recovery");
        Assert.Equal("2", host.Ingestion.Observations[0].SourceTargetId);
        await relay.StopAsync();
    }

    [Fact]
    public async Task Stop_CancelsABlockedPumpPromptly_CountsDroppedObservations_AndRestartWorks()
    {
        var host = new FakeTrackHost { Block = new TaskCompletionSource().Task };
        var relay = Create(host, batchSize: 1);
        relay.Start();
        relay.Start();
        Assert.True(relay.IsRunning);
        relay.Enqueue(Observation("1"));
        relay.Enqueue(Observation("2"));

        await relay.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await relay.StopAsync();
        Assert.False(relay.IsRunning);
        Assert.Equal((2L, 0), (relay.Statistics.Dropped, relay.Statistics.Pending));
        var refused = relay.Enqueue(Observation("3"));
        Assert.Equal((false, RejectionReason.Unavailable), (refused.Accepted, refused.Reason));

        host.Block = null;
        relay.Start();
        Assert.True(relay.Enqueue(Observation("4")).Accepted);
        await host.Ingestion.WaitUntilAsync(() => host.Ingestion.Observations.Count == 1, "delivery after restart");
        Assert.Equal(3, host.Ingestion.Observations[0].Sequence);
        await relay.StopAsync();
    }

    [Fact]
    public void Enqueue_BeforeStart_IsUnavailable()
    {
        var relay = Create(new FakeTrackHost());
        var result = relay.Enqueue(Observation());
        Assert.Equal((false, RejectionReason.Unavailable), (result.Accepted, result.Reason));
    }

    [Fact]
    public async Task Pump_IsNotBlockedByTheLifecycleFlagOfTheCallerThatStartedIt()
    {
        var host = new FakeTrackHost();
        var relay = Create(host);
        FakeTrackHost.InvocationBlocked.Value = true;
        try { relay.Start(); }
        finally { FakeTrackHost.InvocationBlocked.Value = false; }

        relay.Enqueue(Observation());
        await host.Ingestion.WaitUntilAsync(() => host.Ingestion.Observations.Count == 1, "delivery");
        Assert.Null(relay.Statistics.LastFailure);
        await relay.StopAsync();
    }

    [Fact]
    public async Task Checkpoint_CarriesTheSequenceAcrossRestarts_AndRejectsUnsupportedState()
    {
        var host = new FakeTrackHost();
        var relay = Create(host);
        Assert.Null(relay.Capture());
        relay.Start();
        relay.Enqueue(Observation("1"));
        relay.Enqueue(Observation("2"));
        var state = relay.Capture();
        await relay.StopAsync();
        Assert.NotNull(state);

        var restarted = Create(host);
        restarted.Restore(state);
        restarted.Start();
        restarted.Enqueue(Observation("3"));
        await host.Ingestion.WaitUntilAsync(() => host.Ingestion.Observations.Any(o => o.SourceTargetId == "3"), "delivery");
        Assert.Equal(3, host.Ingestion.Observations.Single(o => o.SourceTargetId == "3").Sequence);
        await restarted.StopAsync();

        restarted.Restore(null);
        Assert.Throws<InvalidDataException>(() => restarted.Restore(new ModuleState(99, [])));
        Assert.Throws<InvalidDataException>(() => restarted.Restore(new ModuleState(1, [1, 2])));
    }
}
