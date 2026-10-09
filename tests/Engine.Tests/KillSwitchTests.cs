using Engine.Application.Persistence;
using Engine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Engine.Tests.Persistence;

/// <summary>
/// <c>trading.kill_switch</c>, read the way the engine reads it and written the way an operator
/// writes it - an INSERT, as the superuser would type it in psql.
/// </summary>
[Collection(TradingDatabaseCollection.Name)]
public class KillSwitchTests : IAsyncLifetime
{
    private readonly TradingDatabaseFixture _database;

    public KillSwitchTests(TradingDatabaseFixture database) => _database = database;

    public ValueTask InitializeAsync() => new(_database.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<KillSwitchState> ReadAsync()
    {
        await using var context = _database.NewContext();
        return await new KillSwitch(context, NullLogger<KillSwitch>.Instance)
            .ReadAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_migrated_database_starts_released()
    {
        // The migration's own seed, not the fixture's: read straight after a fresh migration in
        // TradingSchemaTests too. Released, because Trading:Mode already decides whether anything
        // is placed and a fresh checkout ships Shadow.
        (await ReadAsync()).ShouldBe(KillSwitchState.Released);
    }

    [Fact]
    public async Task An_insert_engages_it_with_the_reason_and_the_time()
    {
        await _database.EngageTheKillSwitchAsync("prices look wrong");

        var state = await ReadAsync();

        state.Engaged.ShouldBeTrue();
        state.Reason.ShouldBe("prices look wrong");
        state.Since.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_latest_flip_wins_and_releasing_is_a_new_row()
    {
        await _database.EngageTheKillSwitchAsync("prices look wrong");

        await using (var context = _database.NewContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO trading.kill_switch (engaged, reason) VALUES (false, 'checked, prices are fine')",
                TestContext.Current.CancellationToken);
        }

        (await ReadAsync()).ShouldBe(KillSwitchState.Released);

        // The history is still there: who stopped it, and who started it again.
        await using var reading = _database.NewContext();
        (await reading.KillSwitch.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(3);
    }

    [Fact]
    public async Task An_empty_table_reads_as_engaged()
    {
        // Nobody chose that state, so it is not one to trade under. The reason says how out of it.
        await using (var context = _database.NewContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "TRUNCATE trading.kill_switch", TestContext.Current.CancellationToken);
        }

        var state = await ReadAsync();

        state.Engaged.ShouldBeTrue();
        state.Reason.ShouldNotBeNull().ShouldContain("engaged = false");
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_reads_as_engaged()
    {
        // Fails closed. Port 1 refuses; the one-second timeout is for WSL's mirrored networking,
        // where a dead port hangs instead.
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=tradingdb;Username=engine_svc;Timeout=1")
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var context = new TradingDbContext(options);

        var state = await new KillSwitch(context, NullLogger<KillSwitch>.Instance)
            .ReadAsync(TestContext.Current.CancellationToken);

        state.Engaged.ShouldBeTrue();
        state.Reason.ShouldNotBeNull().ShouldContain("could not be read");
    }

    [Theory]
    [InlineData("UPDATE trading.kill_switch SET engaged = false")]
    [InlineData("DELETE FROM trading.kill_switch")]
    public async Task Its_history_cannot_be_rewritten(string statement)
    {
        // Who stopped trading, and why, is evidence too. Releasing is a new row.
        await using var context = _database.NewContext();

        var exception = await Should.ThrowAsync<Exception>(
            () => context.Database.ExecuteSqlRawAsync(statement, TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("append-only");
    }

    [Fact]
    public async Task A_flip_has_to_say_why()
    {
        await using var context = _database.NewContext();

        var exception = await Should.ThrowAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO trading.kill_switch (engaged, reason) VALUES (true, '  ')",
            TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("ck_kill_switch_reason_is_not_blank");
    }
}
