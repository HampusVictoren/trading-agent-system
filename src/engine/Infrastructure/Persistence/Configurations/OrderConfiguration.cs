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

        // Version 7 ids come from the domain, time-ordered, so the primary key's index also
        // gives the ledger its natural reading order.
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

        // Notional is worked out from the two columns beside it, so storing it would be a
        // second copy of the same fact that nothing keeps in step.
        builder.Ignore(order => order.Notional);

        builder.Property<DateTimeOffset>(PlacedAtColumn)
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        builder.HasIndex(order => order.PortfolioId);
    }

    /// <summary>
    /// When the order was recorded, kept as a shadow property. It is audit metadata rather
    /// than something the domain reasons about, and the database's clock is the one clock
    /// every row agrees on. Stage 5 counts a holding period from the last purchase; that is
    /// when it becomes domain data and starts coming from the engine's injected clock.
    /// </summary>
    public const string PlacedAtColumn = "PlacedAt";
}
