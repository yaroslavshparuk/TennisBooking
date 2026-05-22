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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<BookingCancellationLinkEntity>()
            .HasIndex(x => new { x.ChatId, x.TelegramMessageId })
            .IsUnique();
    }
}
