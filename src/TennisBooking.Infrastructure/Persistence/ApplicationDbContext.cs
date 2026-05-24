namespace TennisBooking.DAL;

using Microsoft.EntityFrameworkCore;
using Models;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<UserConfig> UserConfigs { get; set; }
    public DbSet<BookingCancellationLinkEntity> BookingCancellationLinks { get; set; }
    public DbSet<TelegramPollingStateEntity> TelegramPollingStates { get; set; }
    public DbSet<TelegramChatEntity> TelegramChats { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<BookingCancellationLinkEntity>()
            .HasIndex(x => new { x.ChatId, x.TelegramMessageId })
            .IsUnique();
        modelBuilder.Entity<TelegramChatEntity>()
            .HasIndex(x => x.ChatId)
            .IsUnique();
        modelBuilder.Entity<TelegramChatEntity>()
            .HasIndex(x => x.IsActive)
            .IsUnique()
            .HasFilter("\"IsActive\" = true");
    }
}
