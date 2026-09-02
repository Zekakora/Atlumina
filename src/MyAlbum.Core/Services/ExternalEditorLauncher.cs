using System.Diagnostics;

namespace MyAlbum.Core.Services;

/// <summary>
/// Launches an external RAW editor ("在 Camera Raw 中编辑"). The app never edits pixels
/// itself; it hands the original file to an installed Adobe application (Photoshop,
/// Lightroom, Bridge) or the user's default RAW editor via ShellExecute.
/// </summary>
public sealed class ExternalEditorLauncher
{
    /// <summary>Well-known executable names (in preference order) to hand an ARW to.
    /// 优先独立版 Camera Raw；其次 Bridge（启动快，不会像 Photoshop 那样拉起整包）；
    /// 最后才回落到 Photoshop / Lightroom 等重量级宿主。</summary>
    private static readonly (string Name, string Display)[] Editors =
    [
        ("Camera Raw.exe", "Adobe Camera Raw"),
        ("Bridge.exe", "Adobe Bridge"),
        ("lightroom.exe", "Adobe Lightroom"),
        ("Lightroom Classic.exe", "Adobe Lightroom Classic"),
        ("Photoshop.exe", "Adobe Photoshop"),
        ("PhotoshopCC.exe", "Adobe Photoshop CC"),
    ];

    /// <summary>
    /// Locates an installed Adobe RAW editor. Returns its display name, or null if none
    /// is found (the caller can fall back to the system default handler).
    /// </summary>
    public static (string Name, string Display)? FindInstalledEditor()
    {
        foreach (var (name, display) in Editors)
        {
            var path = FindOnDisk(name);
            if (path is not null)
            {
                return (path, display);
            }
        }
        return null;
    }

    /// <summary>
    /// Opens a photo in an external editor: the resolved Adobe app if one is installed,
    /// otherwise the system default application for the file type (ShellExecute).
    /// Returns a short human-readable message on failure.
    /// </summary>
    public static string? Open(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return "文件不存在: " + filePath;
        }

        var editor = FindInstalledEditor();
        var psi = new ProcessStartInfo
        {
            FileName = editor?.Name ?? filePath,
            UseShellExecute = true,
        };
        if (editor is not null)
        {
            psi.ArgumentList.Add(filePath);
        }

        try
        {
            Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? FindOnDisk(string fileName)
    {
        string[] programFiles =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        // Search the common Adobe install roots (bounded depth to stay fast).
        foreach (var root in programFiles)
        {
            var adobe = Path.Combine(root, "Adobe");
            if (!Directory.Exists(adobe))
            {
                continue;
            }
            // 一级：独立版 Adobe\Camera Raw\Camera Raw.exe。
            // 必须优先于 Photoshop 目录下捆绑的 ACR，否则会先拉起 Photoshop 来托管它。
            if (fileName == "Camera Raw.exe")
            {
                var standalone = Path.Combine(adobe, "Camera Raw", "Camera Raw.exe");
                if (File.Exists(standalone))
                {
                    return standalone;
                }
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(adobe))
                {
                    // 二级：Adobe\<子目录>\<editor>.exe（如 Photoshop.exe / Lightroom.exe）
                    var direct = Path.Combine(sub, fileName);
                    if (File.Exists(direct))
                    {
                        return direct;
                    }
                    // 三级兜底：Adobe\<子目录>\Camera Raw\Camera Raw.exe（Photoshop 捆绑的 ACR）
                    if (fileName == "Camera Raw.exe")
                    {
                        var nested = Path.Combine(sub, "Camera Raw", fileName);
                        if (File.Exists(nested))
                        {
                            return nested;
                        }
                    }
                }
            }
            catch
            {
                // unreadable sub-tree
            }
        }
        return null;
    }
}
