using CrptWorkbench.Domain;
using Microsoft.EntityFrameworkCore;

namespace CrptWorkbench.Infrastructure;

public sealed class ProtectedSecret
{
    public string Key { get; set; } = string.Empty;
    public string ProtectedValue { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkbenchDbContext(DbContextOptions<WorkbenchDbContext> options) : DbContext(options)
{
    public DbSet<CrptProfile> Profiles => Set<CrptProfile>();
    public DbSet<ApiRequestLog> ApiRequestLogs => Set<ApiRequestLog>();
    public DbSet<ProtectedSecret> ProtectedSecrets => Set<ProtectedSecret>();
    public DbSet<FavoriteApiOperation> FavoriteApiOperations => Set<FavoriteApiOperation>();
    public DbSet<PackageDocument> PackageDocuments => Set<PackageDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CrptProfile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Inn).HasMaxLength(12);
            entity.Property(x => x.CertificateThumbprint).HasMaxLength(128);
            entity.Property(x => x.TraceXDatabaseProvider).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<ApiRequestLog>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CreatedAtUnixMs);
            entity.HasIndex(x => x.CorrelationId);
            entity.Property(x => x.OperationId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<ProtectedSecret>(entity =>
        {
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(300);
            entity.Property(x => x.ProtectedValue).IsRequired();
        });

        modelBuilder.Entity<FavoriteApiOperation>(entity =>
        {
            entity.HasKey(x => new { x.ProfileId, x.OperationId });
            entity.Property(x => x.OperationId).HasMaxLength(200);
            entity.HasOne<CrptProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PackageDocument>(entity =>
        {
            entity.ToTable("PackageTransformationDocuments");
            entity.Property(x => x.OperationKind).HasDefaultValue(PackageOperationKind.Transformation);
            entity.Property(x => x.PackageType).HasMaxLength(16);
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ProfileId);
            entity.HasIndex(x => x.NextCheckAtUnixMs);
            entity.Property(x => x.ProductGroup).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PackageCode).HasMaxLength(74).IsRequired();
            entity.Property(x => x.DocumentHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.State).HasMaxLength(32).IsRequired();
            entity.Property(x => x.DocumentId).HasMaxLength(256);
            entity.Property(x => x.RemoteStatus).HasMaxLength(64);
        });
    }
}
