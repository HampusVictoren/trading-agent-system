namespace Engine.Infrastructure.Persistence.Configurations;

using Engine.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class SignalOutcomeConfiguration : IEntityTypeConfiguration<SignalOutcomeRecord>
{
    /// <summary>Returns are fractions, stored to a ten-thousandth of a basis point - the same
    /// precision the calculator rounds to, so nothing is lost or invented on the way in.</summary>
    private const int ReturnDigits = 12;
    private const int ReturnDecimals = 6;

    public void Configure(EntityTypeBuilder<SignalOutcomeRecord> builder)
    {
        builder.ToTable("signal_outcomes");

        builder.HasKey(outcome => outcome.Id);
        builder.Property(outcome => outcome.Id).UseIdentityAlwaysColumn();

        // One measurement per signal per horizon. A sweep that runs twice - two engines, or
        // one restarted mid-sweep - cannot write the same row again, which is the same guard
        // decisions.correlation_id gives a cycle.
        builder.HasIndex(outcome => new { outcome.DecisionId, outcome.HorizonUnit, outcome.HorizonDays })
            .IsUnique();

        builder.HasOne<DecisionRecord>()
            .WithMany()
            .HasForeignKey(outcome => outcome.DecisionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Both enums as text, for the reason every other enum here is: this history is read
        // from psql at least as often as from C#.
        builder.Property(outcome => outcome.HorizonUnit).HasConversion<string>().HasMaxLength(16);
        builder.Property(outcome => outcome.Status).HasConversion<string>().HasMaxLength(16);

        builder.Property(outcome => outcome.BenchmarkSymbol).HasMaxLength(TickerColumn.MaxLength);

        foreach (var column in new[]
                 {
                     nameof(SignalOutcomeRecord.InstrumentReturn),
                     nameof(SignalOutcomeRecord.BenchmarkReturn),
                     nameof(SignalOutcomeRecord.ExcessReturn),
                     nameof(SignalOutcomeRecord.CostFraction),
                     nameof(SignalOutcomeRecord.NetEdge),
                 })
        {
            builder.Property<decimal?>(column).HasPrecision(ReturnDigits, ReturnDecimals);
        }

        builder.Property(outcome => outcome.MeasuredPrice)
            .HasPrecision(MoneyPrecision.Digits, MoneyPrecision.Decimals);

        builder.Property(outcome => outcome.RecordedAt)
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();
    }
}
