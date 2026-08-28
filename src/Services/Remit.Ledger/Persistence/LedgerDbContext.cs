using Microsoft.EntityFrameworkCore;

namespace Remit.Ledger.Persistence;

/// <summary>A persisted journal entry. Built from the domain <see cref="JournalEntry"/>, which has already proven it balances.</summary>
public sealed class JournalEntryRecord
{
    private JournalEntryRecord()
    {
    }

    public Guid Id { get; private set; }
    public string Description { get; private set; } = default!;
    public DateTimeOffset PostedAt { get; private set; }
    public string? CorrelationId { get; private set; }
    public List<PostingRecord> Postings { get; private set; } = [];

    public static JournalEntryRecord From(JournalEntry entry, string? correlationId) => new()
    {
        Id = entry.Id,
        Description = entry.Description,
        PostedAt = entry.PostedAt,
        CorrelationId = correlationId,
        Postings = entry.Postings.Select(p => new PostingRecord(p.Account, p.Amount.Amount, p.Amount.Currency, p.Side)).ToList(),
    };
}

public sealed class PostingRecord(string account, decimal amount, string currency, Side side)
{
    public long Id { get; private set; }
    public Guid EntryId { get; private set; }
    public string Account { get; private set; } = account;
    public decimal Amount { get; private set; } = amount;
    public string Currency { get; private set; } = currency;
    public Side Side { get; private set; } = side;

    /// <summary>Credit-normal signed amount: credits add, debits subtract. Wallets are liabilities.</summary>
    public decimal CreditSigned => Side == Side.Credit ? Amount : -Amount;
}

/// <summary>A consumed message id. Its primary key is the idempotency guarantee (ADR-0007).</summary>
public sealed class InboxRecord
{
    private InboxRecord()
    {
    }

    public Guid MessageId { get; private set; }
    public string Type { get; private set; } = default!;
    public DateTimeOffset ProcessedAt { get; private set; }

    public static InboxRecord For(Guid messageId, string type, DateTimeOffset at) => new() { MessageId = messageId, Type = type, ProcessedAt = at };
}

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public const string Schema = "ledger";

    public DbSet<JournalEntryRecord> Entries => Set<JournalEntryRecord>();
    public DbSet<PostingRecord> Postings => Set<PostingRecord>();
    public DbSet<InboxRecord> Inbox => Set<InboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<JournalEntryRecord>(b =>
        {
            b.ToTable("journal_entries");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.Description).HasColumnName("description").HasMaxLength(256);
            b.Property(e => e.PostedAt).HasColumnName("posted_at");
            b.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            b.HasIndex(e => e.CorrelationId);
            b.HasMany(e => e.Postings).WithOne().HasForeignKey(p => p.EntryId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(e => e.Postings).AutoInclude();
        });

        modelBuilder.Entity<PostingRecord>(b =>
        {
            b.ToTable("postings");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(p => p.EntryId).HasColumnName("entry_id");
            b.Property(p => p.Account).HasColumnName("account").HasMaxLength(128);
            b.Property(p => p.Amount).HasColumnName("amount").HasPrecision(18, 4);
            b.Property(p => p.Currency).HasColumnName("currency").HasMaxLength(3);
            b.Property(p => p.Side).HasColumnName("side").HasConversion<string>().HasMaxLength(6);
            b.Ignore(p => p.CreditSigned);
            // The balance query: all postings of one account in one currency.
            b.HasIndex(p => new { p.Account, p.Currency });
        });

        modelBuilder.Entity<InboxRecord>(b =>
        {
            b.ToTable("inbox");
            b.HasKey(i => i.MessageId);
            b.Property(i => i.MessageId).HasColumnName("message_id").ValueGeneratedNever();
            b.Property(i => i.Type).HasColumnName("type").HasMaxLength(128);
            b.Property(i => i.ProcessedAt).HasColumnName("processed_at");
        });
    }
}
