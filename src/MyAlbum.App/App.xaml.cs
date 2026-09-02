using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MyAlbum.Core.Data;
using MyAlbum.Core.Infrastructure;
using MyAlbum.Core.Services;
using MyAlbum_App.Services;
using MyAlbum_App.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MyAlbum_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private static string TempLogDir => Path.Combine(Path.GetTempPath(), "Atlumina");
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Application-wide service container (singletons only).
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// Completes when the SQLite index has been initialized on startup.
    /// </summary>
    public static Task DatabaseReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LogCrash(e.Exception?.ToString() ?? e.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject?.ToString() ?? "unhandled");
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash(e.Exception?.ToString() ?? "unobserved");
    }

    private static void LogCrash(string message)
    {
        try
        {
            Directory.CreateDirectory(TempLogDir);
            File.AppendAllText(Path.Combine(TempLogDir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n\n");
        }
        catch
        {
            // never crash while logging a crash
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Rename %LOCALAPPDATA%\MyAlbum -> %LOCALAPPDATA%\Atlumina once, preserving user data.
        AppPaths.MigrateLegacyData();

        Services = ConfigureServices();
        DatabaseReady = InitializeDatabaseAsync();

        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.AppWindow.Closing += OnAppWindowClosing;
        Window.Activate();
    }

    private bool _autoBackupDone;
    private bool _autoBackupRunning;

    /// <summary>
    /// Creates an automatic database backup before the window closes when the
    /// "退出应用时自动备份" setting is enabled. This version of AppWindowClosingEventArgs
    /// has no deferral, so the close is canceled, the backup runs, then the window is
    /// closed again. Failures never block the app from closing.
    /// </summary>
    private async void OnAppWindowClosing(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        var appState = Services.GetRequiredService<AppState>();
        if (!appState.EnableAutoBackup || _autoBackupDone)
        {
            return;
        }
        args.Cancel = true;
        if (_autoBackupRunning)
        {
            return;
        }
        _autoBackupRunning = true;
        try
        {
            var backup = Services.GetRequiredService<DatabaseBackupService>();
            await backup.BackupAsync(backup.ResolveDirectory(appState.BackupDirectory), "auto");
        }
        catch
        {
            // best-effort: a failed auto-backup must never block closing the app
        }
        finally
        {
            _autoBackupDone = true;
            _autoBackupRunning = false;
            Window.Close();
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new PhotoDatabase(AppPaths.DatabasePath));
        services.AddSingleton<MetadataReaderService>();
        services.AddSingleton(new ThumbnailService(AppPaths.ThumbnailCacheDirectory));
        services.AddSingleton<LibraryService>();
        services.AddSingleton<FolderWatcherService>();
        services.AddSingleton<AppState>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<AlbumsViewModel>();
        services.AddSingleton<PeopleViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MapViewModel>();
        services.AddSingleton<ExifWriterService>();
        services.AddSingleton<ExifToolInstallerService>();
        services.AddSingleton<DatabaseBackupService>();
        services.AddSingleton<DatabaseMaintenanceService>();
        services.AddSingleton<DbFileDiffService>();
        services.AddSingleton<DbFileDiffViewModel>();
        services.AddSingleton<BatchFileService>();
        services.AddSingleton<DuplicateService>();
        services.AddSingleton<PhotoDateFixService>();
        services.AddSingleton<FormatCleanupService>();
        services.AddSingleton<VisionAnalysisService>();
        services.AddSingleton<SceneTaggerService>();
        services.AddSingleton<AestheticScoreService>();
        services.AddSingleton<FeatureEmbeddingService>();
        services.AddSingleton<ObjectDetectionService>();
        services.AddSingleton<ClipService>();
        services.AddSingleton<DeepAnalysisService>();
        services.AddSingleton<ReverseGeocodeService>();
        services.AddSingleton<GpsPlaceService>();
        services.AddSingleton<LowQualityPhotoService>();
        services.AddSingleton<QualityToolViewModel>();
        services.AddSingleton<FaceService>();
        services.AddSingleton<FaceClusteringService>();
        services.AddSingleton<AiViewModel>();
        services.AddSingleton<GpsToolViewModel>();
        services.AddSingleton<LlmService>();
        services.AddSingleton<AddressNormalizeService>();
        return services.BuildServiceProvider();
    }

    private static async Task InitializeDatabaseAsync()
    {
        var db = Services.GetRequiredService<PhotoDatabase>();
        await db.InitializeAsync();

        // Restore persisted settings (right-panel field toggles etc.).
        await Services.GetRequiredService<AppState>().LoadAsync();

        // Self-heal: register folders for photos whose import was interrupted before the
        // folder row was written, so they show up in Settings and get watched again.
        await Services.GetRequiredService<LibraryService>().RepairFolderRecordsAsync();

        // Restore folder watching for folders that were watched previously.
        var watcher = Services.GetRequiredService<FolderWatcherService>();
        foreach (var folder in await db.GetFoldersAsync())
        {
            if (folder.IsWatched && Directory.Exists(folder.Path))
            {
                watcher.WatchFolder(folder.Path);
            }
        }
    }
}
