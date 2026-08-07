using Infocal.Calendar.Models;

namespace Infocal.Calendar.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<EventItem> Events => Set<EventItem>();
    public DbSet<CategoryEntity> Categories => Set<CategoryEntity>();
    public DbSet<CityEntity> Cities => Set<CityEntity>();
    public DbSet<TypeEntity> Types => Set<TypeEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Start);
            e.HasIndex(x => x.Category);
            e.HasIndex(x => x.City);
            e.HasIndex(x => new { x.City, x.Category });
            e.HasIndex(x => x.SourceUrl);
            e.HasIndex(x => new { x.SourceUrl, x.Start });
        });

        modelBuilder.Entity<CategoryEntity>(e =>
        {
            e.HasKey(x => x.Slug);
        });

        modelBuilder.Entity<CityEntity>(e =>
        {
            e.HasKey(x => x.Slug);
        });

        modelBuilder.Entity<TypeEntity>(e =>
        {
            e.HasKey(x => x.Slug);
            e.HasOne(x => x.Category)
                .WithMany()
                .HasForeignKey(x => x.CategorySlug)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
