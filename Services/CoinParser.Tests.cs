using FluentAssertions;
using NUnit.Framework;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class CoinParserTests
{
    // Regression: production trade descriptions arrive with the minecraft color codes already
    // stripped, so the exact-amount line is "(18,000,000)" instead of "§8(18,000,000)". The old
    // Substring(2) prefix-chop then ate the first digit, turning an 18M sell into an 8M sell.
    [TestCase("Lump-sum amount\n\nTotal Coins Offered:\n18M\n(18,000,000)", 18_000_000L)]
    [TestCase("Lump-sum amount\n\nTotal Coins Offered:\n2.5M\n(2,500,000)", 2_500_000L)]
    [TestCase("§7Lump-sum amount\n\n§6Total Coins Offered:\n§72k\n§8(2,000)", 2_000L)]
    public void ParsesExactCoinAmountRegardlessOfColorCodes(string description, long expected)
    {
        CoinParser.TryParseFromDescription(new[] { description }, out var result).Should().BeTrue();
        result.Should().Be(expected);
    }
}
