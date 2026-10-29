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
    public DbSet<BulkCodeJob> BulkCodeJobs => Set<BulkCodeJob>();
    public DbSet<BulkCodeBatch> BulkCodeBatches => Set<BulkCodeBatch>();
    public DbSet<BulkShipmentCheck> BulkShipmentChecks => Set<BulkShipmentCheck>();
    public DbSet<BulkShipmentCheckProgressEntity> BulkShipmentCheckProgresses => Set<BulkShipmentCheckProgressEntity>();
    public DbSet<BulkShipmentCheckCounter> BulkShipmentCheckCounters => Set<BulkShipmentCheckCounter>();

    public DbSet<SuzUtilisationJob> SuzUtilisationJobs => Set<SuzUtilisationJob>();
    public DbSet<SuzUtilisationReport> SuzUtilisationReports => Set<SuzUtilisationReport>();

    internal static void ConfigureSuzUtilisation(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SuzUtilisationJob>(entity =>
        {
            entity.ToTable("SuzUtilisationJobs");
            entity.Property(x => x.Id).HasColumnType("TEXT");
            entity.Property(x => x.ProfileId).HasColumnType("TEXT");
            entity.Property(x => x.Mode).HasColumnType("INTEGER");
            foreach (var name in new[] { nameof(SuzUtilisationJob.ProductGroup), nameof(SuzUtilisationJob.PackageType), nameof(SuzUtilisationJob.Scenario), nameof(SuzUtilisationJob.OmsId), nameof(SuzUtilisationJob.ConnectionId), nameof(SuzUtilisationJob.BaseUrl), nameof(SuzUtilisationJob.CertificateThumbprint), nameof(SuzUtilisationJob.CorrelationId) })
                entity.Property<string>(name).IsRequired().HasColumnType("TEXT");
            entity.Property(x => x.CodeCount).HasColumnType("INTEGER");
            entity.Property(x => x.ReportCount).HasColumnType("INTEGER");
            entity.Property(x => x.CreatedAtUnixMs).HasColumnType("INTEGER");
            entity.Property(x => x.SubmissionOwner).HasColumnType("TEXT");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ProfileId, x.Mode, x.CreatedAtUnixMs });
            entity.HasOne<CrptProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SuzUtilisationReport>(entity =>
        {
            entity.ToTable("SuzUtilisationReports");
            entity.Property(x => x.Id).HasColumnType("TEXT");
            entity.Property(x => x.JobId).HasColumnType("TEXT");
            entity.Property(x => x.Number).HasColumnType("INTEGER");
            entity.Property(x => x.CodeCount).HasColumnType("INTEGER");
            entity.Property(x => x.ProtectedPayload).IsRequired().HasColumnType("TEXT");
            entity.Property(x => x.State).IsRequired().HasColumnType("TEXT");
            foreach (var name in new[] { nameof(SuzUtilisationReport.ReportId), nameof(SuzUtilisationReport.RemoteStatus), nameof(SuzUtilisationReport.ReceiptState), nameof(SuzUtilisationReport.Error) })
                entity.Property<string?>(name).HasColumnType("TEXT");
            foreach (var name in new[] { nameof(SuzUtilisationReport.SubmittedAtUnixMs), nameof(SuzUtilisationReport.LastCheckedAtUnixMs), nameof(SuzUtilisationReport.NextCheckAtUnixMs), nameof(SuzUtilisationReport.TrackingUntilUnixMs), nameof(SuzUtilisationReport.LeaseUntilUnixMs) })
                entity.Property<long?>(name).HasColumnType("INTEGER");
            entity.Property(x => x.TrackingOwner).HasColumnType("TEXT");
            entity.Property(x => x.PollCount).HasColumnType("INTEGER");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.JobId, x.Number }).IsUnique();
            entity.HasIndex(x => x.NextCheckAtUnixMs);
            entity.HasOne<SuzUtilisationJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureSuzUtilisation(modelBuilder);
        ConfigureBulkShipmentChecks(modelBuilder);
        modelBuilder.Entity<CrptProfile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Inn).HasMaxLength(12);
            entity.Property(x => x.Kpp).HasMaxLength(9);
            entity.Property(x => x.FiasId).HasMaxLength(36);
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
        modelBuilder.Entity<BulkCodeJob>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ProfileId);
            entity.HasIndex(x => x.State);
            entity.HasIndex(x => x.CreatedAtUnixMs);
            entity.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            entity.Property(x => x.State).HasMaxLength(32).IsRequired();
            entity.HasOne<CrptProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<BulkCodeBatch>(entity =>
        {
            entity.HasKey(x => new { x.JobId, x.Number });
            entity.HasIndex(x => new { x.JobId, x.State });
            entity.Property(x => x.State).HasMaxLength(32).IsRequired();
            entity.HasOne<BulkCodeJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    internal static void ConfigureBulkShipmentChecks(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BulkShipmentCheck>(entity =>
        {
            entity.ToTable("BulkShipmentChecks");
            entity.HasKey(x => new { x.ProfileId, x.JobId, x.Mode, x.Inn, x.Code });
            entity.Property(x => x.ProfileId).HasColumnType("TEXT");
            entity.Property(x => x.JobId).HasColumnType("TEXT");
            entity.Property(x => x.Mode).HasColumnType("INTEGER");
            entity.Property(x => x.Inn).HasColumnType("TEXT").HasMaxLength(12).IsRequired();
            entity.Property(x => x.Code).HasColumnType("TEXT").HasMaxLength(74).IsRequired();
            entity.Property(x => x.State).HasColumnType("INTEGER");
            entity.Property(x => x.CheckedAt).HasColumnType("TEXT");
            entity.Property(x => x.Reason).HasColumnType("TEXT").HasMaxLength(256);
            entity.HasOne<BulkCodeJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<BulkShipmentCheckProgressEntity>(entity =>
        {
            entity.ToTable("BulkShipmentCheckProgresses");
            entity.HasKey(x => new { x.ProfileId, x.JobId, x.Mode, x.Inn });
            entity.Property(x => x.ProfileId).HasColumnType("TEXT");
            entity.Property(x => x.JobId).HasColumnType("TEXT");
            entity.Property(x => x.Mode).HasColumnType("INTEGER");
            entity.Property(x => x.Inn).HasColumnType("TEXT").HasMaxLength(12).IsRequired();
            entity.Property(x => x.NextBatchNumber).HasColumnType("INTEGER");
            entity.Property(x => x.ByteOffset).HasColumnType("INTEGER");
            entity.Property(x => x.Completed).HasColumnType("INTEGER");
            entity.Property(x => x.ReviewedRows).HasColumnType("INTEGER");
            entity.HasOne<BulkCodeJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<BulkShipmentCheckCounter>(entity =>
        {
            entity.ToTable("BulkShipmentCheckCounters");
            entity.HasKey(x => new { x.ProfileId, x.JobId, x.Mode, x.Inn, x.State, x.Status });
            entity.Property(x => x.ProfileId).HasColumnType("TEXT");
            entity.Property(x => x.JobId).HasColumnType("TEXT");
            entity.Property(x => x.Mode).HasColumnType("INTEGER");
            entity.Property(x => x.Inn).HasColumnType("TEXT").HasMaxLength(12).IsRequired();
            entity.Property(x => x.State).HasColumnType("INTEGER");
            entity.Property(x => x.Status).HasColumnType("TEXT").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Count).HasColumnType("INTEGER");
            entity.HasOne<BulkCodeJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
