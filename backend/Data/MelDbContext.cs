using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Data;

public class MelDbContext : DbContext
{
    public MelDbContext(DbContextOptions<MelDbContext> options) : base(options) { }

    public DbSet<EquipmentItem> Equipment => Set<EquipmentItem>();
    public DbSet<StagedParameterRecord> StagedUpdates => Set<StagedParameterRecord>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();
    public DbSet<RevitModel> Models => Set<RevitModel>();
    public DbSet<ApsConnection> ApsConnections => Set<ApsConnection>();

    private static string Json(Dictionary<Guid, string> v) => JsonSerializer.Serialize(v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<EquipmentItem>(e =>
        {
            e.ToTable("equipment_master");
            e.HasKey(x => x.Id);
            e.Property(x => x.ModelGuid).HasMaxLength(100).IsRequired();
            e.Property(x => x.UniqueId).HasMaxLength(100).IsRequired();
            e.Property(x => x.EquipmentGuid).HasMaxLength(100).IsRequired();
            e.Property(x => x.Tag).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.ModelGuid, x.UniqueId }).IsUnique();
            e.HasIndex(x => x.EquipmentGuid);   // cross-model link (MEL_Equipment_GUID)

            // Parameter values (SI units) keyed by parameter GUID, stored as jsonb.
            e.Property(x => x.Values).HasColumnType("jsonb").HasConversion(
                v => Json(v),
                v => JsonSerializer.Deserialize<Dictionary<Guid, string>>(v, (JsonSerializerOptions?)null) ?? new Dictionary<Guid, string>(),
                new ValueComparer<Dictionary<Guid, string>>(
                    (a, c) => Json(a!) == Json(c!),
                    v => Json(v).GetHashCode(),
                    v => new Dictionary<Guid, string>(v)));

            e.UseXminAsConcurrencyToken();
        });

        b.Entity<StagedParameterRecord>(e =>
        {
            e.ToTable("staging_parameter_updates");
            e.HasKey(x => x.Id);
            e.Property(x => x.ModelGuid).HasMaxLength(100).IsRequired();
            e.Property(x => x.EquipmentUniqueId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasOne<EquipmentItem>().WithMany().HasForeignKey(x => x.EquipmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ModelGuid, x.Status });
            // At most one open change per equipment + parameter (soft-lock at the data layer).
            e.HasIndex(x => new { x.EquipmentId, x.ParameterGuid }).IsUnique().HasFilter("\"Status\" = 'Pending'");
            e.UseXminAsConcurrencyToken();
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.ModelGuid, x.EquipmentUniqueId, x.OccurredAt });
            e.HasIndex(x => x.BatchId);
        });

        b.Entity<SyncJob>(e =>
        {
            e.ToTable("sync_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.ModelGuid).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Runner).HasMaxLength(16);
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.Property(x => x.RunnerState).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ModelGuid, x.CreatedAt });
            // One in-flight sync per model: two writers against one central model would race. Enforced by the database.
            e.HasIndex(x => x.ModelGuid).IsUnique().HasFilter("\"Status\" IN ('Queued','Submitted')").HasDatabaseName("UX_sync_jobs_one_active_per_model");
            e.UseXminAsConcurrencyToken();
        });

        b.Entity<RevitModel>(e =>
        {
            e.ToTable("revit_models");
            e.HasKey(x => x.ModelGuid);
            e.Property(x => x.ModelGuid).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Region).HasMaxLength(8);
            e.Ignore(x => x.IsLinked);
        });

        b.Entity<ApsConnection>(e =>
        {
            e.ToTable("aps_connection");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
            e.UseXminAsConcurrencyToken();
        });
    }
}
