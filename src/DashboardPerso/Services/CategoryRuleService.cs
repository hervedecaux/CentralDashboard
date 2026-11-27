using DashboardPerso.Data;
using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services;

public class CategoryRuleService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<CategoryRule>> GetAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rules = await db.CategoryRules.AsNoTracking()
            .Include(r => r.Category).ThenInclude(c => c!.Parent)
            .ToListAsync();
        return rules
            .OrderBy(r => r.MarkAsTransfer ? "￿" : r.Category?.FullName ?? "")
            .ThenBy(r => r.Pattern)
            .ToList();
    }

    /// <summary>Ajoute (ou met à jour) une règle : catégorie, ou transfert si <paramref name="categoryId"/> est null.</summary>
    public async Task AddAsync(string pattern, int? categoryId, bool markAsTransfer = false)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0 || (categoryId is null && !markAsTransfer)) return;
        if (markAsTransfer) categoryId = null;

        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.CategoryRules.FirstOrDefaultAsync(r => r.Pattern == pattern);
        if (existing is null)
        {
            existing = new CategoryRule { Pattern = pattern };
            db.CategoryRules.Add(existing);
        }
        existing.CategoryId = categoryId;
        existing.MarkAsTransfer = markAsTransfer;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.CategoryRules.Where(r => r.Id == id).ExecuteDeleteAsync();
    }
}
