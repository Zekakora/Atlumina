using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Media.Imaging;
using MyAlbum.Core.Data;
using MyAlbum.Core.Services;

namespace MyAlbum_App.ViewModels;
/// <summary>A person cluster card shown on the people page.</summary>
public partial class PersonItem : ObservableObject
{
    public long PersonId { get; }
    public long FaceCount { get; }
    public long PhotoCount { get; }
    public BitmapImage? Thumbnail { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    public string Summary { get; }

    public PersonItem(MyAlbum.Core.Models.PersonClusterInfo info, string? thumbnailPath)
    {
        PersonId = info.PersonId;
        FaceCount = info.FaceCount;
        PhotoCount = info.PhotoCount;
        Title = info.DisplayTitle;
        Summary = $"{PhotoCount} 张照片 · {FaceCount} 张脸";
        if (thumbnailPath is not null && File.Exists(thumbnailPath))
        {
            Thumbnail = new BitmapImage(new Uri(thumbnailPath));
        }
    }
}

/// <summary>
/// Drives the people-album page: lists detected person clusters (from the face index) as
/// cards, and applies a person filter to the home photo view when one is selected. Also
/// supports renaming a person and merging two clusters into one.
/// </summary>
public partial class PeopleViewModel : ObservableObject
{
    private readonly PhotoDatabase _db;
    private readonly ThumbnailService _thumbs;

    public System.Collections.ObjectModel.ObservableCollection<PersonItem> People { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public IAsyncRelayCommand<PersonItem> OpenPersonCommand { get; }

    public PeopleViewModel(PhotoDatabase db, ThumbnailService thumbs)
    {
        _db = db;
        _thumbs = thumbs;
        OpenPersonCommand = new AsyncRelayCommand<PersonItem>(OpenPersonAsync);
    }

    public async Task InitializeAsync()
    {
        await App.DatabaseReady;
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var clusters = await _db.GetPersonClustersAsync();
            People.Clear();
            foreach (var info in clusters)
            {
                string? thumbPath = null;
                if (info.RepresentativePath is not null)
                {
                    var p = await _db.GetPhotoByPathAsync(info.RepresentativePath);
                    if (p is not null)
                    {
                        thumbPath = await _thumbs.GetOrCreateThumbnailAsync(p);
                    }
                }
                People.Add(new PersonItem(info, thumbPath));
            }
            StatusText = People.Count == 0
                ? "尚未进行人脸分析（AI 功能页 → 人物识别）"
                : $"已识别 {People.Count} 个人物";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Applies the person filter on the home page and navigates there.</summary>
    public async Task OpenPersonAsync(PersonItem? item)
    {
        if (item is null)
        {
            return;
        }
        var home = App.Services.GetRequiredService<HomeViewModel>();
        home.ApplyPerson(item.PersonId, item.Title);
        // Navigate to the home view (top nav).
        if (App.Window is MainWindow main)
        {
            main.SelectView("home");
        }
    }

    /// <summary>Assigns (or clears, when empty) a display name to a person.</summary>
    public async Task RenamePersonAsync(PersonItem item, string? name)
    {
        await _db.RenamePersonAsync(item.PersonId, name);
        item.Title = string.IsNullOrWhiteSpace(name) ? $"人物 {item.PersonId}" : name.Trim();
    }

    /// <summary>Merges the source cluster into the target cluster and refreshes the list.</summary>
    public async Task MergePersonAsync(PersonItem source, PersonItem target)
    {
        await _db.MergePeopleAsync(source.PersonId, target.PersonId);
        await LoadAsync();
    }

    /// <summary>Deletes the person cluster (its face rows) and refreshes the list.</summary>
    public async Task DeletePersonAsync(PersonItem item)
    {
        await _db.DeletePersonAsync(item.PersonId);
        await LoadAsync();
    }
}
