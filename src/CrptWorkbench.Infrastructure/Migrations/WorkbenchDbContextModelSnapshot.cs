using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
partial class WorkbenchDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "10.0.11");

        modelBuilder.Entity("CrptWorkbench.Domain.ApiRequestLog", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT");
            b.Property<string>("CorrelationId").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("TEXT");
            b.Property<long>("CreatedAtUnixMs").HasColumnType("INTEGER");
            b.Property<long>("DurationMs").HasColumnType("INTEGER");
            b.Property<int>("Environment").HasColumnType("INTEGER");
            b.Property<string>("Error").HasColumnType("TEXT");
            b.Property<string>("ExchangesJson").HasColumnType("TEXT");
            b.Property<string>("HttpMethod").IsRequired().HasColumnType("TEXT");
            b.Property<string>("OperationId").IsRequired().HasMaxLength(200).HasColumnType("TEXT");
            b.Property<Guid>("ProfileId").HasColumnType("TEXT");
            b.Property<string>("RequestBody").HasColumnType("TEXT");
            b.Property<string>("RequestHeaders").HasColumnType("TEXT");
            b.Property<string>("ResponseBody").HasColumnType("TEXT");
            b.Property<string>("ResponseHeaders").HasColumnType("TEXT");
            b.Property<int?>("StatusCode").HasColumnType("INTEGER");
            b.Property<bool>("Success").HasColumnType("INTEGER");
            b.Property<int>("System").HasColumnType("INTEGER");
            b.Property<string>("Url").IsRequired().HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("CorrelationId");
            b.HasIndex("CreatedAtUnixMs");
            b.ToTable("ApiRequestLogs");
        });

        modelBuilder.Entity("CrptWorkbench.Domain.BulkCodeJob", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT");
            b.Property<long>("CodeErrors").HasColumnType("INTEGER");
            b.Property<long>("CodesCompleted").HasColumnType("INTEGER");
            b.Property<long>("CodesFound").HasColumnType("INTEGER");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("TEXT");
            b.Property<long>("CreatedAtUnixMs").HasColumnType("INTEGER");
            b.Property<string>("Error").HasColumnType("TEXT");
            b.Property<long>("FailedBatches").HasColumnType("INTEGER");
            b.Property<long>("FileBytes").HasColumnType("INTEGER");
            b.Property<string>("FileName").IsRequired().HasMaxLength(260).HasColumnType("TEXT");
            b.Property<long>("LinesRead").HasColumnType("INTEGER");
            b.Property<int>("Mode").HasColumnType("INTEGER");
            b.Property<long>("NextBatchNumber").HasColumnType("INTEGER");
            b.Property<long>("NextOffset").HasColumnType("INTEGER");
            b.Property<Guid>("ProfileId").HasColumnType("TEXT");
            b.Property<string>("State").IsRequired().HasMaxLength(32).HasColumnType("TEXT");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("ProfileId");
            b.HasIndex("State");
            b.HasIndex("CreatedAtUnixMs");
            b.ToTable("BulkCodeJobs");
        });

        modelBuilder.Entity("CrptWorkbench.Domain.BulkCodeBatch", b =>
        {
            b.Property<Guid>("JobId").HasColumnType("TEXT");
            b.Property<long>("Number").HasColumnType("INTEGER");
            b.Property<int>("CodeCount").HasColumnType("INTEGER");
            b.Property<long>("EndOffset").HasColumnType("INTEGER");
            b.Property<string>("Error").HasColumnType("TEXT");
            b.Property<long>("StartOffset").HasColumnType("INTEGER");
            b.Property<string>("State").IsRequired().HasMaxLength(32).HasColumnType("TEXT");
            b.HasKey("JobId", "Number");
            b.HasIndex("JobId", "State");
            b.ToTable("BulkCodeBatches");
        });

        modelBuilder.Entity("CrptWorkbench.Domain.CrptProfile", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT");
            b.Property<string>("CertificateThumbprint").HasMaxLength(128).HasColumnType("TEXT");
            b.Property<string>("Inn").HasMaxLength(12).HasColumnType("TEXT");
            b.Property<string>("Kpp").HasMaxLength(9).HasColumnType("TEXT");
            b.Property<string>("FiasId").HasMaxLength(36).HasColumnType("TEXT");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("TEXT");
            b.Property<string>("NationalCatalogProductionBaseUrl").HasColumnType("TEXT");
            b.Property<string>("SuzConnectionId").HasColumnType("TEXT");
            b.Property<string>("SuzOmsId").HasColumnType("TEXT");
            b.Property<string>("SuzProductionBaseUrl").HasColumnType("TEXT");
            b.Property<string>("TraceXDatabaseProvider").IsRequired().HasMaxLength(32).HasColumnType("TEXT");
            b.Property<string>("TrueApiProductionBaseUrl").HasColumnType("TEXT");
            b.HasKey("Id");
            b.ToTable("Profiles");
        });

        modelBuilder.Entity("CrptWorkbench.Domain.FavoriteApiOperation", b =>
        {
            b.Property<Guid>("ProfileId").HasColumnType("TEXT");
            b.Property<string>("OperationId").HasMaxLength(200).HasColumnType("TEXT");
            b.HasKey("ProfileId", "OperationId");
            b.ToTable("FavoriteApiOperations");
        });

        modelBuilder.Entity("CrptWorkbench.Domain.PackageDocument", b =>
        {
            b.Property<int>("OperationKind").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasDefaultValue(0);
            b.Property<string>("PackageType").HasMaxLength(16).HasColumnType("TEXT");
            b.Property<Guid>("Id").HasColumnType("TEXT");
            b.Property<string>("Action").IsRequired().HasMaxLength(32).HasColumnType("TEXT");
            b.Property<long>("CreatedAtUnixMs").HasColumnType("INTEGER");
            b.Property<string>("DocumentHash").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<string>("DocumentId").HasMaxLength(256).HasColumnType("TEXT");
            b.Property<string>("Error").HasColumnType("TEXT");
            b.Property<int>("ItemCount").HasColumnType("INTEGER");
            b.Property<long?>("LastCheckedAtUnixMs").HasColumnType("INTEGER");
            b.Property<long?>("LeaseUntilUnixMs").HasColumnType("INTEGER");
            b.Property<int>("Mode").HasColumnType("INTEGER");
            b.Property<long?>("NextCheckAtUnixMs").HasColumnType("INTEGER");
            b.Property<string>("PackageCode").IsRequired().HasMaxLength(74).HasColumnType("TEXT");
            b.Property<int>("PollCount").HasColumnType("INTEGER");
            b.Property<string>("ProductGroup").IsRequired().HasMaxLength(32).HasColumnType("TEXT");
            b.Property<Guid>("ProfileId").HasColumnType("TEXT");
            b.Property<string>("RemoteStatus").HasMaxLength(64).HasColumnType("TEXT");
            b.Property<string>("State").IsRequired().HasMaxLength(32).HasColumnType("TEXT");
            b.Property<long?>("SubmittedAtUnixMs").HasColumnType("INTEGER");
            b.Property<long?>("TrackingUntilUnixMs").HasColumnType("INTEGER");
            b.HasKey("Id");
            b.HasIndex("NextCheckAtUnixMs");
            b.HasIndex("ProfileId");
            b.ToTable("PackageTransformationDocuments");
        });

        modelBuilder.Entity("CrptWorkbench.Infrastructure.ProtectedSecret", b =>
        {
            b.Property<string>("Key").HasMaxLength(300).HasColumnType("TEXT");
            b.Property<string>("ProtectedValue").IsRequired().HasColumnType("TEXT");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("TEXT");
            b.HasKey("Key");
            b.ToTable("ProtectedSecrets");
        });

        modelBuilder.Entity("CrptWorkbench.Domain.FavoriteApiOperation", b =>
        {
            b.HasOne("CrptWorkbench.Domain.CrptProfile", null)
                .WithMany()
                .HasForeignKey("ProfileId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });
        modelBuilder.Entity("CrptWorkbench.Domain.BulkCodeJob", b =>
        {
            b.HasOne("CrptWorkbench.Domain.CrptProfile", null)
                .WithMany().HasForeignKey("ProfileId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity("CrptWorkbench.Domain.BulkCodeBatch", b =>
        {
            b.HasOne("CrptWorkbench.Domain.BulkCodeJob", null)
                .WithMany().HasForeignKey("JobId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });

        modelBuilder.Entity("CrptWorkbench.Domain.SuzUtilisationJob", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT");
            b.Property<Guid>("ProfileId").HasColumnType("TEXT");
            b.Property<int>("Mode").HasColumnType("INTEGER");
            b.Property<string>("ProductGroup").IsRequired().HasColumnType("TEXT");
            b.Property<string>("PackageType").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Scenario").IsRequired().HasColumnType("TEXT");
            b.Property<string>("OmsId").IsRequired().HasColumnType("TEXT");
            b.Property<string>("ConnectionId").IsRequired().HasColumnType("TEXT");
            b.Property<string>("BaseUrl").IsRequired().HasColumnType("TEXT");
            b.Property<string>("CertificateThumbprint").IsRequired().HasColumnType("TEXT");
            b.Property<string>("CorrelationId").IsRequired().HasColumnType("TEXT");
            b.Property<int>("CodeCount").HasColumnType("INTEGER");
            b.Property<int>("ReportCount").HasColumnType("INTEGER");
            b.Property<long>("CreatedAtUnixMs").HasColumnType("INTEGER");
            b.Property<Guid?>("SubmissionOwner").HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("ProfileId", "Mode", "CreatedAtUnixMs");
            b.ToTable("SuzUtilisationJobs");
        });
        modelBuilder.Entity("CrptWorkbench.Domain.SuzUtilisationReport", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT");
            b.Property<Guid>("JobId").HasColumnType("TEXT");
            b.Property<int>("Number").HasColumnType("INTEGER");
            b.Property<int>("CodeCount").HasColumnType("INTEGER");
            b.Property<string>("ProtectedPayload").IsRequired().HasColumnType("TEXT");
            b.Property<string>("State").IsRequired().HasColumnType("TEXT");
            b.Property<string>("ReportId").HasColumnType("TEXT");
            b.Property<string>("RemoteStatus").HasColumnType("TEXT");
            b.Property<string>("ReceiptState").HasColumnType("TEXT");
            b.Property<string>("Error").HasColumnType("TEXT");
            b.Property<long?>("SubmittedAtUnixMs").HasColumnType("INTEGER");
            b.Property<long?>("LastCheckedAtUnixMs").HasColumnType("INTEGER");
            b.Property<long?>("NextCheckAtUnixMs").HasColumnType("INTEGER");
            b.Property<long?>("TrackingUntilUnixMs").HasColumnType("INTEGER");
            b.Property<long?>("LeaseUntilUnixMs").HasColumnType("INTEGER");
            b.Property<Guid?>("TrackingOwner").HasColumnType("TEXT");
            b.Property<int>("PollCount").HasColumnType("INTEGER");
            b.HasKey("Id");
            b.HasIndex("JobId", "Number").IsUnique();
            b.HasIndex("NextCheckAtUnixMs");
            b.ToTable("SuzUtilisationReports");
        });
        modelBuilder.Entity("CrptWorkbench.Domain.SuzUtilisationJob", b =>
        {
            b.HasOne("CrptWorkbench.Domain.CrptProfile", null)
                .WithMany().HasForeignKey("ProfileId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity("CrptWorkbench.Domain.SuzUtilisationReport", b =>
        {
            b.HasOne("CrptWorkbench.Domain.SuzUtilisationJob", null)
                .WithMany().HasForeignKey("JobId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
#pragma warning restore 612, 618
    }
}
