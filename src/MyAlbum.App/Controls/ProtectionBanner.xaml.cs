using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyAlbum_App.Services;

namespace MyAlbum_App.Controls;

/// <summary>
/// A non-dismissible warning banner shown wherever an action could modify / delete /
/// rename original photo files, while 「保护原始照片」 is enabled. Auto-appears/hides
/// with the AppState.ProtectOriginalData toggle.
/// </summary>
public sealed partial class ProtectionBanner : UserControl
{
    public AppState AppState { get; }

    public ProtectionBanner()
    {
        AppState = App.Services.GetRequiredService<AppState>();
        InitializeComponent();
        AppState.PropertyChanged += OnAppStateChanged;
        Banner.Visibility = AppState.ProtectOriginalData ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.ProtectOriginalData))
        {
            Banner.Visibility = AppState.ProtectOriginalData ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
