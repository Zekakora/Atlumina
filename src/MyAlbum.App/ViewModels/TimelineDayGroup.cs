namespace MyAlbum_App.ViewModels;

/// <summary>
/// A single day in the timeline view: a date header plus that day's photos
/// rendered as one horizontal row.
/// </summary>
public sealed class TimelineDayGroup
{
    public DateTime Date { get; }
    public string Header { get; }
    public string CountText { get; }
    public IReadOnlyList<PhotoGridItem> Photos { get; }

    public TimelineDayGroup(DateTime date, IReadOnlyList<PhotoGridItem> photos)
    {
        Date = date;
        Photos = photos;
        Header = date.ToString("yyyy年M月d日 dddd");
        CountText = $"{photos.Count} 张";
    }
}
