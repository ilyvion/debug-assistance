using DebugAssistance.Capture;
using RimTestRedux;

namespace DebugAssistance.Tests.Capture;

[TestSuite]
internal static class RawCaptureRingBufferTests
{
    private static RawCapture MakeCapture(
        string errorTypeName = "System.Exception",
        string message = "message",
        DateTime? timestamp = null
    ) => new(errorTypeName, message, [], "", timestamp ?? DateTime.UtcNow);

    [Test]
    public static void StoringACaptureMakesItAppearInTheSnapshot()
    {
        var buffer = new RawCaptureRingBuffer();
        var capture = MakeCapture();

        buffer.Store(capture);

        Assert.ThatCollection(buffer.Snapshot()).Does.Contain(capture);
    }

    [Test]
    public static void StoringTheSameKeyTwiceOverwritesInPlaceWithoutGrowingTheBuffer()
    {
        var buffer = new RawCaptureRingBuffer();
        var first = MakeCapture(timestamp: new DateTime(2026, 1, 1));
        var second = MakeCapture(timestamp: new DateTime(2026, 1, 2));

        buffer.Store(first);
        buffer.Store(second);

        var snapshot = buffer.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        Assert.That(snapshot[0].Timestamp).Is.EqualTo(new DateTime(2026, 1, 2));
    }

    [Test]
    public static void DifferentKeysProduceSeparateEntries()
    {
        var buffer = new RawCaptureRingBuffer();
        buffer.Store(MakeCapture(message: "first"));
        buffer.Store(MakeCapture(message: "second"));

        Assert.ThatCollection(buffer.Snapshot()).Has.Count(2);
    }

    [Test]
    public static void FifoEvictionRemovesOldestEntryOnceOverCapacity()
    {
        var buffer = new RawCaptureRingBuffer(capacity: 2);
        var oldest = MakeCapture(message: "oldest");
        var middle = MakeCapture(message: "middle");
        var newest = MakeCapture(message: "newest");

        buffer.Store(oldest);
        buffer.Store(middle);
        buffer.Store(newest);

        var snapshot = buffer.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(2);
        Assert.ThatCollection(snapshot).Does.Not.Contain(oldest);
        Assert.ThatCollection(snapshot).Does.Contain(middle);
        Assert.ThatCollection(snapshot).Does.Contain(newest);
    }

    [Test]
    public static void OverwritingAnEntryDoesNotResetItsEvictionOrder()
    {
        var buffer = new RawCaptureRingBuffer(capacity: 2);
        var first = MakeCapture(message: "first", timestamp: new DateTime(2026, 1, 1));
        buffer.Store(first);
        buffer.Store(MakeCapture(message: "second"));

        // Re-storing "first" should not move it to the back of the FIFO order.
        buffer.Store(MakeCapture(message: "first", timestamp: new DateTime(2026, 1, 2)));
        buffer.Store(MakeCapture(message: "third"));

        var snapshot = buffer.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(2);
        Assert.ThatCollection(snapshot.Select(c => c.Message)).Does.Not.Contain("first");
    }

    [Test]
    public static void SnapshotReturnsAnIndependentCopy()
    {
        var buffer = new RawCaptureRingBuffer();
        buffer.Store(MakeCapture());
        var snapshot = buffer.Snapshot();

        buffer.Store(MakeCapture(message: "second"));

        Assert.ThatCollection(snapshot).Has.Count(1);
    }
}
