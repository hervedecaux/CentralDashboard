using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<BudgetCategory> BudgetCategories => Set<BudgetCategory>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Payee> Payees => Set<Payee>();
    public DbSet<ExpenseSplit> ExpenseSplits => Set<ExpenseSplit>();
    public DbSet<ActivityCategory> ActivityCategories => Set<ActivityCategory>();
    public DbSet<WeeklySlot> WeeklySlots => Set<WeeklySlot>();
    public DbSet<PlannedActivity> PlannedActivities => Set<PlannedActivity>();

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
        modelBuilder.Entity<Expense>()
            .HasOne(e => e.Account).WithMany()
            .HasForeignKey(e => e.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Expense>().Property(e => e.AccountId).HasDefaultValue(Account.MainAccountId);
        modelBuilder.Entity<Expense>()
            .HasOne(e => e.Payee).WithMany(p => p.Expenses)
            .HasForeignKey(e => e.PayeeId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Account>().HasIndex(a => a.ImportId).IsUnique();
        modelBuilder.Entity<Account>().HasIndex(a => a.AccountNumber).IsUnique();

        modelBuilder.Entity<ExpenseSplit>()
            .HasOne(s => s.Expense).WithMany(e => e.Splits)
            .HasForeignKey(s => s.ExpenseId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ExpenseSplit>()
            .HasOne(s => s.Category).WithMany()
            .HasForeignKey(s => s.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Account>().HasData(new Account
        {
            Id = Account.MainAccountId, Label = "Compte courant", Kind = AccountKind.CheckingAccount, ImportId = "Compte"
        });

        modelBuilder.Entity<Payee>().HasIndex(p => p.Key).IsUnique();
        modelBuilder.Entity<Payee>()
            .HasOne(p => p.DefaultCategory).WithMany()
            .HasForeignKey(p => p.DefaultCategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<CategoryRule>()
            .HasOne(r => r.Category)
            .WithMany()
            .HasForeignKey(r => r.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TaskItem>().HasIndex(t => t.Status);
        modelBuilder.Entity<TaskItem>()
            .HasOne(t => t.ActivityCategory).WithMany()
            .HasForeignKey(t => t.ActivityCategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // Planning d'activités
        modelBuilder.Entity<ActivityCategory>()
            .HasOne(c => c.Parent).WithMany(c => c.Children)
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<WeeklySlot>()
            .HasOne(s => s.Category).WithMany()
            .HasForeignKey(s => s.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PlannedActivity>()
            .HasOne(a => a.Category).WithMany()
            .HasForeignKey(a => a.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PlannedActivity>()
            .HasOne(a => a.Slot).WithMany()
            .HasForeignKey(a => a.SlotId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<PlannedActivity>().HasIndex(a => a.Date);

        // Catégories d'activités de départ, modifiables dans Réglages › Activités.
        modelBuilder.Entity<ActivityCategory>().HasData(
            new ActivityCategory { Id = 1, Name = "Travail", Color = "#4f6bed", SortOrder = 1 },
            new ActivityCategory { Id = 2, Name = "Travaux", Color = "#b7702a", SortOrder = 2 },
            new ActivityCategory { Id = 3, Name = "Tâches ménagères", Color = "#2e9e6a", SortOrder = 3 },
            new ActivityCategory { Id = 4, Name = "Loisirs", Color = "#c2457a", SortOrder = 4 },
            new ActivityCategory { Id = 5, Name = "Sport", Color = "#e0582b", ParentId = 4, SortOrder = 1 },
            new ActivityCategory { Id = 6, Name = "Musique", Color = "#7a5bd1", ParentId = 4, SortOrder = 2 },
            new ActivityCategory { Id = 7, Name = "Lecture", Color = "#2b8fb3", ParentId = 4, SortOrder = 3 },
            new ActivityCategory { Id = 8, Name = "Photo", Color = "#d4a21b", ParentId = 4, SortOrder = 4 });

        // Quelques catégories de départ, modifiables depuis la page Budget.
        modelBuilder.Entity<BudgetCategory>().HasData(
            new BudgetCategory { Id = 1, Name = "Logement", AnnualBudget = 0, Color = "#4f6bed" },
            new BudgetCategory { Id = 2, Name = "Alimentation", AnnualBudget = 0, Color = "#2e9e6a" },
            new BudgetCategory { Id = 3, Name = "Transport", AnnualBudget = 0, Color = "#e08a1e" },
            new BudgetCategory { Id = 4, Name = "Loisirs", AnnualBudget = 0, Color = "#c2457a" },
            new BudgetCategory { Id = 5, Name = "Santé", AnnualBudget = 0, Color = "#7a5bd1" });
    }
}
