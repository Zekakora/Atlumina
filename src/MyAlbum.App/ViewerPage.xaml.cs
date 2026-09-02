using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MyAlbum.Core.Data;
using MyAlbum.Core.Services;
using MyAlbum_App.ViewModels;
using Windows.System;

namespace MyAlbum_App;

/// <summary>
/// Fullscreen photo viewer (hosted in its own <see cref="ViewerWindow"/>).
/// Keyboard: ← → navigate, + / - zoom, 1-5 rate, 0 clear, Esc close.
/// Trackpad pinch / Ctrl+wheel zoom, drag to pan when zoomed.
/// </summary>
public sealed partial class ViewerPage : Page, INotifyPropertyChanged
{
    private readonly PhotoDatabase _db;
    private readonly ThumbnailService _thumbs;

    private List<PhotoGridItem> _photos = new();
    private int _index;

    /// <summary>Invoked to close the hosting window.</summary>
    public Action? CloseRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Rating
    {
        get => _index >= 0 && _index < _photos.Count ? _photos[_index].Rating : 0;
    }

    private void NotifyPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ViewerPage()
    {
        InitializeComponent();
        // MinZoomFactor must be >= 0.1 (WinUI enforces this); XAML parse of these values
        // can fail on some WinAppSDK builds, so set them here.
        ZoomScroller.MinZoomFactor = 0.1f;
        ZoomScroller.MaxZoomFactor = 8f;
        // User scroll input is handled manually: plain wheel zooms, left-drag pans.
        ZoomScroller.VerticalScrollMode = ScrollMode.Disabled;
        ZoomScroller.HorizontalScrollMode = ScrollMode.Disabled;
        _db = App.Services.GetRequiredService<PhotoDatabase>();
        _thumbs = App.Services.GetRequiredService<ThumbnailService>();

        // Plain mouse wheel zooms (Google-Photos style). Capture even if a child handled it.
        ViewerRoot.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnViewerWheel),
            handledEventsToo: true);

        // 视口变化时保持照片容器铺满（用于“最适合”时居中）。
        ZoomScroller.SizeChanged += (_, _) => UpdatePhotoHostSize();
    }

    private void OnViewerWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        Zoom(delta > 0 ? 1.25 : 0.8, e.GetCurrentPoint(ZoomScroller).Position);
        e.Handled = true;
    }

    // ---- left-drag to pan the photo ----
    private bool _panning;
    private Windows.Foundation.Point _panStart;
    private double _panStartH;
    private double _panStartV;

    private void PhotoImage_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _panning = true;
            PhotoImage.CapturePointer(e.Pointer);
            _panStart = e.GetCurrentPoint(ZoomScroller).Position;
            _panStartH = ZoomScroller.HorizontalOffset;
            _panStartV = ZoomScroller.VerticalOffset;
            e.Handled = true;
        }
    }

    private void PhotoImage_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning)
        {
            return;
        }
        var cur = e.GetCurrentPoint(ZoomScroller).Position;
        ZoomScroller.ChangeView(
            _panStartH + (_panStart.X - cur.X),
            _panStartV + (_panStart.Y - cur.Y),
            null,
            disableAnimation: true);
        e.Handled = true;
    }

    private void PhotoImage_OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            PhotoImage.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    /// <summary>Initializes the viewer with the photo list and start index (called by the host window).</summary>
    public async Task SetupAsync(ViewerSession session)
    {
        _photos = session.Photos.ToList();
        _index = Math.Clamp(session.StartIndex, 0, _photos.Count - 1);

        KeyDown += OnViewerKeyDown;
        Focus(FocusState.Programmatic);
        await LoadCurrentAsync();
    }

    private async Task LoadCurrentAsync()
    {
        if (_photos.Count == 0)
        {
            return;
        }

        var photo = _photos[_index].Photo;
        FileNameText.Text = photo.FileName;
        PositionText.Text = $"{_index + 1} / {_photos.Count}";
        NotifyPropertyChanged(nameof(Rating));

        var preview = await _thumbs.GetOrCreatePreviewAsync(photo);
        PhotoImage.Source = preview is null ? null : new BitmapImage(new Uri(preview));
    }

    private void PhotoImage_OnImageOpened(object sender, RoutedEventArgs e)
    {
        FitToWindow();
    }

    private void PhotoImage_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PhotoImage.Source is BitmapImage bmp && bmp.PixelWidth > 0)
        {
            FitToWindow();
        }
    }

    private double ComputeFitZoom()
    {
        if (PhotoImage.Source is not BitmapImage bmp || bmp.PixelWidth == 0 || bmp.PixelHeight == 0)
        {
            return 1.0;
        }
        double availW = ZoomScroller.ViewportWidth;
        double availH = ZoomScroller.ViewportHeight;
        if (availW <= 0 || availH <= 0)
        {
            return 1.0;
        }
        double fit = Math.Min(availW / bmp.PixelWidth, availH / bmp.PixelHeight);
        return Math.Clamp(fit, ZoomScroller.MinZoomFactor, ZoomScroller.MaxZoomFactor);
    }

    private void FitToWindow()
    {
        double fit = ComputeFitZoom();
        // 单次调用同时设置缩放与偏移（带动画=丝滑）：先前的“放大+跳角”来自两次 ChangeView
        // （先缩放、后偏移）动画互相冲突；一次到位就不会。
        double x = 0, y = 0;
        if (PhotoImage.Source is BitmapImage bmp && bmp.PixelWidth > 0)
        {
            double contentW = bmp.PixelWidth * fit;
            double contentH = bmp.PixelHeight * fit;
            x = Math.Max(0, (contentW - ZoomScroller.ViewportWidth) / 2);
            y = Math.Max(0, (contentH - ZoomScroller.ViewportHeight) / 2);
        }
        ZoomScroller.ChangeView(x, y, (float)fit);
        UpdatePhotoHostSize();
    }

    /// <summary>让照片容器至少铺满视口，这样小于视口的照片在“最适合”时也能居中。</summary>
    private void UpdatePhotoHostSize()
    {
        PhotoHost.MinWidth = ZoomScroller.ViewportWidth;
        PhotoHost.MinHeight = ZoomScroller.ViewportHeight;
    }

    private void OnViewerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
            case VirtualKey.Left:
                Navigate(-1);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                Navigate(1);
                e.Handled = true;
                break;
            case VirtualKey.Add:
            case (VirtualKey)187: // OemPlus (=/+)
                Zoom(1.5);
                e.Handled = true;
                break;
            case VirtualKey.Subtract:
            case (VirtualKey)189: // OemMinus (-/_)
                Zoom(1 / 1.5);
                e.Handled = true;
                break;
            case VirtualKey.Number0:
            case VirtualKey.NumberPad0:
                SetRating(0);
                e.Handled = true;
                break;
            case VirtualKey.Number1:
            case VirtualKey.NumberPad1:
                SetRating(1);
                e.Handled = true;
                break;
            case VirtualKey.Number2:
            case VirtualKey.NumberPad2:
                SetRating(2);
                e.Handled = true;
                break;
            case VirtualKey.Number3:
            case VirtualKey.NumberPad3:
                SetRating(3);
                e.Handled = true;
                break;
            case VirtualKey.Number4:
            case VirtualKey.NumberPad4:
                SetRating(4);
                e.Handled = true;
                break;
            case VirtualKey.Number5:
            case VirtualKey.NumberPad5:
                SetRating(5);
                e.Handled = true;
                break;
        }
    }

    private void Navigate(int delta)
    {
        if (_photos.Count == 0)
        {
            return;
        }
        _index = (_index + delta + _photos.Count) % _photos.Count;
        _ = LoadCurrentAsync();
    }

    private void Zoom(double factor)
    {
        Zoom(factor, null);
    }

    /// <summary>
    /// Zooms by <paramref name="factor"/> while keeping the point under <paramref name="anchor"/>
    /// (viewport coordinates of the ScrollViewer; null = viewport center) fixed on screen.
    /// </summary>
    private void Zoom(double factor, Windows.Foundation.Point? anchor)
    {
        double z0 = ZoomScroller.ZoomFactor;
        double z1 = Math.Clamp(z0 * factor, ZoomScroller.MinZoomFactor, ZoomScroller.MaxZoomFactor);
        if (Math.Abs(z1 - z0) < 1e-6)
        {
            return;
        }
        var p = anchor ?? new Windows.Foundation.Point(ZoomScroller.ViewportWidth / 2, ZoomScroller.ViewportHeight / 2);
        // Keep the content point under the cursor stationary across the zoom change.
        double nh = (p.X + ZoomScroller.HorizontalOffset) * (z1 / z0) - p.X;
        double nv = (p.Y + ZoomScroller.VerticalOffset) * (z1 / z0) - p.Y;
        ZoomScroller.ChangeView(nh, nv, (float)z1);
    }

    private void SetRating(int value)
    {
        if (_index < 0 || _index >= _photos.Count)
        {
            return;
        }
        var item = _photos[_index];
        // 与主页一致：点相同星级取消评级。
        int target = item.Rating == value ? 0 : value;
        item.Rating = target;
        item.Photo.Rating = target;
        NotifyPropertyChanged(nameof(Rating));
        _ = Task.Run(async () => await _db.UpsertPhotoAsync(item.Photo));
    }

    /// <summary>双击恢复到最适合比例。</summary>
    private void PhotoImage_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        FitToWindow();
    }

    private void Star_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var value))
        {
            SetRating(value);
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        CloseRequested?.Invoke();
    }
}
