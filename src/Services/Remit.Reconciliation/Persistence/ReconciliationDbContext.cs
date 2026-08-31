using Microsoft.EntityFrameworkCore;
using Remit.Reconciliation.Matching;

namespace Remit.Reconciliation.Persistence;

/// <summary>Our copy of what Funding said about a movement, built from its events (ADR-0009).</summary>
public sealed class MovementRecord
{
    private MovementRecord()
    {
    }

    public Guid Id { get; private set; }
    public MovementKind Kind { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = default!;
    public string Status { get; private set; } = default!;
    public string? Provider { get; private set; }
    public string? Reference { get; private set; }
    public DateTimeOffset FirstEventAt { get; private set; }
    public DateTimeOffset LastEventAt { get; private set; }

    public static MovementRecord Start(Guid id, MovementKind kind, Guid accountId, decimal amount, string currency, string status, string? provider, string? reference, DateTimeOffset at) =>
        new() { Id = id, Kind = kind, AccountId = accountId, Amount = amount, Currency = currency, Status = status, Provider = provider, Reference = reference, FirstEventAt = at, LastEventAt = at };

    public void Apply(string status, string? provider, string? reference, DateTimeOffset at)
    {
        Status = status;
        Provider ??= provider;
        Reference ??= reference;
        LastEventAt = at > LastEventAt ? at : LastEventAt;
    }

    public ExpectedMovement ToExpected() =>
        new(Id, Provider ?? string.Empty, Reference ?? string.Empty, Kind, BuildingBlocks.Money.Of(Amount, Currency), Status, LastEventAt);
}

public sealed class StatementRecord
{
    private StatementRecord()
    {
    }

    public Guid Id { get; private set; }
    public string Provider { get; private set; } = default!;
    public DateTimeOffset PeriodStart { get; private set; }
    public DateTimeOffset PeriodEnd { get; private set; }
    public int Lines { get; private set; }
    public int Matched { get; private set; }
    public int Exceptions { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }

    public static StatementRecord Create(string provider, DateTimeOffset periodStart, DateTimeOffset periodEnd, int lines, int matched, int exceptions, DateTimeOffset at) =>
        new() { Id = Guid.NewGuid(), Provider = provider, PeriodStart = periodStart, PeriodEnd = periodEnd, Lines = lines, Matched = matched, Exceptions = exceptions, ReceivedAt = at };
}

/// <summary>A difference somebody has to look at. Never auto-resolved (ADR-0001).</summary>
public sealed class ExceptionRecord
{
    private ExceptionRecord()
    {
    }

    public Guid Id { get; private set; }
    public string Kind { get; private set; } = default!;
    public string Provider { get; private set; } = default!;
    public string? Reference { get; private set; }
    public Guid? MovementId { get; private set; }
    public Guid? StatementId { get; private set; }
    public string Detail { get; private set; } = default!;
    public DateTimeOffset RaisedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? Resolution { get; private set; }

    public bool IsOpen => ResolvedAt is null;

    public static ExceptionRecord Raise(string kind, string provider, string? reference, Guid? movementId, Guid? statementId, string detail, DateTimeOffset at) =>
        new() { Id = Guid.NewGuid(), Kind = kind, Provider = provider, Reference = reference, MovementId = movementId, StatementId = statementId, Detail = detail, RaisedAt = at };

    public void Resolve(string resolution, DateTimeOffset at)
    {
        Resolution = resolution;
        ResolvedAt = at;
    }
}

public sealed class InboxRecord
{
    private InboxRecord()
    {
    }

    public Guid MessageId { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }

    public static InboxRecord For(Guid id, DateTimeOffset at) => new() { MessageId = id, ProcessedAt = at };
}

public sealed class ReconciliationDbContext(DbContextOptions<ReconciliationDbContext> options) : DbContext(options)
{
    public const string Schema = "reconciliation";

    public DbSet<MovementRecord> Movements => Set<MovementRecord>();
    public DbSet<StatementRecord> Statements => Set<StatementRecord>();
    public DbSet<ExceptionRecord> Exceptions => Set<ExceptionRecord>();
    public DbSet<InboxRecord> Inbox => Set<InboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<MovementRecord>(b =>
        {
            b.ToTable("movements");
            b.HasKey(m => m.Id);
            b.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(m => m.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
            b.Property(m => m.AccountId).HasColumnName("account_id");
            b.Property(m => m.Amount).HasColumnName("amount").HasPrecision(18, 4);
            b.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3);
            b.Property(m => m.Status).HasColumnName("status").HasMaxLength(32);
            b.Property(m => m.Provider).HasColumnName("provider").HasMaxLength(64);
            b.Property(m => m.Reference).HasColumnName("reference").HasMaxLength(128);
            b.Property(m => m.FirstEventAt).HasColumnName("first_event_at");
            b.Property(m => m.LastEventAt).HasColumnName("last_event_at");
            b.HasIndex(m => new { m.Provider, m.Reference });
            b.HasIndex(m => new { m.Status, m.FirstEventAt });
        });

        modelBuilder.Entity<StatementRecord>(b =>
        {
            b.ToTable("statements");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(s => s.Provider).HasColumnName("provider").HasMaxLength(64);
            b.Property(s => s.PeriodStart).HasColumnName("period_start");
            b.Property(s => s.PeriodEnd).HasColumnName("period_end");
            b.Property(s => s.Lines).HasColumnName("lines");
            b.Property(s => s.Matched).HasColumnName("matched");
            b.Property(s => s.Exceptions).HasColumnName("exceptions");
            b.Property(s => s.ReceivedAt).HasColumnName("received_at");
        });

        modelBuilder.Entity<ExceptionRecord>(b =>
        {
            b.ToTable("exceptions");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(32);
            b.Property(e => e.Provider).HasColumnName("provider").HasMaxLength(64);
            b.Property(e => e.Reference).HasColumnName("reference").HasMaxLength(128);
            b.Property(e => e.MovementId).HasColumnName("movement_id");
            b.Property(e => e.StatementId).HasColumnName("statement_id");
            b.Property(e => e.Detail).HasColumnName("detail").HasMaxLength(1024);
            b.Property(e => e.RaisedAt).HasColumnName("raised_at");
            b.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            b.Property(e => e.Resolution).HasColumnName("resolution").HasMaxLength(1024);
            b.Ignore(e => e.IsOpen);
            b.HasIndex(e => new { e.Provider, e.ResolvedAt });
            // One open exception per (kind, reference): re-uploading a statement must not duplicate it.
            b.HasIndex(e => new { e.Kind, e.Provider, e.Reference }).IsUnique().HasFilter("resolved_at IS NULL AND reference IS NOT NULL");
        });

        modelBuilder.Entity<InboxRecord>(b =>
        {
            b.ToTable("inbox");
            b.HasKey(i => i.MessageId);
            b.Property(i => i.MessageId).HasColumnName("message_id").ValueGeneratedNever();
            b.Property(i => i.ProcessedAt).HasColumnName("processed_at");
        });
    }
}
