using CrptWorkbench.Domain;
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

        modelBuilder.Entity("CrptWorkbench.Domain.CrptProfile", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT");
            b.Property<string>("CertificateThumbprint").HasMaxLength(128).HasColumnType("TEXT");
            b.Property<string>("Inn").HasMaxLength(12).HasColumnType("TEXT");
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
#pragma warning restore 612, 618
    }
}
