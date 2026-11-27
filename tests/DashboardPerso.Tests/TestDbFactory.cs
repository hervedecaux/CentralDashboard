using DashboardPerso.Data;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Tests;

internal sealed class TestDbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);
}
