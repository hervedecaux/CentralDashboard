using DashboardPerso.Data;
using DashboardPerso.Models;
using DashboardPerso.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"backup-tests-{Guid.NewGuid():N}");
    private readonly TestDbFactory factory;
    private readonly BackupService service;

    public BackupServiceTests()
    {
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "dashboard.db");
        factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False").Options);
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        db.Expenses.Add(new Expense { Label = "Boulangerie", Amount = 4.2m, Date = new DateOnly(2026, 11, 2) });
        db.SaveChanges();
        service = new BackupService(factory, new DatabaseFile(dbPath));
    }

    [Fact]
    public async Task Create_WritesConsistentCopyInBackupFolder()
    {
        var backup = await service.CreateAsync();

        Assert.Equal(Path.Combine(dir, "Sauvegardes"), Path.GetDirectoryName(backup.FullPath));
        Assert.StartsWith("dashboard-", backup.FileName);
        Assert.Single(service.List());

        await using var copy = new SqliteConnection($"Data Source={backup.FullPath};Mode=ReadOnly;Pooling=False");
        await copy.OpenAsync();
        await using var cmd = copy.CreateCommand();
        cmd.CommandText = "SELECT Label FROM Expenses";
        Assert.Equal("Boulangerie", await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Find_RejectsPathsOutsideBackupFolder()
    {
        var backup = await service.CreateAsync();

        Assert.NotNull(service.Find(backup.FileName));
        Assert.Null(service.Find("../dashboard.db"));
        Assert.Null(service.Find("absent.db"));
        Assert.True(service.Delete(backup.FileName));
        Assert.Empty(service.List());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir, recursive: true);
    }
}
