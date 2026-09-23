namespace Engine.Infrastructure.Persistence.Configurations;

using Engine.Application.Persistence;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Every analysis cycle, whatever came of it. The shape is the decision this pull request
/// exists to take: what is a column here is what a report can group by later.
/// </summary>
public sealed class DecisionRecordConfiguration : IEntityTypeConfiguration<DecisionRecord>
{
    /// <summary>The contract's own caps, so a value the agent service could not have sent
    /// cannot be stored either.</summary>
    private const int MaxThesisLength = 2000;
    private const int MaxRiskLength = 300;
    private const int MaxTeamVersionLength = 64;

    public void Configure(EntityTypeBuilder<DecisionRecord> builder)
    {
        builder.ToTable("decisions");

        builder.HasKey(decision => decision.Id);
        builder.Property(decision => decision.Id).UseIdentityAlwaysColumn();

        // A retried call cannot become two decisions. The client already refuses to retry a
        // failing response for this reason; the unique index is the half that holds even if
        // that rule is ever relaxed by accident.
        builder.HasIndex(decision => decision.CorrelationId).IsUnique();
        builder.Property(decision => decision.CorrelationId).HasMaxLength(64);

        builder.Property(decision => decision.Symbol)
            .HasConversion(ticker => ticker.Value, value => new Ticker(value))
            .HasColumnName("symbol")
            .HasMaxLength(TickerColumn.MaxLength);

        builder.Property(decision => decision.TeamId).HasMaxLength(64);
        builder.Property(decision => decision.TeamVersion).HasMaxLength(MaxTeamVersionLength);

        builder.Property(decision => decision.AvailableRiskBudgetUsd)
            .HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);
        builder.Property(decision => decision.MaxPositionPct).HasPrecision(9, 6);
        builder.Property(decision => decision.ExistingQuantity)
            .HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);
        builder.Property(decision => decision.ExistingAveragePrice)
            .HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);
        builder.Property(decision => decision.ReferencePrice)
            .HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);
        builder.Property(decision => decision.ReferenceCurrency).HasMaxLength(3).IsFixedLength();

        builder.Property(decision => decision.Thesis).HasMaxLength(MaxThesisLength);

        // A Postgres text[], not JSON. The risks are a list of strings and nothing more, and
        // an array column can be unnested in the report view without a parser.
        builder.Property(decision => decision.KeyRisks)
            .HasColumnType($"varchar({MaxRiskLength})[]");

        // Both enums are stored as text, for the same reason the order side is: a decision
        // history is read from psql at least as often as from C#.
        builder.Property(decision => decision.Stance).HasConversion<string>().HasMaxLength(8);
        builder.Property(decision => decision.Outcome).HasConversion<string>().HasMaxLength(24);

        builder.Property(decision => decision.RecordedAt)
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        builder.HasOne<Portfolio>()
            .WithMany()
            .HasForeignKey(decision => decision.PortfolioId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(decision => decision.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        // The two questions stage 4 ends with: how did this team version do, and does
        // conviction mean anything. Both are answered by scanning this index rather than
        // the table.
        builder.HasIndex(decision => new { decision.TeamVersion, decision.Outcome });
        builder.HasIndex(decision => new { decision.Symbol, decision.RequestedAt });
    }
}
