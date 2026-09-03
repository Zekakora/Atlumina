using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MyAlbum.Core.Data;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;

namespace MyAlbum_App.ViewModels;

public partial class AlbumsViewModel : ObservableObject
{
    private readonly PhotoDatabase _db;
    private readonly ThumbnailService _thumbs;

    public ObservableCollection<SmartAlbumItem> AlbumCards { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    public IRelayCommand<SmartAlbumItem> ApplyAlbumCommand { get; }
    public IRelayCommand<SmartAlbumItem> DeleteAlbumCommand { get; }

    public AlbumsViewModel(PhotoDatabase db, ThumbnailService thumbs)
    {
        _db = db;
        _thumbs = thumbs;
        ApplyAlbumCommand = new RelayCommand<SmartAlbumItem>(ApplyAlbum);
        DeleteAlbumCommand = new RelayCommand<SmartAlbumItem>(DeleteAlbum);
    }

    public async Task InitializeAsync()
    {
        await App.DatabaseReady;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        var albums = await _db.GetSmartAlbumsAsync();
        AlbumCards.Clear();
        foreach (var album in albums)
        {
            var item = new SmartAlbumItem(album);
            var f = item.Filter;
            var count = await _db.CountPhotosAsync(
                f.FolderPath, f.CameraModel, f.RatingMin, f.TagName,
                f.SearchText, f.DateFrom, f.DateTo);
            item.PhotoCount = count;
            AlbumCards.Add(item);
        }
        StatusText = $"{AlbumCards.Count} 个相册";
    }

    public async Task SaveAlbumAsync(string name)
    {
        var home = App.Services.GetRequiredService<HomeViewModel>();
        var filter = home.CurrentFilter;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        await _db.UpsertSmartAlbumAsync(new SmartAlbum
        {
            Name = name.Trim(),
            FilterJson = filter.ToJson(),
            CreatedUtc = DateTime.UtcNow,
        });
        await ReloadAsync();
    }

    private void ApplyAlbum(SmartAlbumItem? item)
    {
        if (item is null)
        {
            return;
        }
        var home = App.Services.GetRequiredService<HomeViewModel>();
        home.ApplySmartAlbum(item);
    }

    private void DeleteAlbum(SmartAlbumItem? item)
    {
        if (item is null)
        {
            return;
        }
        _ = Task.Run(async () => await _db.DeleteSmartAlbumAsync(item.Id));
        AlbumCards.Remove(item);
    }
}
