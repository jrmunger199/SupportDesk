using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SupportDesk.Domain;

namespace SupportDesk.Data;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.Property(t => t.Name).HasMaxLength(120);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.Email).HasMaxLength(256);
        builder.Property(u => u.DisplayName).HasMaxLength(120);
        builder.Property(u => u.PasswordHash).HasMaxLength(512);
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);

        builder.HasIndex(u => u.Email).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.Property(t => t.Title).HasMaxLength(200);
        builder.Property(t => t.Description).HasMaxLength(10_000);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        // Priority stays numeric so ORDER BY priority sorts by severity, not alphabetically.
        builder.Property(t => t.Priority).HasConversion<int>();
        builder.Property(t => t.RowVersion).IsRowVersion();

        builder.HasIndex(t => new { t.TenantId, t.Number }).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.Status, t.UpdatedUtc });
        builder.HasIndex(t => new { t.TenantId, t.RequesterId });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.Requester).WithMany().HasForeignKey(t => t.RequesterId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.Assignee).WithMany().HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(t => t.Comments).WithOne().HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.Events).WithOne().HasForeignKey(e => e.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.Property(c => c.Body).HasMaxLength(4_000);
        builder.HasIndex(c => new { c.TicketId, c.CreatedUtc });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.Author).WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TicketEventConfiguration : IEntityTypeConfiguration<TicketEvent>
{
    public void Configure(EntityTypeBuilder<TicketEvent> builder)
    {
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(24);
        builder.Property(e => e.Detail).HasMaxLength(500);
        builder.HasIndex(e => new { e.TicketId, e.CreatedUtc });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Actor).WithMany().HasForeignKey(e => e.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TicketMentionConfiguration : IEntityTypeConfiguration<TicketMention>
{
    public void Configure(EntityTypeBuilder<TicketMention> builder)
    {
        builder.Property(m => m.Snippet).HasMaxLength(200);

        // The inbox is "my mentions, newest first, unread first": this index serves both the list and the unread count.
        builder.HasIndex(m => new { m.UserId, m.IsRead, m.CreatedUtc });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(m => m.Ticket).WithMany().HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);

        // SQL Server forbids a second cascade path (Ticket -> Comment -> Mention and Ticket -> Mention).
        builder.HasOne<Comment>().WithMany().HasForeignKey(m => m.CommentId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(m => m.Actor).WithMany().HasForeignKey(m => m.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
}
