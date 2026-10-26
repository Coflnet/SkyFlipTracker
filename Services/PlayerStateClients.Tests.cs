using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// A player-state that answers after minutes held consumed batches past the kafka poll interval and got the pods evicted.
/// </summary>
public class PlayerStateClientsTests
{
    private static IConfiguration Settings(params (string, string)[] values)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (key, value) in values)
            dict[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Test]
    public void TimeoutDefaultsToThirtySecondsAndHonoursOverride()
    {
        PlayerStateClients.GetTimeout(Settings()).Should().Be(TimeSpan.FromSeconds(30));
        PlayerStateClients.GetTimeout(Settings(("PLAYERSTATE_TIMEOUT_SECONDS", "5"))).Should().Be(TimeSpan.FromSeconds(5));
        PlayerStateClients.GetTimeout(Settings(("PLAYERSTATE_TIMEOUT_SECONDS", "0"))).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task HangingRequestIsCutOffAndRetriedAsInfrastructureError()
    {
        // loopback server that accepts the request and never answers
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var pending = new List<HttpListenerContext>();
        _ = Task.Run(async () =>
        {
            try { while (true) pending.Add(await listener.GetContextAsync()); }
            catch (Exception) { /* listener closed */ }
        });
        var api = PlayerStateClients.CreateTransactionApi(Settings(
            ("PLAYERSTATE_BASE_URL", $"http://127.0.0.1:{port}"), ("PLAYERSTATE_TIMEOUT_SECONDS", "1")));

        var watch = Stopwatch.StartNew();
        Exception thrown = null;
        try
        {
            await api.TransactionItemItemIdGetAsync(1, 0);
        }
        catch (Exception e)
        {
            thrown = e;
        }
        watch.Stop();

        thrown.Should().NotBeNull("the request never gets an answer");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        // retried with backoff by ProcessResilient instead of skipping the sell
        TrackerBackgroundService.IsInfrastructureError(thrown).Should().BeTrue(thrown?.ToString());
    }

    [TestCase(0, true)]
    [TestCase(503, true)]
    [TestCase(404, false)]
    [TestCase(400, false)]
    public void OnlyUnreachablePlayerStateIsInfrastructureError(int code, bool expected)
    {
        var error = new PlayerState.Client.Client.ApiException(code, "player-state");

        TrackerBackgroundService.IsInfrastructureError(error).Should().Be(expected);
    }

    private static int GetFreePort()
    {
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }
}
