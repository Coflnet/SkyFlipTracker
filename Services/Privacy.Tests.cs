using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class PrivacyTests
{
    // legacy opted out uuid
    private static readonly Guid OptedOut = Guid.Parse("f3c19fb53ea940f3921e90faab8e2b30");
    private static readonly Guid Other = Guid.Parse("11111111111111111111111111111111");
    private static readonly DateTime T = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp() => PlayerOptOut.ResetMemory();
    [TearDown]
    public void TearDown() => PlayerOptOut.ResetMemory();

    private static PastFlip Flip(Guid p, long uid = 1) => new() { Flipper = p, Uid = uid, SellTime = T.AddMinutes(uid) };

    private static PrivacyService.PlayerExport Export(Guid p, params PastFlip[] flips) =>
        new() { PlayerUuid = p, Flips = new List<PastFlip>(flips) };

    [Test]
    public async Task OptedOutFlipIsNotStored()
    {
        // null session would throw if the insert path was reached
        var storage = new FlipStorageService(null, null, null);
        await storage.SaveFlip(Flip(OptedOut));
        await storage.SaveUnknownFlip(Flip(OptedOut));
        await storage.SaveFlips(new[] { Flip(OptedOut) });
    }

    [Test]
    public void OthersAreStored()
    {
        FlipStorageService.ShouldStore(Other).Should().BeTrue();
        FlipStorageService.ShouldStore(OptedOut).Should().BeFalse();
    }

    [Test]
    public void ValidateRejectsForeignRows()
    {
        var export = Export(OptedOut, Flip(OptedOut), Flip(Other, 2));
        PrivacyService.Validate(OptedOut, export, new[] { Flip(OptedOut) }, true).Should().Be(PrivacyService.EraseCheck.Mismatch);
        PrivacyService.Validate(Other, Export(OptedOut), new List<PastFlip>(), true).Should().Be(PrivacyService.EraseCheck.Mismatch);
    }

    [Test]
    public void ValidateRejectsNotOptedOut()
    {
        PrivacyService.Validate(Other, Export(Other, Flip(Other)), new[] { Flip(Other) }, false).Should().Be(PrivacyService.EraseCheck.NotOptedOut);
    }

    [Test]
    public void ValidateDetectsChangedScope()
    {
        var export = Export(OptedOut, Flip(OptedOut, 1));
        PrivacyService.Validate(OptedOut, export, new[] { Flip(OptedOut, 1), Flip(OptedOut, 2) }, true).Should().Be(PrivacyService.EraseCheck.ScopeChanged);
        PrivacyService.Validate(OptedOut, export, new[] { Flip(OptedOut, 1) }, true).Should().Be(PrivacyService.EraseCheck.Ok);
    }

    [Test]
    public async Task EraseRejectsWithoutDeleting()
    {
        var storage = new Mock<FlipStorageService>(null, null, null);
        storage.Setup(s => s.GetAllFlips(OptedOut)).ReturnsAsync(new List<PastFlip> { Flip(OptedOut, 1), Flip(OptedOut, 2) });
        var service = new PrivacyService(storage.Object);

        (await service.Erase(OptedOut, Export(OptedOut, Flip(Other)))).Check.Should().Be(PrivacyService.EraseCheck.Mismatch);
        (await service.Erase(Other, Export(Other, Flip(Other)))).Check.Should().Be(PrivacyService.EraseCheck.NotOptedOut);
        (await service.Erase(OptedOut, Export(OptedOut, Flip(OptedOut, 1)))).Check.Should().Be(PrivacyService.EraseCheck.ScopeChanged);

        storage.Verify(s => s.DeleteAllFlips(It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public async Task EraseDeletesOnMatch()
    {
        var live = new List<PastFlip> { Flip(OptedOut, 1), Flip(OptedOut, 2) };
        var storage = new Mock<FlipStorageService>(null, null, null);
        storage.Setup(s => s.GetAllFlips(OptedOut)).ReturnsAsync(live);
        storage.Setup(s => s.DeleteAllFlips(OptedOut)).Returns(Task.CompletedTask);
        var service = new PrivacyService(storage.Object);

        var result = await service.Erase(OptedOut, Export(OptedOut, Flip(OptedOut, 2), Flip(OptedOut, 1)));

        result.Check.Should().Be(PrivacyService.EraseCheck.Ok);
        result.DeletedFlips.Should().Be(2);
        storage.Verify(s => s.DeleteAllFlips(OptedOut), Times.Once);
    }

    private static readonly string A = "11111111111111111111111111111111";
    private static readonly string B = "22222222222222222222222222222222";
    private static readonly string C = "33333333333333333333333333333333";

    [Test]
    public async Task RemoveOptedOutDeletesEachOnceAndOnlyNewOnes()
    {
        var storage = new Mock<FlipStorageService>(null, null, null);
        storage.Setup(s => s.DeleteAllFlips(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        var service = new PrivacyService(storage.Object);

        (await service.RemoveOptedOut(new[] { A, B })).Should().Be(2);
        storage.Verify(s => s.DeleteAllFlips(Guid.Parse(A)), Times.Once);
        storage.Verify(s => s.DeleteAllFlips(Guid.Parse(B)), Times.Once);

        (await service.RemoveOptedOut(new[] { A, B })).Should().Be(0);
        storage.Verify(s => s.DeleteAllFlips(It.IsAny<Guid>()), Times.Exactly(2));

        (await service.RemoveOptedOut(new[] { A, B, C })).Should().Be(1);
        storage.Verify(s => s.DeleteAllFlips(Guid.Parse(C)), Times.Once);
        storage.Verify(s => s.DeleteAllFlips(It.IsAny<Guid>()), Times.Exactly(3));
    }

    [Test]
    public async Task RemoveOptedOutRetriesFailedAndContinues()
    {
        var storage = new Mock<FlipStorageService>(null, null, null);
        storage.Setup(s => s.DeleteAllFlips(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        storage.SetupSequence(s => s.DeleteAllFlips(Guid.Parse(A)))
            .ThrowsAsync(new InvalidOperationException("cassandra down"))
            .Returns(Task.CompletedTask);
        var service = new PrivacyService(storage.Object);

        (await service.RemoveOptedOut(new[] { A, B })).Should().Be(1);
        storage.Verify(s => s.DeleteAllFlips(Guid.Parse(B)), Times.Once);

        (await service.RemoveOptedOut(new[] { A, B })).Should().Be(1);
        storage.Verify(s => s.DeleteAllFlips(Guid.Parse(A)), Times.Exactly(2));
        storage.Verify(s => s.DeleteAllFlips(Guid.Parse(B)), Times.Once);
    }

    [Test]
    public async Task RemoveOptedOutUsesCurrentOptOutList()
    {
        var storage = new Mock<FlipStorageService>(null, null, null);
        storage.Setup(s => s.DeleteAllFlips(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        await new PrivacyService(storage.Object).RemoveOptedOut();
        storage.Verify(s => s.DeleteAllFlips(OptedOut), Times.Once);
    }
}
