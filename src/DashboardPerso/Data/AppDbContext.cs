using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<BudgetCategory> BudgetCategories => Set<BudgetCategory>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite ne sait pas trier ni sommer des decimal : on les stocke en REAL.
        builder.Properties<decimal>().HaveConversion<double>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Expense>()
            .HasOne(e => e.Category)
            .WithMany(c => c.Expenses)
            .HasForeignKey(e => e.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<BudgetCategory>()
            .HasOne(c => c.Parent)
            .WithMany(c => c.Children)
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);   // on ne supprime pas une catégorie qui a des sous-catégories

        modelBuilder.Entity<Expense>().HasIndex(e => e.Date);
        modelBuilder.Entity<Expense>().HasIndex(e => e.ImportKey);

        modelBuilder.Entity<CategoryRule>()
            .HasOne(r => r.Category)
            .WithMany()
            .HasForeignKey(r => r.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TaskItem>().HasIndex(t => t.Status);

        // Quelques catégories de départ, modifiables depuis la page Budget.
        modelBuilder.Entity<BudgetCategory>().HasData(
            new BudgetCategory { Id = 1, Name = "Logement", AnnualBudget = 0, Color = "#4f6bed" },
            new BudgetCategory { Id = 2, Name = "Alimentation", AnnualBudget = 0, Color = "#2e9e6a" },
            new BudgetCategory { Id = 3, Name = "Transport", AnnualBudget = 0, Color = "#e08a1e" },
            new BudgetCategory { Id = 4, Name = "Loisirs", AnnualBudget = 0, Color = "#c2457a" },
            new BudgetCategory { Id = 5, Name = "Santé", AnnualBudget = 0, Color = "#7a5bd1" });
    }
}
