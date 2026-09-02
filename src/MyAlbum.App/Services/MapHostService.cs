using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using MyAlbum.Core.Infrastructure;

namespace MyAlbum_App.Services;

/// <summary>
/// Bootstraps a WebView2 hosting the local map page: sets up the "myalbum.map"
/// virtual host for the bundled Leaflet assets and "myalbum.data" for the thumbnail
/// cache, then navigates to map.html and waits for the document to load. Shared by
/// the map view and the GPS tool page.
/// </summary>
public static class MapHostService
{
    /// <summary>
    /// Initializes <paramref name="webView"/> for the map page. Returns the host when
    /// ready (message listeners may be registered against it), or null on failure.
    /// </summary>
    public static async Task<CoreWebView2?> InitializeAsync(WebView2 webView, Action<string>? diagnostics = null)
    {
        diagnostics?.Invoke("地图: 初始化 WebView2…");
        try
        {
            // Unpackaged WebView2 默认把用户数据（EBWebView 等）放在 exe 所在目录——
            // IDE 里（bin\Debug）可写没问题，装到 Program Files 后无写权限会初始化失败。
            // 显式指到 %LOCALAPPDATA%\Atlumina\WebView2。
            var userDataFolder = Path.Combine(AppPaths.AppDataDirectory, "WebView2");
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                null, userDataFolder, new CoreWebView2EnvironmentOptions());
            await webView.EnsureCoreWebView2Async(environment);
            var host = webView.CoreWebView2;
            if (host is null)
            {
                diagnostics?.Invoke("地图: WebView2 初始化失败");
                LogToCrash("MapHostService: CoreWebView2 is null after EnsureCoreWebView2Async");
                return null;
            }

            var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Map");
            diagnostics?.Invoke("地图: WebView2 就绪，映射资源目录…");
            LogToCrash("MapHostService assetsDir=" + assetsDir
                + " exists=" + Directory.Exists(assetsDir)
                + " leaflet=" + File.Exists(Path.Combine(assetsDir, "leaflet.js"))
                + " maphtml=" + File.Exists(Path.Combine(assetsDir, "map.html")));
            host.SetVirtualHostNameToFolderMapping(
                "myalbum.map",
                assetsDir,
                CoreWebView2HostResourceAccessKind.Allow);
            host.SetVirtualHostNameToFolderMapping(
                "myalbum.data",
                AppPaths.AppDataDirectory,
                CoreWebView2HostResourceAccessKind.Allow);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.NavigationCompleted += (_, args) => tcs.TrySetResult(args.IsSuccess);

            diagnostics?.Invoke("地图: 加载地图页面…");
            host.Navigate("https://myalbum.map/map.html");

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != tcs.Task)
            {
                diagnostics?.Invoke("地图: 页面加载超时（15s），未收到 NavigationCompleted");
                LogToCrash("MapHostService: map.html 加载超时，未收到 NavigationCompleted");
                return null;
            }
            if (!tcs.Task.Result)
            {
                diagnostics?.Invoke("地图: 页面加载失败");
                LogToCrash("MapHostService: map.html 导航失败 (IsSuccess=false)");
                return null;
            }
            return host;
        }
        catch (Exception ex)
        {
            diagnostics?.Invoke("地图: 初始化异常 " + ex.GetType().Name);
            LogToCrash("MapHostService: " + ex);
            return null;
        }
    }

    private static void LogToCrash(string message)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Atlumina");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [MapHostService] {message}\n");
        }
        catch
        {
            // never crash while logging
        }
    }
}
