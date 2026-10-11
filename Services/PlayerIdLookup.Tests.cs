using System.Threading.Tasks;
using Coflnet.Sky.Core;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class PlayerIdRecorderTests
{
    private static readonly DateTime T = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string OptedOut = "f3c19fb53ea940f3921e90faab8e2b30";
    private const string A = "11111111111111111111111111111111";

    [SetUp]
    public void SetUp() => PlayerOptOut.ResetMemory();
    [TearDown]
    public void TearDown() => PlayerOptOut.ResetMemory();

    private static long Id(string uuid) => TrackerService.HashId(uuid);

    [Test]
    public void SelectsOnceWithinWindowAndAgainAfter()
    {
        var recorder = new PlayerIdRecorder();
        recorder.Select([A], Id, T).Should().ContainSingle().Which.Item2.Should().Be(Guid.Parse(A));
        recorder.Select([A], Id, T.AddHours(23)).Should().BeEmpty();
        recorder.Select([A], Id, T.AddHours(25)).Should().ContainSingle();
    }

    [Test]
    public void DuplicatesInOneBatchAreSelectedOnce()
        => new PlayerIdRecorder().Select([A, A, Guid.Parse(A).ToString()], Id, T).Should().ContainSingle();

    [Test]
    public void OptedOutAndInvalidAreNeverSelected()
    {
        var selected = new PlayerIdRecorder().Select([OptedOut, "garbage", null, "", Guid.Empty.ToString("N")], Id, T);
        selected.Should().BeEmpty();
    }

    [Test]
    public void MemoryIsCapped()
    {
        var recorder = new PlayerIdRecorder();
        var max = PlayerIdRecorder.MaxEntries;
        recorder.Select(Enumerable.Range(1, max + 50).Select(i => new Guid(i, 0, 0, new byte[8]).ToString("N")), Id, T);
        recorder.Count.Should().BeLessThanOrEqualTo(max);
        // the first 'max' entries were remembered before the reset, later ones are selectable again, nothing throws
        recorder.Select([A], Id, T).Should().ContainSingle();
    }

    [Test]
    public void ThrowingHashFunctionSkipsEntry()
        => new PlayerIdRecorder().Select([A], _ => throw new FormatException(), T).Should().BeEmpty();

    [Test]
    public async Task StorageSkipsOptedOutPlayers()
    {
        // null table would throw if the insert path was reached; the opt-out filter leaves nothing to insert
        var storage = new FlipStorageService(null, null, null);
        await storage.SavePlayerIds([(1, Guid.Parse(OptedOut))]);
    }

    [Test]
    public void RecordNeverThrowsWhenStorageFails()
    {
        var storage = new Mock<FlipStorageService>(null, null, null);
        storage.Setup(s => s.SavePlayerIds(It.IsAny<IEnumerable<(long, Guid)>>())).ThrowsAsync(new Exception("down"));
        var recorder = new PlayerIdRecorder();
        Assert.DoesNotThrow(() => recorder.Record([A], storage.Object, null));
        Assert.DoesNotThrow(() => recorder.Record(null, storage.Object, null));
    }
}
