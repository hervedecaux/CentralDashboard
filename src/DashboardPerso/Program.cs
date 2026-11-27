using System.Globalization;
using DashboardPerso.Components;
using DashboardPerso.Data;
using DashboardPerso.Services;
using DashboardPerso.Services.Import;
using Microsoft.EntityFrameworkCore;

// Affichage en français (dates, montants en euros).
var culture = new CultureInfo("fr-FR");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

// Version publiée lancée depuis un autre dossier (ex. lancement automatique à l'ouverture de session) :
// la configuration et les fichiers web sont lus à côté de l'exécutable.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"))
        ? null
        : AppContext.BaseDirectory,
});

// Base SQLite locale. Par défaut : ~/Library/Application Support/DashboardPerso/dashboard.db sur macOS.
// Modifiable via "Database:Path" dans appsettings.json.
var dbPath = builder.Configuration["Database:Path"];
if (string.IsNullOrWhiteSpace(dbPath))
{
    var dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashboardPerso");
    dbPath = Path.Combine(dataDir, "dashboard.db");
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<ExpenseService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<CategoryRuleService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<PayeeService>();
builder.Services.AddScoped<OperationListState>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Crée la base ou applique les nouvelles migrations au démarrage.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}
// Bénéficiaires des dépenses importées avant leur introduction.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<PayeeService>().BackfillImportedAsync();
}
app.Logger.LogInformation("Base de données : {DbPath}", Path.GetFullPath(dbPath));

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
