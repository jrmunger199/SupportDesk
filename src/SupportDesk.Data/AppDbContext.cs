using Microsoft.EntityFrameworkCore;

using SupportDesk.Domain;

namespace SupportDesk.Data;

/// <summary>
/// Single shared database; tenants are separated by a TenantId column. Isolation is enforced in two layers:
/// global query filters hide other tenants' rows on read, and SaveChanges refuses cross-tenant writes.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant, TimeProvider clock)
    : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();

    public DbSet<TicketMention> TicketMentions => Set<TicketMention>();

    /// <summary>
    /// Read by the query filters, and re-evaluated per query because it is a property of the context instance.
    /// With no tenant it is Guid.Empty, which matches no rows: the filters fail closed.
    /// </summary>
    private Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQL Server hands back DateTime with Kind=Unspecified, which JSON would write without a "Z" and browsers
        // would then read as local time. Everything we store is UTC, so say so on the way out.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.Entity<User>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Ticket>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Comment>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TicketEvent>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TicketMention>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAndGuard();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAndGuard();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void StampAndGuard()
    {
        var now = clock.GetUtcNow().UtcDateTime;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is ITenantOwned owned && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                GuardTenant(owned, entry.State);
            }

            switch (entry.Entity, entry.State)
            {
                case (Tenant t, EntityState.Added):
                    t.CreatedUtc = now;
                    break;
                case (User u, EntityState.Added):
                    u.CreatedUtc = now;
                    break;
                case (Comment c, EntityState.Added):
                    c.CreatedUtc = now;
                    break;
                case (TicketMention m, EntityState.Added):
                    m.CreatedUtc = now;
                    break;
                case (TicketEvent e, EntityState.Added):
                    e.CreatedUtc = now;
                    break;
                case (Ticket t, EntityState.Added):
                    t.CreatedUtc = now;
                    t.UpdatedUtc = now;
                    break;
                case (Ticket t, EntityState.Modified):
                    t.UpdatedUtc = now;
                    break;
            }
        }
    }

    private void GuardTenant(ITenantOwned owned, EntityState state)
    {
        if (tenant.TenantId is { } current)
        {
            if (owned.TenantId == Guid.Empty && state == EntityState.Added)
            {
                owned.TenantId = current;
            }
            else if (owned.TenantId != current)
            {
                throw new InvalidOperationException(
                    $"Refusing to save a {owned.GetType().Name} that belongs to another tenant.");
            }
        }
        else if (owned.TenantId == Guid.Empty)
        {
            // System work (registration, seeding) has no ambient tenant, so the caller must name one explicitly.
            throw new InvalidOperationException(
                $"No tenant in context and {owned.GetType().Name}.TenantId was not set.");
        }
    }
}
