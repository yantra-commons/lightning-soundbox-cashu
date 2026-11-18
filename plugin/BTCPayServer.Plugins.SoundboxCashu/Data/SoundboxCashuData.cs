using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.SoundboxCashu.Data;

public static class SoundboxCashuConstants
{
    /// <summary>
    /// Postgres schema for this plugin. Separate from the Lightning Soundbox
    /// plugin's schema on purpose: the two plugins share nothing but the
    /// store id, and either can be uninstalled without touching the other.
    /// </summary>
    public const string SchemaName = "BTCPayServer.Plugins.SoundboxCashu";

    /// <summary>Unit this plugin trades in. Lightning settles in sats.</summary>
    public const string Unit = "sat";

    /// <summary>
    /// Upper bound on proofs in one incoming payment. Well above what any
    /// real payment needs (a binary split of 2^21 sats is 21 proofs) while
    /// keeping one anonymous request from making us shovel a huge list at the
    /// mint.
    /// </summary>
    public const int MaxProofsPerPayment = 64;

    /// <summary>Hard cap on the POST body of an incoming payment.</summary>
    public const int MaxPaymentBodyBytes = 64 * 1024;

    /// <summary>
    /// How long an operation may sit mid-flight before recovery treats it as
    /// interrupted. Longer than any mint call we make (the melt timeout is 3
    /// minutes), so recovery never races a request that is still running.
    /// </summary>
    public static readonly TimeSpan InterruptedAfter = TimeSpan.FromMinutes(5);
}

/// <summary>Per-store configuration. One mint per store.</summary>
public class CashuStoreSettings
{
    [Key]
    [MaxLength(50)]
    public string StoreId { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>Normalised (see CashuMintClient.NormalizeMintUrl).</summary>
    [MaxLength(300)]
    public string MintUrl { get; set; } = string.Empty;

    /// <summary>
    /// This server's public base URL, captured from the merchant's own
    /// browser request when they save settings. The background settlement
    /// worker has no HTTP request to derive it from, and BTCPay needs it to
    /// create invoices; the payment request QR embeds it too.
    /// </summary>
    [MaxLength(300)]
    public string ServerUrl { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum CashuOperationKind
{
    /// <summary>A customer's ecash arriving through the payment request.</summary>
    Receive = 0,
    /// <summary>Held ecash handed to the merchant as a token.</summary>
    Export = 1
}

public enum CashuOperationState
{
    /// <summary>Receive: swap sent; outputs stored but not yet signed.</summary>
    Swapping = 0,
    /// <summary>Receive: ecash is ours, waiting to be paid out to Lightning.</summary>
    Received = 1,
    /// <summary>Receive: melt sent to pay the BTCPay invoice.</summary>
    Settling = 2,
    /// <summary>Receive: the BTCPay invoice was paid. Done.</summary>
    Settled = 3,
    /// <summary>The mint refused; nothing changed hands.</summary>
    Failed = 4,
    /// <summary>Export: token created and shown to the merchant.</summary>
    Exported = 5,
    /// <summary>Receive: its ecash left in an export rather than over Lightning.</summary>
    SweptToToken = 6
}

/// <summary>
/// One unit of work against the mint, and the audit trail the merchant sees.
/// Every proof row points back at the operation that created it, which is
/// what lets recovery finish an operation that was interrupted half-way.
/// </summary>
public class CashuOperation
{
    [Key]
    [MaxLength(40)]
    public string Id { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string StoreId { get; set; } = string.Empty;

    public CashuOperationKind Kind { get; set; }
    public CashuOperationState State { get; set; }

    [MaxLength(300)]
    public string MintUrl { get; set; } = string.Empty;

    /// <summary>Face value of the ecash received (or exported).</summary>
    public long Amount { get; set; }

    /// <summary>Mint input fee paid to receive it.</summary>
    public long ReceiveFee { get; set; }

    /// <summary>What was actually credited to the merchant on Lightning.</summary>
    public long? SettledAmount { get; set; }

    [MaxLength(100)]
    public string? InvoiceId { get; set; }

    [MaxLength(100)]
    public string? MeltQuoteId { get; set; }

    /// <summary>Export only: the token, so it can be shown again.</summary>
    public string? Token { get; set; }

    [MaxLength(500)]
    public string? LastError { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum CashuProofState
{
    /// <summary>Output sent to the mint; no signature yet, so C is null.</summary>
    Pending = 0,
    /// <summary>Spendable ecash held by this store.</summary>
    Unspent = 1,
    /// <summary>Committed as an input to an in-flight melt.</summary>
    Reserved = 2,
    /// <summary>Spent at the mint (melted or exported).</summary>
    Spent = 3
}

/// <summary>
/// One ecash note. Rows are created BEFORE the mint is asked to sign them --
/// with the secret, blinding factor and blinded message -- so a request that
/// dies after the mint signed can still be recovered with NUT-09 restore.
/// </summary>
public class CashuProofRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string StoreId { get; set; } = string.Empty;

    [MaxLength(300)]
    public string MintUrl { get; set; } = string.Empty;

    [MaxLength(70)]
    public string KeysetId { get; set; } = string.Empty;

    /// <summary>0 until signed for a NUT-08 blank output; the mint picks it.</summary>
    public long Amount { get; set; }

    [Required]
    [MaxLength(64)]
    public string Secret { get; set; } = string.Empty;

    [MaxLength(64)]
    public string BlindingFactor { get; set; } = string.Empty;

    [MaxLength(66)]
    public string BlindedMessage { get; set; } = string.Empty;

    /// <summary>Unblinded signature. Null while Pending.</summary>
    [MaxLength(66)]
    public string? C { get; set; }

    public CashuProofState State { get; set; }

    /// <summary>Operation whose outputs these are.</summary>
    [MaxLength(40)]
    public string CreatedByOperationId { get; set; } = string.Empty;

    /// <summary>Operation that spent (or is spending) this proof.</summary>
    [MaxLength(40)]
    public string? SpentByOperationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SoundboxCashuDbContext : DbContext
{
    public SoundboxCashuDbContext(DbContextOptions<SoundboxCashuDbContext> options) : base(options) { }

    public DbSet<CashuStoreSettings> Settings { get; set; } = null!;
    public DbSet<CashuOperation> Operations { get; set; } = null!;
    public DbSet<CashuProofRecord> Proofs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(SoundboxCashuConstants.SchemaName);

        modelBuilder.Entity<CashuOperation>(e =>
        {
            e.HasIndex(x => new { x.StoreId, x.CreatedAt });
            e.HasIndex(x => x.State);
        });

        modelBuilder.Entity<CashuProofRecord>(e =>
        {
            // A secret is the identity of a note; storing one twice would let
            // the same ecash be counted twice.
            e.HasIndex(x => x.Secret).IsUnique();
            e.HasIndex(x => new { x.StoreId, x.State });
            e.HasIndex(x => x.CreatedByOperationId);
            e.HasIndex(x => x.SpentByOperationId);
        });
    }
}

public class SoundboxCashuDbContextFactory : BaseDbContextFactory<SoundboxCashuDbContext>
{
    public SoundboxCashuDbContextFactory(IOptions<DatabaseOptions> options)
        : base(options, SoundboxCashuConstants.SchemaName)
    {
    }

    public override SoundboxCashuDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<SoundboxCashuDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new SoundboxCashuDbContext(builder.Options);
    }
}

/// <summary>Applies migrations at startup, before any hosted service runs.</summary>
public class SoundboxCashuMigrationRunner : IStartupTask
{
    private readonly SoundboxCashuDbContextFactory _factory;

    public SoundboxCashuMigrationRunner(SoundboxCashuDbContextFactory factory) => _factory = factory;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var ctx = _factory.CreateContext();
        await ctx.Database.MigrateAsync(cancellationToken);
    }
}
