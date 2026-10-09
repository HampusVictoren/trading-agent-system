using Engine.Application.UseCases;
using Engine.Domain.Trading;
using Engine.Hosting.Options;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Engine.Tests.Application.UseCases;

/// <summary>
/// The last thing between an approved order and the portfolio, as a table: mode against switch.
/// </summary>
public class OrderGateTests
{
    private static OrderGate AGate(TradingMode? mode, FixedKillSwitch killSwitch) =>
        new(Options.Create(new TradingOptions { Mode = mode }), killSwitch);

    [Fact]
    public async Task Paper_with_the_switch_released_places_the_order()
    {
        var gate = AGate(TradingMode.Paper, FixedKillSwitch.Released());

        (await gate.AskAsync(TestContext.Current.CancellationToken)).ShouldBeOfType<OrderPermission.Granted>();
    }

    [Fact]
    public async Task Paper_with_the_switch_engaged_places_nothing_and_says_why()
    {
        var gate = AGate(TradingMode.Paper, FixedKillSwitch.Engaged("prices look wrong"));

        var permission = await gate.AskAsync(TestContext.Current.CancellationToken);

        permission.ShouldBeOfType<OrderPermission.Halted>()
            .Reason.ShouldBe("Kill switch engaged: prices look wrong");
    }

    [Fact]
    public async Task The_switch_is_read_on_every_ask_rather_than_once()
    {
        // The read just before an order is the one that matters, and a cached answer would be the
        // state from whenever the gate was first asked - minutes earlier, in a cycle.
        var killSwitch = FixedKillSwitch.Released();
        var gate = AGate(TradingMode.Paper, killSwitch);

        await gate.AskAsync(TestContext.Current.CancellationToken);
        await gate.AskAsync(TestContext.Current.CancellationToken);

        killSwitch.Reads.ShouldBe(2);
    }

    [Fact]
    public async Task Shadow_places_nothing_whatever_the_switch_says()
    {
        var killSwitch = FixedKillSwitch.Released();
        var gate = AGate(TradingMode.Shadow, killSwitch);

        (await gate.AskAsync(TestContext.Current.CancellationToken)).ShouldBeOfType<OrderPermission.ShadowOnly>();
    }

    [Fact]
    public async Task A_missing_mode_is_shadow()
    {
        // Unreachable in a running engine - validation refuses it at startup - and the fallback
        // goes the way that places nothing.
        var gate = AGate(mode: null, FixedKillSwitch.Released());

        gate.Mode.ShouldBe(TradingMode.Shadow);
        (await gate.AskAsync(TestContext.Current.CancellationToken)).ShouldBeOfType<OrderPermission.ShadowOnly>();
    }

    [Fact]
    public async Task Live_is_never_granted()
    {
        var gate = AGate(TradingMode.Live, FixedKillSwitch.Released());

        await Should.ThrowAsync<InvalidOperationException>(
            () => gate.AskAsync(TestContext.Current.CancellationToken));
    }
}
