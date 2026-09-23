namespace Engine.Infrastructure.Persistence.Configurations;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// The aggregate root. Positions are part of it and travel with it; orders hang off it but
/// are never read back, because the ledger is queried rather than loaded.
/// </summary>
public sealed class PortfolioConfiguration : IEntityTypeConfiguration<Portfolio>
{
    public void Configure(EntityTypeBuilder<Portfolio> builder)
    {
        builder.ToTable("portfolios");

        builder.HasKey(portfolio => portfolio.Id);

        // The domain hands out the id, so the database must not invent one of its own.
        builder.Property(portfolio => portfolio.Id).ValueGeneratedNever();

        // Optimistic concurrency on Postgres's own row version. An explicit column would be
        // one more thing to remember to bump; xmin is a system column the database maintains
        // itself and costs no storage. Npgsql's convention recognises a uint row version and
        // maps it there, so no column is created and the domain type never learns about it.
        builder.Property<uint>(XminNames.Column).IsRowVersion();

        builder.ComplexProperty(portfolio => portfolio.CashBalance, cash =>
        {
            cash.Property(money => money.Amount).HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);
            cash.Property(money => money.Currency).HasMaxLength(3).IsFixedLength();
        });

        builder.HasMany(portfolio => portfolio.Positions)
            .WithOne()
            .HasForeignKey(PositionConfiguration.PortfolioIdColumn)
            .OnDelete(DeleteBehavior.Cascade);

        // Orders survive the portfolio being deleted: a ledger that can be erased by deleting
        // something else is not a ledger. Nothing deletes a portfolio today, which is exactly
        // when the rule is cheap to state.
        builder.HasMany(portfolio => portfolio.NewOrders)
            .WithOne()
            .HasForeignKey(order => order.PortfolioId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(portfolio => portfolio.Positions).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(portfolio => portfolio.NewOrders).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>The name Npgsql's system-column convention looks for.</summary>
internal static class XminNames
{
    public const string Column = "xmin";
}

/// <summary>One precision for every amount in the schema, so two columns holding money can
/// never disagree about what they can represent.</summary>
internal static class MoneyPrecision
{
    public const int Digits = 18;
    public const int Decimals = 8;
}
