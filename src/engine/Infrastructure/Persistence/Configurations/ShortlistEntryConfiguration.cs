namespace Engine.Infrastructure.Persistence.Configurations;

using Engine.Application.Contracts;
using Engine.Application.Persistence;
using Engine.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// One trading day's screen, one row per instrument it had something to say about.
/// </summary>
public sealed class ShortlistEntryConfiguration : IEntityTypeConfiguration<ShortlistEntry>
{
    public void Configure(EntityTypeBuilder<ShortlistEntry> builder)
    {
        builder.ToTable("shortlists");

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).UseIdentityAlwaysColumn();

        builder.Property(entry => entry.CorrelationId).HasMaxLength(64);

        builder.Property(entry => entry.Symbol)
            .HasConversion(ticker => ticker.Value, value => new Ticker(value))
            .HasColumnName("symbol")
            .HasMaxLength(TickerColumn.MaxLength);

        // One verdict per instrument per trading day, as a primary-key-grade rule rather than a
        // convention. It is also what makes the engine's "have I screened today?" safe: two
        // cycles that both decided to screen cannot both store one, and the loser's commit
        // fails rather than doubling the day's shortlist.
        builder.HasIndex(entry => new { entry.ScreenedOn, entry.Symbol }).IsUnique();

        // The figures, at the same precision as everything else that came off a price. Score
        // and the two fractions are not money, so they get their own width: a ratio with six
        // decimals, which is what the agent service rounds to anyway.
        builder.Property(entry => entry.Score).HasPrecision(RatioPrecision.Digits, RatioPrecision.Decimals);

        // Named for the contract rather than for the property. The snake-case convention turns
        // Return3M into return3m, and these columns are read beside the JSON they came from -
        // one of the two spellings had to give, and it is not the one in the contract.
        builder.Property(entry => entry.Return3M)
            .HasColumnName("return_3m")
            .HasPrecision(RatioPrecision.Digits, RatioPrecision.Decimals);
        builder.Property(entry => entry.Volatility30D)
            .HasColumnName("volatility_30d")
            .HasPrecision(RatioPrecision.Digits, RatioPrecision.Decimals);
        builder.Property(entry => entry.MedianDollarVolume)
            .HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);

        builder.Property(entry => entry.RejectedBecause).HasMaxLength(ScreenMapper.MaxReasonLength);

        // The other service's clock, so its offset is normalised rather than refused.
        builder.Property(entry => entry.ScreenedAt).HasConversion(StoredInstant.Converter);

        builder.Property(entry => entry.RecordedAt)
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        // The one query anything makes of this table today, and the one the comparison in the
        // next pull request makes of it: everything from a given day, in rank order.
        builder.HasIndex(entry => new { entry.ScreenedOn, entry.Rank });
    }
}

/// <summary>One precision for every ratio in the schema - a score, a return, a volatility -
/// kept apart from money because they are not amounts and do not share its width.</summary>
internal static class RatioPrecision
{
    public const int Digits = 18;
    public const int Decimals = 6;
}
