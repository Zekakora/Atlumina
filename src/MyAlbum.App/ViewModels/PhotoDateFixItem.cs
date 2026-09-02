using CommunityToolkit.Mvvm.ComponentModel;
using MyAlbum.Core.Models;

namespace MyAlbum_App.ViewModels;

/// <summary>
/// A single scanned photo shown in the "拍摄时间修复" dialog. Mismatched rows are
/// fixable (with a selection checkbox); matched / no-EXIF rows are read-only status rows.
/// </summary>
public partial class PhotoDateFixItem : ObservableObject
{
    public PhotoDateFixItem(PhotoDateCheckItem item)
    {
        Item = item;
    }

    public PhotoDateCheckItem Item { get; }

    public string FileName => Path.GetFileName(Item.FilePath);
    public string Folder => Item.DirectoryPath;

    public bool IsFixable => Item.Status == PhotoDateStatus.Mismatch;
    public bool IsNotFixable => !IsFixable;

    public string CurrentCreatedText => Item.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
    public string TargetCreatedText => Item.TakenAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";

    public string StatusText => Item.Status switch
    {
        PhotoDateStatus.Match => "已一致",
        PhotoDateStatus.NoExif => "无拍摄日期，无法修正",
        _ => "",
    };

    /// <summary>Non-null when <see cref="IsFixable"/>; used for the actual fix.</summary>
    public PhotoDateMismatch? Mismatch => Item.Mismatch;

    [ObservableProperty]
    public partial bool Selected { get; set; } = true;
}
