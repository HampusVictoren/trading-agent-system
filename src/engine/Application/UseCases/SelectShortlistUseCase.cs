namespace Engine.Application.UseCases;

using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.Persistence;
using Engine.Domain.Screening;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Today's shortlist: read back if the day has already been screened, fetched and stored if it
/// has not.
/// </summary>
/// <remarks>
/// <para>
/// One screen per trading day rather than one per cycle, and that is the contract's own claim
/// rather than a saving taken against it: the factors are computed from daily bars, so two
/// screens on the same day rank the same way. Re-running it would spend a batched fetch of the
/// whole universe to arrive at the list already in the database.
/// </para>
/// <para>
/// What it buys is that the market-data source's rate limit stops being a consideration in the
/// cycle at all, and that the shortlist a decision was made from is a row rather than a
/// reconstruction. The cost is that a name that starts moving at eleven o'clock is not ranked
/// again until tomorrow - which is the same trade the fact-sheet rule makes one level down, and
/// it is why the exits are deterministic and run every cycle.
/// </para>
/// </remarks>
public sealed class SelectShortlistUseCase
{
    private readonly IAgentClient _agentClient;
    private readonly IShortlistLog _shortlists;
    private readonly TradingOptions _trading;
    private readonly ILogger<SelectShortlistUseCase> _logger;

    public SelectShortlistUseCase(
        IAgentClient agentClient,
        IShortlistLog shortlists,
        IOptions<TradingOptions> trading,
        ILogger<SelectShortlistUseCase> logger)
    {
        _agentClient = agentClient;
        _shortlists = shortlists;
        _trading = trading.Value;
        _logger = logger;
    }

    /// <summary>
    /// The instruments to analyse today, best first. Empty when the screen could not be reached
    /// or found nothing - both of which leave the holdings to be analysed on their own, which is
    /// the outcome that matters: a screen is how new positions are found, and an outage must not
    /// stop the ones already open from being looked at.
    /// </summary>
    /// <remarks>
    /// Rows are queued, not committed. The caller owns the transaction, so the shortlist and
    /// whatever the cycle does with it land together or not at all.
    /// </remarks>
    public async Task<IReadOnlyList<Ticker>> ExecuteAsync(
        DateOnly today, string correlationId, CancellationToken cancellationToken = default)
    {
        var stored = await _shortlists.ForAsync(today, cancellationToken);

        if (stored.Count > 0)
        {
            // Including a day where every instrument was rejected. That day has been screened,
            // and screening it again is the one case guaranteed to produce the same nothing.
            var shortlisted = stored
                .Where(entry => entry.Rank is not null)
                .Select(entry => entry.Symbol)
                .ToArray();

            _logger.LogDebug(
                "{Count} instrument(s) already screened for {Day}, so no screen was requested.",
                shortlisted.Length, today);

            return shortlisted;
        }

        var screen = await ScreenOrNothingAsync(correlationId, cancellationToken);

        if (screen is null)
            return [];

        Store(screen, today, correlationId);

        _logger.LogInformation(
            "Screened {Universe} instrument(s) for {Day}: {Shortlisted} shortlisted, {Rejected} rejected.",
            _trading.Universe.Length, today, screen.Candidates.Count, screen.Rejected.Count);

        return screen.Candidates.Select(candidate => candidate.Ticker).ToArray();
    }

    /// <summary>
    /// A screen that failed is not a failed cycle. Nothing was stored, so the next cycle asks
    /// again - and a day that ends without one costs new candidates, not the portfolio.
    /// </summary>
    private async Task<Screen?> ScreenOrNothingAsync(string correlationId, CancellationToken cancellationToken)
    {
        var request = new ScreenRequestDto
        {
            Universe = _trading.Universe
                .Select(symbol => (InstrumentDto)new EquityInstrumentDto { Symbol = symbol })
                .ToArray(),
            Limit = _trading.ShortlistSize,

            // Validated at startup as [Required], so the null-forgiving read is the shape of
            // the setting rather than an assumption: zero is a legitimate floor and had to be
            // told apart from a missing key.
            MinDollarVolume = _trading.MinDollarVolume!.Value,
            CorrelationId = correlationId
        };

        try
        {
            var dto = await _agentClient.GetScreenAsync(request, cancellationToken);

            if (dto is null)
            {
                _logger.LogWarning("The screen came back with an empty body, so nothing was ranked today.");
                return null;
            }

            var asked = request.Universe
                .OfType<EquityInstrumentDto>()
                .Select(equity => Ticker.TryCreate(equity.Symbol, out var ticker) ? ticker : null)
                .Where(ticker => ticker is not null)
                .Cast<Ticker>()
                .ToHashSet();

            return ScreenMapper.ToDomain(dto, asked, request.Limit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // We are shutting down.
        }
        catch (Exception ex) when (ex is AgentServiceUnavailableException or AgentResponseInvalidException)
        {
            _logger.LogWarning(ex, "No shortlist this cycle, so only the holdings will be analysed.");
            return null;
        }
    }

    private void Store(Screen screen, DateOnly today, string correlationId)
    {
        // Rank from 1, and from the position in the list rather than from the score. The order
        // is what the contract promises and the mapper has already checked; deriving the rank
        // from the score again would have to reproduce the tie-break too.
        for (var index = 0; index < screen.Candidates.Count; index++)
        {
            var candidate = screen.Candidates[index];

            _shortlists.Record(new ShortlistEntry
            {
                CorrelationId = correlationId,
                ScreenedOn = today,
                ScreenedAt = screen.AsOf,
                Symbol = candidate.Ticker,
                Rank = index + 1,
                Score = candidate.Score,
                Return3M = candidate.Return3M,
                Volatility30D = candidate.Volatility30D,
                MedianDollarVolume = candidate.MedianDollarVolume
            });
        }

        foreach (var rejection in screen.Rejected)
        {
            _shortlists.Record(new ShortlistEntry
            {
                CorrelationId = correlationId,
                ScreenedOn = today,
                ScreenedAt = screen.AsOf,
                Symbol = rejection.Ticker,
                RejectedBecause = rejection.Reason
            });
        }
    }
}
