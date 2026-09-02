using MyAlbum.Core.Data;
using MyAlbum.Core.Infrastructure;

namespace MyAlbum.Core.Services;

/// <summary>
/// Creates timestamped SQLite backups of the photo index and restores from them.
/// Backups are written with the SQLite online-backup API so WAL data is included
/// (see <see cref="PhotoDatabase.BackupToAsync"/>).
/// </summary>
public sealed class DatabaseBackupService
{
    /// <summary>How many backups per prefix are kept on disk (oldest are pruned).</summary>
    private const int MaxBackupsPerPrefix = 20;

    private readonly PhotoDatabase _db;

    public DatabaseBackupService(PhotoDatabase db) => _db = db;

    /// <summary>Default backup folder inside the app data directory.</summary>
    public static string DefaultBackupDirectory =>
        Path.Combine(MyAlbum.Core.Infrastructure.AppPaths.AppDataDirectory, "backups");

    /// <summary>Resolves the directory to use: the configured one, or the default when empty.</summary>
    public string ResolveDirectory(string configured) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? DefaultBackupDirectory : configured);

    /// <summary>
    /// Backs up the index into <paramref name="directory"/> as <c>{prefix}_yyyyMMdd_HHmmss.db</c>,
    /// pruning the oldest <c>{prefix}</c> backups so the folder does not grow forever. The
    /// app's settings (settings.json, incl. LLM / AMap API keys) are backed up alongside as
    /// <c>{prefix}_yyyyMMdd_HHmmss.settings.json</c>. Returns the full path of the written backup.
    /// </summary>
    public async Task<string> BackupAsync(string directory, string prefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.db");
        await _db.BackupToAsync(destination);
        CopySettingsToBackup(destination);
        ct.ThrowIfCancellationRequested();
        Prune(directory, prefix);
        return destination;
    }

    /// <summary>
    /// Restores the index from a backup file (overwrites the live database). If a matching
    /// <c>.settings.json</c> backup exists, it is also copied back over the live settings.
    /// Returns true when settings were restored too.
    /// </summary>
    public async Task<bool> RestoreAsync(string backupPath)
    {
        await _db.RestoreFromAsync(backupPath);

        var settingsBackup = Path.ChangeExtension(backupPath, ".settings.json");
        if (!File.Exists(settingsBackup))
        {
            return false;
        }
        try
        {
            var settingsLive = Path.Combine(AppPaths.AppDataDirectory, "settings.json");
            var dir = Path.GetDirectoryName(settingsLive);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.Copy(settingsBackup, settingsLive, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopySettingsToBackup(string backupDbPath)
    {
        try
        {
            var settingsLive = Path.Combine(AppPaths.AppDataDirectory, "settings.json");
            if (File.Exists(settingsLive))
            {
                File.Copy(settingsLive, Path.ChangeExtension(backupDbPath, ".settings.json"), overwrite: true);
            }
        }
        catch
        {
            // settings backup is best-effort
        }
    }

    /// <summary>Deletes the oldest backups for a prefix, keeping only the newest <see cref="MaxBackupsPerPrefix"/>.</summary>
    private static void Prune(string directory, string prefix)
    {
        try
        {
            // Timestamps sort lexicographically, so order by name is newest-first.
            var stale = Directory.GetFiles(directory, prefix + "_*.db")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Skip(MaxBackupsPerPrefix);
            foreach (var file in stale)
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // best-effort: a locked file just stays
                }
            }
        }
        catch
        {
            // pruning is best-effort
        }
    }
}
