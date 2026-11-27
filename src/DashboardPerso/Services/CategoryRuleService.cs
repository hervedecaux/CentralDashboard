using DashboardPerso.Data;
using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services;

public class CategoryRuleService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<CategoryRule>> GetAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CategoryRules.AsNoTracking()
            .Include(r => r.Category).ThenInclude(c => c!.Parent)
            .OrderBy(r => r.Category!.Name).ThenBy(r => r.Pattern)
            .ToListAsync();
    }

    public async Task AddAsync(string pattern, int categoryId)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.CategoryRules.FirstOrDefaultAsync(r => r.Pattern == pattern);
        if (existing is not null) existing.CategoryId = categoryId;
        else db.CategoryRules.Add(new CategoryRule { Pattern = pattern, CategoryId = categoryId });
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.CategoryRules.Where(r => r.Id == id).ExecuteDeleteAsync();
    }
}
