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
// En développement (dotnet run), appsettings.Development.json utilise dashboard-dev.db, à côté :
// les essais ne touchent pas aux données de la version installée.
var dbPath = builder.Configuration["Database:Path"];
if (string.IsNullOrWhiteSpace(dbPath))
{
    var dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashboardPerso");
    var fileName = builder.Configuration["Database:FileName"];
    dbPath = Path.Combine(dataDir, string.IsNullOrWhiteSpace(fileName) ? "dashboard.db" : fileName);
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<ExpenseService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<ActivityService>();
builder.Services.AddScoped<CategoryRuleService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<PayeeService>();
builder.Services.AddScoped<OperationListState>();
builder.Services.AddSingleton(new DatabaseFile(dbPath));
builder.Services.AddScoped<BackupService>();

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
app.Logger.LogInformation("Environnement : {Environment} · base de données : {DbPath}",
    app.Environment.EnvironmentName, Path.GetFullPath(dbPath));

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();

// Sauvegarde de la base (page Données) : copie cohérente téléchargée, ou sauvegarde déjà enregistrée.
const string SqliteMime = "application/vnd.sqlite3";
app.MapGet("/donnees/telecharger", async (BackupService backups) =>
{
    var path = await backups.CreateTemporaryCopyAsync();
    // Le fichier temporaire est supprimé une fois l'envoi terminé.
    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
        bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    return Results.File(stream, SqliteMime, backups.NewFileName(DateTime.Now));
});
app.MapGet("/donnees/sauvegardes/{fileName}", (string fileName, BackupService backups) =>
    backups.Find(fileName) is { } path ? Results.File(path, SqliteMime, fileName) : Results.NotFound());
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
