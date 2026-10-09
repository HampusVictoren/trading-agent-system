namespace Engine.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// <c>trading.kill_switch</c>: every flip, latest wins. Append-only by the same trigger as the
/// ledger, which the migration attaches.
/// </summary>
public sealed class KillSwitchEventConfiguration : IEntityTypeConfiguration<KillSwitchEvent>
{
    public const int MaxReasonLength = 500;

    public void Configure(EntityTypeBuilder<KillSwitchEvent> builder)
    {
        // A flip has to say why. An engine stopped for no stated reason is one nobody dares start
        // again, and the next operator to read this table is often the same one, a week later.
        builder.ToTable("kill_switch", table => table.HasCheckConstraint(
            "ck_kill_switch_reason_is_not_blank", "btrim(reason) <> ''"));

        builder.HasKey(flip => flip.Id);
        builder.Property(flip => flip.Id).UseIdentityAlwaysColumn();

        builder.Property(flip => flip.Reason).HasMaxLength(MaxReasonLength);

        builder.Property(flip => flip.ChangedAt)
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        builder.Property(flip => flip.ChangedBy)
            .HasMaxLength(64)
            .HasDefaultValueSql("current_user")
            .ValueGeneratedOnAdd();
    }
}
