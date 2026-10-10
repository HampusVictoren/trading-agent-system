using Engine.Hosting.Lock;
using Engine.Hosting.Options;
using Engine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;

namespace Engine.Tests.Persistence;

/// <summary>
/// The one-engine lock against a real Postgres: a second engine is refused, the lock goes with its
/// session however that ends, a lost lock stops the engine, and migrating is never blocked.
/// </summary>
[Collection(TradingDatabaseCollection.Name)]
public class EngineLockTests
{
    private readonly TradingDatabaseFixture _database;

    public EngineLockTests(TradingDatabaseFixture database) => _database = database;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task TerminateAsync(int backend)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection);
        command.Parameters.AddWithValue("pid", backend);
        (await command.ExecuteScalarAsync(Token)).ShouldBe(true);
    }

    private EngineLockService NewService(IHostApplicationLifetime? lifetime = null) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ConnectionString = _database.ConnectionString }),
            lifetime ?? Substitute.For<IHostApplicationLifetime>(),
            NullLogger<EngineLockService>.Instance)
        {
            CheckInterval = TimeSpan.FromMilliseconds(100),
        };

    [Fact]
    public async Task A_second_engine_is_refused_while_the_first_holds_it_and_admitted_once_it_lets_go()
    {
        var first = await EngineInstanceLock.TryAcquireAsync(_database.ConnectionString, Token);
        first.ShouldNotBeNull();

        (await EngineInstanceLock.TryAcquireAsync(_database.ConnectionString, Token)).ShouldBeNull();

        await first.DisposeAsync();

        await using var second = await EngineInstanceLock.TryAcquireAsync(_database.ConnectionString, Token);
        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_session_that_ends_takes_the_lock_with_it_and_says_so()
    {
        await using var held = (await EngineInstanceLock.TryAcquireAsync(_database.ConnectionString, Token))!;
        (await held.IsStillHeldAsync(Token)).ShouldBeTrue();

        await TerminateAsync(held.BackendProcessId);

        (await held.IsStillHeldAsync(Token)).ShouldBeFalse();
        await using var next = await EngineInstanceLock.TryAcquireAsync(_database.ConnectionString, Token);
        next.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_holder_is_described_by_its_session()
    {
        await using var held = (await EngineInstanceLock.TryAcquireAsync(_database.ConnectionString, Token))!;

        var holder = await EngineInstanceLock.DescribeHolderAsync(_database.ConnectionString, Token);

        holder.ShouldNotBeNull();
        holder.ShouldContain(EngineInstanceLock.ApplicationName);
        holder.ShouldContain($"backend pid {held.BackendProcessId}");
    }

    [Fact]
    public async Task Migrating_and_ordinary_queries_are_not_blocked_while_an_engine_holds_it()
    {
        await using var held = (await EngineInstanceLock.TryAcquireAsync(_database.ConnectionString, Token))!;

        // EF's migration lock, which the migrator and the efbundle take, is a LOCK TABLE on the
        // history table - a different lock altogether. Bounded, so a regression fails, not hangs.
        await using var context = _database.NewContext();
        await context.Database.MigrateAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
        (await context.Portfolios.CountAsync(Token)).ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task The_service_refuses_to_start_a_second_engine_before_any_worker_runs()
    {
        await using var other = (await EngineInstanceLock.TryAcquireAsync(_database.ConnectionString, Token))!;
        var worker = new RecordingService();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new DatabaseOptions { ConnectionString = _database.ConnectionString }));
        builder.Services.AddSingleton<EngineLockService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<EngineLockService>());
        builder.Services.AddHostedService(_ => worker);
        using var host = builder.Build();

        var refusal = await Should.ThrowAsync<EngineAlreadyRunningException>(() => host.StartAsync(Token));

        refusal.Message.ShouldContain("Another engine is already running against this database");
        refusal.Holder.ShouldNotBeNull();
        refusal.Holder.ShouldContain($"backend pid {other.BackendProcessId}");
        worker.Started.ShouldBeFalse();
    }

    [Fact]
    public async Task A_lost_lock_stops_the_engine_rather_than_letting_it_trade_unlocked()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource();
        lifetime.When(l => l.StopApplication()).Do(_ => stopped.TrySetResult());
        await using var service = NewService(lifetime);
        await service.StartAsync(Token);

        var backend = await HolderBackendAsync();
        await TerminateAsync(backend);

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        service.Lost.ShouldBeTrue();
        await service.StopAsync(Token);
    }

    [Fact]
    public async Task While_the_lock_holds_nothing_is_stopped_and_stopping_releases_it()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        await using var service = NewService(lifetime);
        await service.StartAsync(Token);

        await Task.Delay(TimeSpan.FromMilliseconds(500), Token); // several checks
        lifetime.DidNotReceive().StopApplication();
        service.Lost.ShouldBeFalse();
        (await EngineInstanceLock.TryAcquireAsync(_database.ConnectionString, Token)).ShouldBeNull();

        await service.StopAsync(Token);

        await using var next = await EngineInstanceLock.TryAcquireAsync(_database.ConnectionString, Token);
        next.ShouldNotBeNull();
    }

    private async Task<int> HolderBackendAsync()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(
            "SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND granted AND classid = @c::oid AND objid = @o::oid",
            connection);
        command.Parameters.AddWithValue("c", EngineInstanceLock.ClassKey);
        command.Parameters.AddWithValue("o", EngineInstanceLock.ObjectKey);
        return (int)(await command.ExecuteScalarAsync(Token))!;
    }

    private sealed class RecordingService : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
