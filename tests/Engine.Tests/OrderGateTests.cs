using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Trading;
using Engine.Hosting.Options;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Engine.Tests.Application.UseCases;

/// <summary>
/// The last thing between an approved order and the portfolio, as a table: mode, side and switch.
/// </summary>
public class OrderGateTests
{
    private static OrderGate AGate(TradingMode? mode, FixedKillSwitch killSwitch) =>
        new(Options.Create(new TradingOptions { Mode = mode }), killSwitch);

    [Fact]
    public async Task Paper_with_the_switch_released_places_the_order()
    {
        var gate = AGate(TradingMode.Paper, FixedKillSwitch.Released());

        (await gate.AskAsync(OrderSide.Buy, TestContext.Current.CancellationToken)).ShouldBeOfType<OrderPermission.Granted>();
    }

    [Fact]
    public async Task Paper_with_the_switch_engaged_places_nothing_and_says_why()
    {
        var gate = AGate(TradingMode.Paper, FixedKillSwitch.Engaged("prices look wrong"));

        var permission = await gate.AskAsync(OrderSide.Buy, TestContext.Current.CancellationToken);

        permission.ShouldBeOfType<OrderPermission.Halted>()
            .Reason.ShouldBe("Kill switch engaged: prices look wrong");
    }

    [Fact]
    public async Task Paper_with_the_switch_engaged_still_sells_and_does_not_even_read_it()
    {
        // The switch stops new buys. A sale reduces exposure, which is what pulling it is for, so a
        // stop-loss, a time limit and the agents' own SELL all go out while it is engaged.
        var killSwitch = FixedKillSwitch.Engaged("prices look wrong");
        var gate = AGate(TradingMode.Paper, killSwitch);

        (await gate.AskAsync(OrderSide.Sell, TestContext.Current.CancellationToken))
            .ShouldBeOfType<OrderPermission.Granted>();

        killSwitch.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task A_switch_that_could_not_be_read_halts_a_buy()
    {
        // KillSwitch reads a failure as engaged, with the failure as its reason. The gate does not
        // tell the two apart, and that is the point: a buy is never placed on "I could not tell".
        var gate = AGate(
            TradingMode.Paper, FixedKillSwitch.Engaged("the kill switch could not be read (NpgsqlException)"));

        (await gate.AskAsync(OrderSide.Buy, TestContext.Current.CancellationToken))
            .ShouldBeOfType<OrderPermission.Halted>()
            .Reason.ShouldBe("Kill switch engaged: the kill switch could not be read (NpgsqlException)");
    }

    [Fact]
    public async Task Shadow_sells_nothing_either()
    {
        var gate = AGate(TradingMode.Shadow, FixedKillSwitch.Released());

        (await gate.AskAsync(OrderSide.Sell, TestContext.Current.CancellationToken))
            .ShouldBeOfType<OrderPermission.ShadowOnly>();
    }

    [Fact]
    public async Task The_switch_is_read_on_every_ask_rather_than_once()
    {
        // The read just before an order is the one that matters, and a cached answer would be the
        // state from whenever the gate was first asked - minutes earlier, in a cycle.
        var killSwitch = FixedKillSwitch.Released();
        var gate = AGate(TradingMode.Paper, killSwitch);

        await gate.AskAsync(OrderSide.Buy, TestContext.Current.CancellationToken);
        await gate.AskAsync(OrderSide.Buy, TestContext.Current.CancellationToken);

        killSwitch.Reads.ShouldBe(2);
    }

    [Fact]
    public async Task Shadow_places_nothing_whatever_the_switch_says()
    {
        var killSwitch = FixedKillSwitch.Released();
        var gate = AGate(TradingMode.Shadow, killSwitch);

        (await gate.AskAsync(OrderSide.Buy, TestContext.Current.CancellationToken)).ShouldBeOfType<OrderPermission.ShadowOnly>();
    }

    [Fact]
    public async Task A_missing_mode_is_shadow()
    {
        // Unreachable in a running engine - validation refuses it at startup - and the fallback
        // goes the way that places nothing.
        var gate = AGate(mode: null, FixedKillSwitch.Released());

        gate.Mode.ShouldBe(TradingMode.Shadow);
        (await gate.AskAsync(OrderSide.Buy, TestContext.Current.CancellationToken)).ShouldBeOfType<OrderPermission.ShadowOnly>();
    }

    [Fact]
    public async Task Live_is_never_granted()
    {
        var gate = AGate(TradingMode.Live, FixedKillSwitch.Released());

        await Should.ThrowAsync<InvalidOperationException>(
            () => gate.AskAsync(OrderSide.Buy, TestContext.Current.CancellationToken));
    }
}
