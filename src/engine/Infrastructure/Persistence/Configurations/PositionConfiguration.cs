namespace Engine.Infrastructure.Persistence.Configurations;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    /// <summary>A shadow property: which portfolio a holding belongs to is the repository's
    /// business, not something domain code should be able to set to the wrong value.</summary>
    public const string PortfolioIdColumn = "PortfolioId";

    public void Configure(EntityTypeBuilder<Position> builder)
    {
        builder.ToTable("positions");

        builder.Property<Guid>(PortfolioIdColumn);

        // Composite natural key rather than a surrogate one. "One holding per instrument per
        // portfolio" is an invariant the aggregate already keeps in memory; as a primary key
        // the database keeps it too, and a bug that tried to open a second AAPL position
        // fails on insert instead of quietly double-counting.
        builder.HasKey(PortfolioIdColumn, nameof(Position.Ticker));

        builder.Property(position => position.Ticker)
            .HasConversion(ticker => ticker.Value, value => new Ticker(value))
            .HasColumnName("symbol")
            .HasMaxLength(TickerColumn.MaxLength);

        builder.Property(position => position.Quantity)
            .HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);

        builder.ComplexProperty(position => position.AveragePurchasePrice, price =>
        {
            price.Property(money => money.Amount).HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);
            price.Property(money => money.Currency).HasMaxLength(3).IsFixedLength();
        });
    }
}

/// <summary>The contract caps a symbol at ten characters, and Ticker enforces the same rule.
/// The column says so too, so a value that could not exist cannot be stored.</summary>
internal static class TickerColumn
{
    public const int MaxLength = 10;
}
