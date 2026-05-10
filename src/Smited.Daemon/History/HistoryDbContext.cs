using Microsoft.EntityFrameworkCore;

namespace Smited.Daemon.History;

/// <summary>
/// EF Core <see cref="DbContext"/> for smited's history database. Records
/// every trigger, stop, panic, and lifecycle event so post-hoc queries
/// (&quot;what fired in the last hour?&quot;, &quot;show me every panic from
/// the past week&quot;) can be answered without parsing log files.
/// </summary>
/// <remarks>
/// The database is daemon-internal: no entity is exposed through gRPC and
/// no public types reference these records. A future Razor or Blazor
/// admin UI hosted on a separate HTTP port is the planned consumer.
///
/// History writes are best-effort. <see cref="HistoryRecorder"/> swallows
/// database failures after logging, so a corrupt or locked database
/// never blocks the daemon's primary job (firing haptics).
/// </remarks>
internal sealed class HistoryDbContext : DbContext
{
    public HistoryDbContext(DbContextOptions<HistoryDbContext> options) : base(options) { }

    public DbSet<TriggerRecord> Triggers => Set<TriggerRecord>();
    public DbSet<StopRecord> Stops => Set<StopRecord>();
    public DbSet<PanicRecord> Panics => Set<PanicRecord>();
    public DbSet<LifecycleRecord> Lifecycle => Set<LifecycleRecord>();
    public DbSet<BackendStateRecord> BackendStates => Set<BackendStateRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TriggerRecord>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Timestamp);
            e.HasIndex(r => r.BackendId);
            e.HasIndex(r => r.Accepted);
        });

        b.Entity<StopRecord>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Timestamp);
            e.HasIndex(r => r.Source);
        });

        b.Entity<PanicRecord>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Timestamp);
        });

        b.Entity<LifecycleRecord>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Timestamp);
            e.HasIndex(r => r.BackendId);
            e.HasIndex(r => r.EventKind);
        });

        b.Entity<BackendStateRecord>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Timestamp);
            e.HasIndex(r => r.BackendId);
        });
    }
}
