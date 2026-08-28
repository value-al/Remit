using Microsoft.EntityFrameworkCore;
using Remit.Funding.Deposits;
using Remit.Funding.Withdrawals;

namespace Remit.Funding.Persistence;

/// <summary>
/// One schema per service (<c>funding</c>); no other service reads these tables (ADR-0005).
/// Mapping is explicit rather than convention-driven so the schema is readable from here.
/// </summary>
public sealed class FundingDbContext(DbContextOptions<FundingDbContext> options) : DbContext(options)
{
    public const string Schema = "funding";

    public DbSet<Deposit> Deposits => Set<Deposit>();
    public DbSet<Withdrawal> Withdrawals => Set<Withdrawal>();
    public DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();
    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Deposit>(b =>
        {
            b.ToTable("deposits");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(d => d.AccountId).HasColumnName("account_id");
            b.Property(d => d.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            b.Property(d => d.RequestedAt).HasColumnName("requested_at");
            b.Property(d => d.Provider).HasColumnName("provider").HasMaxLength(64);
            b.Property(d => d.PspReference).HasColumnName("psp_reference").HasMaxLength(128);
            b.Property(d => d.FailureReason).HasColumnName("failure_reason").HasMaxLength(512);
            b.HasIndex(d => new { d.AccountId, d.RequestedAt });

            b.ComplexProperty(d => d.Amount, m =>
            {
                m.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 4);
                m.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3);
            });

            b.OwnsMany(d => d.History, h =>
            {
                h.ToTable("deposit_transitions");
                h.WithOwner().HasForeignKey("deposit_id");
                h.Property<int>("id").ValueGeneratedOnAdd();
                h.HasKey("id");
                h.Property(t => t.From).HasColumnName("from_status").HasConversion<string>().HasMaxLength(32);
                h.Property(t => t.To).HasColumnName("to_status").HasConversion<string>().HasMaxLength(32);
                h.Property(t => t.At).HasColumnName("at");
            });
            b.Navigation(d => d.History).HasField("_history").UsePropertyAccessMode(PropertyAccessMode.Field);

            // Optimistic concurrency on PostgreSQL's system column: two handlers racing to
            // move the same deposit cannot both win.
            b.Property<uint>("xmin").IsRowVersion();
        });

        modelBuilder.Entity<Withdrawal>(b =>
        {
            b.ToTable("withdrawals");
            b.HasKey(w => w.Id);
            b.Property(w => w.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(w => w.AccountId).HasColumnName("account_id");
            b.Property(w => w.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            b.Property(w => w.RequestedAt).HasColumnName("requested_at");
            b.Property(w => w.Provider).HasColumnName("provider").HasMaxLength(64);
            b.Property(w => w.PspReference).HasColumnName("psp_reference").HasMaxLength(128);
            b.Property(w => w.FailureReason).HasColumnName("failure_reason").HasMaxLength(512);
            b.HasIndex(w => new { w.AccountId, w.RequestedAt });

            b.ComplexProperty(w => w.Amount, m =>
            {
                m.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 4);
                m.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3);
            });

            b.OwnsMany(w => w.History, h =>
            {
                h.ToTable("withdrawal_transitions");
                h.WithOwner().HasForeignKey("withdrawal_id");
                h.Property<int>("id").ValueGeneratedOnAdd();
                h.HasKey("id");
                h.Property(t => t.From).HasColumnName("from_status").HasConversion<string>().HasMaxLength(32);
                h.Property(t => t.To).HasColumnName("to_status").HasConversion<string>().HasMaxLength(32);
                h.Property(t => t.At).HasColumnName("at");
            });
            b.Navigation(w => w.History).HasField("_history").UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Property<uint>("xmin").IsRowVersion();
        });

        modelBuilder.Entity<OutboxRecord>(b =>
        {
            b.ToTable("outbox");
            b.HasKey(o => o.Id);
            b.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(o => o.Type).HasColumnName("type").HasMaxLength(128);
            b.Property(o => o.Payload).HasColumnName("payload").HasColumnType("jsonb");
            b.Property(o => o.OccurredAt).HasColumnName("occurred_at");
            b.Property(o => o.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            b.Property(o => o.SentAt).HasColumnName("sent_at");
            b.Property(o => o.Attempts).HasColumnName("attempts");
            b.Property(o => o.LastError).HasColumnName("last_error").HasMaxLength(1024);
            // The relay's scan: unsent rows in occurrence order.
            b.HasIndex(o => o.OccurredAt).HasFilter("sent_at IS NULL");
        });

        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.ToTable("idempotency_keys");
            b.HasKey(i => i.Key);
            b.Property(i => i.Key).HasColumnName("key").HasMaxLength(200);
            b.Property(i => i.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
            b.Property(i => i.StatusCode).HasColumnName("status_code");
            b.Property(i => i.ContentType).HasColumnName("content_type").HasMaxLength(128);
            b.Property(i => i.Body).HasColumnName("body");
            b.Property(i => i.ClaimedAt).HasColumnName("claimed_at");
            b.Property(i => i.CompletedAt).HasColumnName("completed_at");
        });
    }
}
