namespace Engine.Infrastructure.Persistence.Configurations;

using Engine.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class OutcomeDeliveryConfiguration : IEntityTypeConfiguration<OutcomeDelivery>
{
    public void Configure(EntityTypeBuilder<OutcomeDelivery> builder)
    {
        builder.ToTable("outcome_deliveries");

        // The outcome's own id is the key, so a second delivery of the same measurement is
        // refused by the primary key rather than by anyone remembering to check.
        builder.HasKey(delivery => delivery.SignalOutcomeId);
        builder.Property(delivery => delivery.SignalOutcomeId).ValueGeneratedNever();

        builder.HasOne<SignalOutcomeRecord>()
            .WithMany()
            .HasForeignKey(delivery => delivery.SignalOutcomeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(delivery => delivery.DeliveredAt)
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();
    }
}
