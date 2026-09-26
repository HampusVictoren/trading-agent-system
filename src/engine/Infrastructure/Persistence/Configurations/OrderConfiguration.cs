namespace Engine.Infrastructure.Persistence.Configurations;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// The ledger. Append-only, and the migration puts a trigger on the table that says so in
/// the one place that cannot be bypassed by a future code path.
/// </summary>
public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");

        builder.HasKey(order => order.Id);

        // Version 7 ids come from the domain, time-ordered to the millisecond, so the primary
        // key's index also gives the ledger its reading order - between cycles. Within one
        // millisecond it does not: .NET fills the bits after the timestamp at random, so two
        // orders placed in the same transaction sort arbitrarily, and `placed_at` cannot break
        // the tie either because `now()` is the transaction's clock and identical for both.
        // Nothing reads the ledger in order today. A cycle that places several sales will, and
        // that is when it needs a sequence of its own rather than a sharper timestamp.
        builder.Property(order => order.Id).ValueGeneratedNever();

        builder.Property(order => order.Ticker)
            .HasConversion(ticker => ticker.Value, value => new Ticker(value))
            .HasColumnName("symbol")
            .HasMaxLength(TickerColumn.MaxLength);

        // Stored as text, not as an int. A number in a ledger that only a C# enum can decode
        // is unreadable from psql, and adding Sell in stage 5 then needs no migration at all.
        builder.Property(order => order.Side)
            .HasConversion<string>()
            .HasMaxLength(8);

        builder.Property(order => order.Quantity)
            .HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);

        builder.ComplexProperty(order => order.Price, price =>
        {
            price.Property(money => money.Amount).HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);
            price.Property(money => money.Currency).HasMaxLength(3).IsFixedLength();
        });

        // Text as well, and for the same reason: `triggered_by = 'StopLoss'` is readable from
        // psql, and stage 5's later exits add values without a migration. The column is named
        // rather than defaulted to "Trigger", which in a table that carries an append-only
        // trigger would be a word with two meanings in the same schema.
        builder.Property(order => order.Trigger)
            .HasConversion<string>()
            .HasColumnName("triggered_by")
            .HasMaxLength(16);

        // Notional is worked out from the two columns beside it, so storing it would be a
        // second copy of the same fact that nothing keeps in step.
        builder.Ignore(order => order.Notional);

        // Owned rather than a complex property, because it is optional - null on a buy - and a
        // complex property cannot be. Unlike Notional this one *is* stored: it is the only
        // number here that cannot be recomputed from the row, since the average purchase price
        // it was measured against is gone once the holding closes.
        builder.OwnsOne(order => order.RealisedProfitAndLoss, realised =>
        {
            realised.Property(money => money.Amount)
                .HasColumnName("realised_pnl_amount")
                .HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);

            realised.Property(money => money.Currency)
                .HasColumnName("realised_pnl_currency")
                .HasMaxLength(3)
                .IsFixedLength();
        });

        builder.Property<DateTimeOffset>(PlacedAtColumn)
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        builder.HasIndex(order => order.PortfolioId);
    }

    /// <summary>
    /// When the order was recorded, kept as a shadow property. It is audit metadata rather
    /// than something the domain reasons about, and the database's clock is the one clock
    /// every row agrees on. It stayed that way: stage 5 does count a holding period from the
    /// last purchase, but from `positions.last_purchased_at`, which the engine's own clock
    /// writes. A holding period measured off the ledger would have had to reconstruct which
    /// order was the last buy, and would have read the database's clock to answer a question
    /// about the engine's.
    /// </summary>
    public const string PlacedAtColumn = "PlacedAt";
}
