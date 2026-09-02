namespace MyAlbum.Core.Infrastructure;

/// <summary>
/// Well-known locations for the app's index database and cache.
/// </summary>
public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\Atlumina — root for the app's own data.</summary>
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlumina");

    /// <summary>SQLite index database.</summary>
    public static string DatabasePath => Path.Combine(AppDataDirectory, "atlumina.db");

    /// <summary>L2 disk cache root (WebP thumbnails etc.).</summary>
    public static string CacheDirectory => Path.Combine(AppDataDirectory, "cache");

    /// <summary>Generated 256px WebP thumbnails.</summary>
    public static string ThumbnailCacheDirectory => Path.Combine(CacheDirectory, "thumbs");

    /// <summary>
    /// One-time migration for the rename from MyAlbum to Atlumina: if the legacy
    /// %LOCALAPPDATA%\MyAlbum folder still exists and the new data directory is empty,
    /// rename the whole folder over (same volume, so instant) and rename the index file.
    /// Preserves the library DB, thumbnails, downloaded models and the installed ExifTool.
    /// Best-effort: on failure the app simply starts with an empty library.
    /// </summary>
    public static void MigrateLegacyData()
    {
        try
        {
            var legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyAlbum");
            if (string.Equals(legacyDir, AppDataDirectory, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(legacyDir)
                || Directory.Exists(AppDataDirectory))
            {
                return;
            }

            Directory.Move(legacyDir, AppDataDirectory);

            // The index file name changed too (myalbum.db -> atlumina.db). Move the WAL
            // sidecar files along with it so no un-checkpointed data is stranded.
            var legacyDb = Path.Combine(AppDataDirectory, "myalbum.db");
            if (File.Exists(legacyDb)
                && !string.Equals(Path.GetFileName(legacyDb), Path.GetFileName(DatabasePath), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var src = legacyDb + suffix;
                    if (File.Exists(src))
                    {
                        File.Move(src, DatabasePath + suffix);
                    }
                }
            }
        }
        catch
        {
            // best effort: fall back to a fresh (empty) library if the rename fails
        }
    }
}
