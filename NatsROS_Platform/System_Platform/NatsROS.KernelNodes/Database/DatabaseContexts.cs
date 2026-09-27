using Microsoft.EntityFrameworkCore;
using NatsROS.Core.Environment;

namespace NatsROS.KernelNodes.Database;

// ==========================================
// 1. MES 生产数据实体表
// ==========================================
public class MesRecordEntity
{
    public int Id { get; set; }
    public string Barcode { get; set; } = "";
    public long Timestamp { get; set; }
    public bool IsPass { get; set; }
    public double CycleTimeSec { get; set; }

    // EF Core 存储扩展字典的 JSON 字符串列
    public string MetricsJson { get; set; } = "{}";
    public string InfosJson { get; set; } = "{}";
}

public class MesDbContext : DbContext
{
    public DbSet<MesRecordEntity> MesRecords { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // 物理隔离：生产数据单独一个库
        string dbPath = WorkspaceManager.GetLocalDatabasePath("mes_production.db");
        // Cache=Shared 是 SQLite 高并发的关键之一
        optionsBuilder.UseSqlite($"Data Source={dbPath};Cache=Shared");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 建立索引，让大屏查询速度起飞
        modelBuilder.Entity<MesRecordEntity>().HasIndex(e => e.Barcode);
        modelBuilder.Entity<MesRecordEntity>().HasIndex(e => e.Timestamp);
    }
}

// ==========================================
// 2. RMS 审计黑匣子实体表 (FDA Part 11)
// ==========================================
public class AuditLogEntity
{
    public int Id { get; set; }
    public long Timestamp { get; set; }
    public string Operator { get; set; } = "";
    public string RecipeId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
}

public class RmsDbContext : DbContext
{
    public DbSet<AuditLogEntity> AuditLogs { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // 物理隔离：审计日志单独一个库
        string dbPath = WorkspaceManager.GetLocalDatabasePath("rms_audits.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath};Cache=Shared");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLogEntity>().HasIndex(e => e.RecipeId);
        modelBuilder.Entity<AuditLogEntity>().HasIndex(e => e.Timestamp);
    }
}

// ==========================================
// 3. AEM 报警历史台账 (Alarm History)
// ==========================================
public class AlarmHistoryEntity
{
    public int Id { get; set; }
    public long Timestamp { get; set; }
    public string Code { get; set; } = "";
    public string Action { get; set; } = "";
    public string Operator { get; set; } = "";
    public string Details { get; set; } = "";
}

public class AemDbContext : DbContext
{
    public DbSet<AlarmHistoryEntity> AlarmHistories { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        string dbPath = WorkspaceManager.GetLocalDatabasePath("aem_history.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath};Cache=Shared");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlarmHistoryEntity>().HasIndex(e => e.Timestamp);
        modelBuilder.Entity<AlarmHistoryEntity>().HasIndex(e => e.Code);
    }
}