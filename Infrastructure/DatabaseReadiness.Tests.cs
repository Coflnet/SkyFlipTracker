using System;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Coflnet.Cassandra;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Infrastructure;

public class DatabaseReadinessTests
{
    [Test]
    public async Task ReadinessQueriesBothDatabasesEveryTimeWithoutOpeningOldSession()
    {
        var session = HealthySession();
        var sql = new SqlProbe();
        using var services = Services(session.Object, sql);
        await DatabaseReadiness.CheckAsync(services);
        await DatabaseReadiness.CheckAsync(services);
        session.Verify(s => s.ExecuteAsync(It.Is<SimpleStatement>(q => q.QueryString == "SELECT key FROM system.local")), Times.Exactly(2));
        Assert.That(sql.Calls, Is.EqualTo(2));
        Assert.That(sql.CancellationCanBeRequested, Is.True);
    }

    [Test]
    public void IncompleteOptInPemConfigurationFailsBeforeAnyConnection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["CASSANDRA:HOSTS"] = "127.0.0.1", ["CASSANDRA:USER"] = "synthetic",
            ["CASSANDRA:PASSWORD"] = "synthetic", ["CASSANDRA:KEYSPACE"] = "synthetic",
            ["CASSANDRA:TLS:CA_CERT_PATH"] = "/synthetic/absent-ca.pem"
        }).Build();
        var collection = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        collection.AddCassandra();
        using var services = collection.BuildServiceProvider();
        var error = Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<ISession>());
        Assert.That(error.Message, Does.Contain("TLS:CERT_PATH"));
    }

    [Test]
    public void FailureOfEitherDatabaseFailsTheReadinessCheck()
    {
        var session = HealthySession();
        session.Setup(s => s.ExecuteAsync(It.IsAny<IStatement>())).ThrowsAsync(new InvalidOperationException("synthetic CQL failure"));
        using (var services = Services(session.Object, new SqlProbe()))
            Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseReadiness.CheckAsync(services));
        using (var services = Services(HealthySession().Object, new SqlProbe { Fail = true }))
            Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseReadiness.CheckAsync(services));
    }

    [Test]
    public async Task StalledSqlIsCancelledWithinTheOverallReadinessDeadline()
    {
        var sql = new SqlProbe { Stall = true };
        using var services = Services(HealthySession().Object, sql);
        var elapsed = Stopwatch.StartNew();
        Assert.CatchAsync<Exception>(() => DatabaseReadiness.CheckAsync(services));
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(6)));
        Assert.That(sql.Calls, Is.EqualTo(1));
        await sql.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task CallerCancellationCancelsPendingSql()
    {
        var sql = new SqlProbe { Stall = true };
        using var services = Services(HealthySession().Object, sql);
        using var cancellation = new CancellationTokenSource();
        var check = DatabaseReadiness.CheckAsync(services, cancellation.Token);
        await sql.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() => check);
        await sql.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public void StalledCassandraIsBoundedEvenWhenSqlSucceeds()
    {
        var session = new Mock<ISession>();
        var pending = new TaskCompletionSource<RowSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Setup(s => s.ExecuteAsync(It.IsAny<IStatement>())).Returns(pending.Task);
        var sql = new SqlProbe();
        using var services = Services(session.Object, sql);
        var elapsed = Stopwatch.StartNew();
        try
        {
            Assert.ThrowsAsync<TimeoutException>(() => DatabaseReadiness.CheckAsync(services));
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(6)));
            Assert.That(sql.Calls, Is.EqualTo(1));
        }
        finally { pending.TrySetResult(new RowSet()); }
    }

    private static Mock<ISession> HealthySession()
    {
        var session = new Mock<ISession>();
        session.Setup(s => s.ExecuteAsync(It.IsAny<IStatement>())).ReturnsAsync(new RowSet());
        return session;
    }

    private static ServiceProvider Services(ISession session, SqlProbe sql) => new ServiceCollection()
        .AddSingleton(session)
        .AddSingleton<OldSession>(_ => throw new Exception("Old migration session must not be opened by health"))
        .AddDbContext<TrackerDbContext>(options => options.UseMySql(
            "Server=synthetic.invalid;User ID=synthetic;Password=synthetic-only;ConnectionTimeout=3600",
            new MariaDbServerVersion(new Version(10, 11)))
            .AddInterceptors(new SuppressOpen(), sql)).BuildServiceProvider();

    private sealed class SuppressOpen : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => new(InterceptionResult.Suppress());
    }

    private sealed class SqlProbe : DbCommandInterceptor
    {
        public bool Stall, Fail, CancellationCanBeRequested;
        public int Calls;
        public TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Assert.That(command.CommandText, Is.EqualTo("SELECT 1"));
            Calls++;
            Started.TrySetResult(true);
            CancellationCanBeRequested = cancellationToken.CanBeCanceled;
            if (Fail) throw new InvalidOperationException("synthetic SQL failure");
            if (Stall)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { if (cancellationToken.IsCancellationRequested) Cancelled.TrySetResult(true); }
            }
            return InterceptionResult<int>.SuppressWithResult(1);
        }
    }
}
