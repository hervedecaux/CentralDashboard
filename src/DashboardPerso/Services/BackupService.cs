using System.Text.RegularExpressions;
using DashboardPerso.Data;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services;

/// <summary>Chemin du fichier SQLite utilisé par l'application.</summary>
public record DatabaseFile(string Path);

/// <summary>Une sauvegarde présente dans le dossier des sauvegardes.</summary>
public record BackupInfo(string FileName, string FullPath, DateTime CreatedAt, long Size);

/// <summary>
/// Sauvegardes de la base SQLite. Les copies sont faites avec <c>VACUUM INTO</c> : SQLite produit un fichier
/// cohérent et compact même pendant que l'application tourne (pas de risque de copier une base à moitié écrite).
/// </summary>
public partial class BackupService(IDbContextFactory<AppDbContext> dbFactory, DatabaseFile database)
{
    public const string FolderName = "Sauvegardes";

    public string DatabasePath => System.IO.Path.GetFullPath(database.Path);

    /// <summary>Dossier « Sauvegardes », à côté de la base.</summary>
    public string BackupDirectory => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(DatabasePath)!, FolderName);

    private string BaseName => System.IO.Path.GetFileNameWithoutExtension(DatabasePath);

    /// <summary>Nom proposé pour une copie faite maintenant, ex. dashboard-2026-11-28_0730.db.</summary>
    public string NewFileName(DateTime now) => $"{BaseName}-{now:yyyy-MM-dd_HHmmss}.db";

    /// <summary>Enregistre une copie horodatée dans le dossier des sauvegardes.</summary>
    public async Task<BackupInfo> CreateAsync()
    {
        Directory.CreateDirectory(BackupDirectory);
        var path = System.IO.Path.Combine(BackupDirectory, NewFileName(DateTime.Now));
        await CopyToAsync(path);
        return Info(path);
    }

    /// <summary>Copie dans un fichier temporaire (pour un téléchargement) ; à supprimer par l'appelant.</summary>
    public async Task<string> CreateTemporaryCopyAsync()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dashboardperso-{Guid.NewGuid():N}.db");
        await CopyToAsync(path);
        return path;
    }

    /// <summary>Sauvegardes du dossier, les plus récentes d'abord.</summary>
    public List<BackupInfo> List()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        return Directory.EnumerateFiles(BackupDirectory, "*.db")
            .Select(Info)
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    /// <summary>Chemin d'une sauvegarde du dossier d'après son nom, ou null (nom invalide ou absent).</summary>
    public string? Find(string fileName)
    {
        if (!SafeName().IsMatch(fileName)) return null;
        var path = System.IO.Path.Combine(BackupDirectory, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Supprime une sauvegarde du dossier.</summary>
    public bool Delete(string fileName)
    {
        if (Find(fileName) is not { } path) return false;
        File.Delete(path);
        return true;
    }

    private async Task CopyToAsync(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlAsync($"VACUUM INTO {path}");
    }

    private static BackupInfo Info(string path)
    {
        var file = new FileInfo(path);
        return new BackupInfo(file.Name, file.FullName, file.LastWriteTime, file.Length);
    }

    // Uniquement un nom de fichier simple (pas de dossier, pas de « .. »).
    [GeneratedRegex(@"^[\w\-. ]+\.db$")]
    private static partial Regex SafeName();
}
