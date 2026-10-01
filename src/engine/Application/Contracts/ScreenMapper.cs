namespace Engine.Application.Contracts;

using Engine.Application.Interfaces;
using Engine.Domain.Screening;
using Engine.Domain.ValueObjects;

/// <summary>
/// The seam for a screen, matching <see cref="QuoteMapper"/> and <see cref="TradeSignalMapper"/>.
/// System.Text.Json checks the shape; everything the schema says about values it cannot check,
/// and the two things the schema cannot say at all, are checked here.
/// </summary>
/// <remarks>
/// <para>
/// What this does not do is re-derive the score. The pattern elsewhere in the engine is to
/// re-derive an answer from the other direction and refuse it when the two disagree - but
/// that rule is about numbers that move money, and it earns its keep because the engine holds
/// the same inputs. Here it does not: the factors are rounded on the way out, so an equality
/// check would fail on the rounding rather than on a disagreement, and the score orders a
/// list rather than sizing an order.
/// </para>
/// <para>
/// The order, on the other hand, is checked. The contract says best first, and the position in
/// this list becomes the stored rank that this stage's comparison groups by - so an answer
/// that is not sorted would quietly write wrong ranks rather than fail.
/// </para>
/// </remarks>
public static class ScreenMapper
{
    public static Screen ToDomain(ScreenResultDto dto)
    {
        var candidates = dto.Candidates.Select(ToCandidate).ToArray();
        var rejected = dto.Rejected.Select(ToRejection).ToArray();

        for (var i = 1; i < candidates.Length; i++)
        {
            if (candidates[i].Score > candidates[i - 1].Score)
            {
                throw Invalid(
                    $"{candidates[i].Ticker.Value} scores higher than {candidates[i - 1].Ticker.Value} "
                    + "but is ranked below it");
            }
        }

        // One instrument, one verdict. A symbol on both lists would be analysed and explained
        // away in the same cycle; a symbol twice on either would be analysed twice, and the
        // shortlist table would refuse the second row after the first had already been acted
        // on. Both are cheaper to catch here than to discover in a transaction.
        var seen = new HashSet<Ticker>();

        foreach (var ticker in candidates.Select(c => c.Ticker).Concat(rejected.Select(r => r.Ticker)))
        {
            if (!seen.Add(ticker))
                throw Invalid($"{ticker.Value} appears more than once");
        }

        return new Screen(candidates, rejected, dto.AsOf);
    }

    private static Candidate ToCandidate(CandidateDto dto)
    {
        var ticker = TickerOf(dto.Instrument);

        // The schema's exclusiveMinimum. It is the divisor of the score, so a zero is not a
        // flat share - it is a ranking that cannot be computed, and the agent service leaves
        // those out rather than ranking them first.
        if (dto.Volatility30D <= 0m)
            throw Invalid($"the volatility {dto.Volatility30D} for {ticker.Value} is not positive");

        if (dto.MedianDollarVolume < 0m)
            throw Invalid($"the turnover {dto.MedianDollarVolume} for {ticker.Value} is negative");

        return new Candidate(
            ticker, dto.Score, dto.Return3M, dto.Volatility30D, dto.MedianDollarVolume);
    }

    private static Rejection ToRejection(RejectionDto dto)
    {
        var ticker = TickerOf(dto.Instrument);

        // The schema's minLength and maxLength. A rejection whose reason says nothing is the
        // silent shrinking the list exists to prevent, and the cap is what every other
        // free-text field in these contracts has: this string is stored and then read by a
        // person, not parsed.
        if (string.IsNullOrWhiteSpace(dto.Reason))
            throw Invalid($"{ticker.Value} was rejected without a reason");

        if (dto.Reason.Length > MaxReasonLength)
            throw Invalid($"the reason for rejecting {ticker.Value} is longer than {MaxReasonLength} characters");

        return new Rejection(ticker, dto.Reason);
    }

    /// <summary>Mirrors <c>maxLength</c> on the rejection reason in contracts/screen.schema.json.</summary>
    public const int MaxReasonLength = 200;

    private static Ticker TickerOf(InstrumentDto instrument)
    {
        if (instrument is not EquityInstrumentDto equity)
            throw Invalid($"the instrument type '{instrument.GetType().Name}' is not one the engine trades");

        if (!Ticker.TryCreate(equity.Symbol, out var ticker))
            throw Invalid($"the symbol '{equity.Symbol}' is not a ticker");

        return ticker;
    }

    private static AgentResponseInvalidException Invalid(string reason) =>
        new($"The screen makes no sense: {reason}.");
}
