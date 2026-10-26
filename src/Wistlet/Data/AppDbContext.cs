using Microsoft.EntityFrameworkCore;
using Wistlet.Models;

namespace Wistlet.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<PostTag> PostTags => Set<PostTag>();
    public DbSet<Like> Likes => Set<Like>();
    public DbSet<Bookmark> Bookmarks => Set<Bookmark>();
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(u => u.NormalizedUserName).IsUnique();
            e.HasIndex(u => u.Email);
            e.Ignore(u => u.IsStaff);
            e.Ignore(u => u.IsCurrentlyBanned);
        });

        b.Entity<Post>(e =>
        {
            e.HasOne(p => p.Author).WithMany(u => u.Posts).HasForeignKey(p => p.AuthorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.Parent).WithMany(p => p.Replies).HasForeignKey(p => p.ParentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.SharedPost).WithMany().HasForeignKey(p => p.SharedPostId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(p => p.SharedPostId);
            e.HasIndex(p => p.CreatedAt);
            e.HasIndex(p => new { p.AuthorId, p.CreatedAt });
            e.HasIndex(p => p.ParentId);
            e.HasIndex(p => p.RootId);
        });

        b.Entity<PostTag>(e =>
        {
            e.HasKey(t => new { t.PostId, t.Tag });
            e.HasIndex(t => t.Tag);
            e.HasOne(t => t.Post).WithMany(p => p.Tags).HasForeignKey(t => t.PostId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Like>(e =>
        {
            e.HasKey(l => new { l.UserId, l.PostId });
            e.HasIndex(l => l.PostId);
            e.HasOne(l => l.Post).WithMany(p => p.Likes).HasForeignKey(l => l.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.User).WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Bookmark>(e =>
        {
            e.HasKey(x => new { x.UserId, x.PostId });
            e.HasOne(x => x.Post).WithMany(p => p.Bookmarks).HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Follow>(e =>
        {
            e.HasKey(f => new { f.FollowerId, f.FolloweeId });
            e.HasIndex(f => f.FolloweeId);
            e.HasOne(f => f.Follower).WithMany().HasForeignKey(f => f.FollowerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(f => f.Followee).WithMany().HasForeignKey(f => f.FolloweeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Block>(e =>
        {
            e.HasKey(x => new { x.BlockerId, x.BlockedId });
            e.HasOne(x => x.Blocker).WithMany().HasForeignKey(x => x.BlockerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Blocked).WithMany().HasForeignKey(x => x.BlockedId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Notification>(e =>
        {
            e.HasIndex(n => new { n.UserId, n.IsRead });
            e.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(n => n.Actor).WithMany().HasForeignKey(n => n.ActorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(n => n.Post).WithMany().HasForeignKey(n => n.PostId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Message>(e =>
        {
            e.HasIndex(m => new { m.SenderId, m.RecipientId });
            e.HasIndex(m => new { m.RecipientId, m.ReadAt });
            e.HasOne(m => m.Sender).WithMany().HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Recipient).WithMany().HasForeignKey(m => m.RecipientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Report>(e =>
        {
            e.HasIndex(r => r.Status);
            e.HasOne(r => r.Reporter).WithMany().HasForeignKey(r => r.ReporterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Post).WithMany().HasForeignKey(r => r.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.TargetUser).WithMany().HasForeignKey(r => r.TargetUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.ResolvedBy).WithMany().HasForeignKey(r => r.ResolvedById).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(a => a.CreatedAt);
            e.HasOne(a => a.Actor).WithMany().HasForeignKey(a => a.ActorId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
