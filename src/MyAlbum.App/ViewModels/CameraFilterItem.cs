namespace MyAlbum_App.ViewModels;

/// <summary>
/// A camera model entry in the filter list. The first entry (null Model) means "all cameras".
/// </summary>
public sealed class CameraFilterItem
{
    public string? Model { get; }
    public long Count { get; }
    public string DisplayName => Model ?? "全部相机";

    public CameraFilterItem(string? model, long count)
    {
        Model = model;
        Count = count;
    }
}
