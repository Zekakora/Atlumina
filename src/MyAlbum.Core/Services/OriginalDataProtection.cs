namespace MyAlbum.Core.Services;

/// <summary>
/// Global safety switch ("保护原始照片"). When enabled, every operation that writes to,
/// deletes or renames the original photo files is blocked — EXIF edits, GPS write-back,
/// timestamp fixes, format/dedup cleanup and batch rename. The SQLite index and the app's
/// own thumbnail cache are NOT affected; only the source photo files themselves.
/// The flag lives in Core so the services that mutate files can enforce it regardless of
/// the UI; the App mirrors the persisted setting into it.
/// </summary>
public static class OriginalDataProtection
{
    private static int _enabled;

    public static bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public static void SetEnabled(bool value) => Interlocked.Exchange(ref _enabled, value ? 1 : 0);

    /// <summary>Shown to the user whenever a write/delete/rename is blocked.</summary>
    public const string BlockedMessage =
        "已开启「保护原始照片」：禁止修改、删除或重命名原始照片文件。请在设置中关闭该开关后重试。";
}
