using System.ComponentModel;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using MyAlbum_App.Controls;
using MyAlbum_App.Services;
using MyAlbum_App.ViewModels;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;

namespace MyAlbum_App.Pages;

/// <summary>
/// The photo browser: three-column layout with a collapsible left/right panel,
/// a top toolbar, a drag-to-scrub timeline ruler and a reserved AI reminder banner.
/// </summary>
public sealed partial class HomePage : Page
{
    /// <summary>Half the width of the edge hit zones, used to center them on the panel edge.</summary>
    private const double EdgeZoneHalf = 20;

    private readonly DispatcherTimer _leftEdgeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _rightEdgeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public HomeViewModel ViewModel { get; }
    public AppState AppState { get; }

    public HomePage()
    {
        InitializeComponent();
        // 切走再切回不重建页面：保留照片网格的滚动位置与已加载状态。
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        AppState = App.Services.GetRequiredService<AppState>();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnVmPropertyChanged;
        ViewModel.Photos.CollectionChanged += OnPhotosChanged;
        ViewModel.PhotosLoaded += OnPhotosLoaded;

        // Mirror the ruler's hover fade to the white scrim layer below the photo content.
        TimelineRuler.ActivationChanged += OnRulerActivationChanged;

        // Calendar day click → drill into that day's photos.
        CalendarControl.DayInvoked += (_, day) => ViewModel.DrillToDay(day);

        // Capture Ctrl+wheel globally over the home page, even when a child (e.g. the grid's
        // ScrollViewer) already handled the event. Plain wheel is left untouched.
        HomeRoot.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(PhotoArea_OnPointerWheelChanged),
            handledEventsToo: true);

        // Keep the hover-reveal edge buttons glued to the panel inner edges. LayoutUpdated
        // runs AFTER a column width change has taken effect, so ActualWidth is never stale
        // here (reading it right inside the collapse/expand animation callback would be).
        Loaded += (_, _) =>
        {
            UpdateEdgeButtons();
            _ = EnsureMiniMapAsync();
        };
        BodyGrid.LayoutUpdated += (_, _) => UpdateEdgeButtons();

        // A short dwell before showing the button avoids popping up while the mouse is
        // just passing over the panel edge (e.g. moving to the scrollbar or splitter).
        _leftEdgeTimer.Tick += (_, _) => { _leftEdgeTimer.Stop(); ShowEdgeButton(LeftEdgeButton); };
        _rightEdgeTimer.Tick += (_, _) => { _rightEdgeTimer.Stop(); ShowEdgeButton(RightEdgeButton); };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 页面缓存（NavigationCacheMode=Required）复用时需重新订阅。
        ViewModel.PropertyChanged -= OnVmPropertyChanged;
        ViewModel.Photos.CollectionChanged -= OnPhotosChanged;
        ViewModel.PhotosLoaded -= OnPhotosLoaded;
        ViewModel.PropertyChanged += OnVmPropertyChanged;
        ViewModel.Photos.CollectionChanged += OnPhotosChanged;
        ViewModel.PhotosLoaded += OnPhotosLoaded;
        try
        {
            await ViewModel.InitializeAsync();
            // Photos are cached across navigation; refresh place addresses written by the
            // out-of-band LLM normalization pass so the right-panel location shows immediately.
            await ViewModel.RefreshPlaceAddressesAsync();
            UpdateRulerDays();
        }
        catch (Exception ex)
        {
            // 导航往返时的 COM 竞态等不应让页面白屏；记录并继续。
            System.Diagnostics.Debug.WriteLine($"HomePage init: {ex}");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.PropertyChanged -= OnVmPropertyChanged;
        ViewModel.Photos.CollectionChanged -= OnPhotosChanged;
        ViewModel.PhotosLoaded -= OnPhotosLoaded;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.IsLeftPanelOpen):
                if (ViewModel.IsLeftPanelOpen)
                {
                    ExpandPanel(LeftPanel, LeftColumn, -1, 160, 240, LeftSplitter);
                }
                else
                {
                    CollapsePanel(LeftPanel, LeftColumn, -1, LeftSplitter);
                }
                break;
            case nameof(ViewModel.IsRightPanelOpen):
                if (ViewModel.IsRightPanelOpen)
                {
                    ExpandPanel(RightPanel, RightColumn, 1, 240, 320, RightSplitter);
                }
                else
                {
                    CollapsePanel(RightPanel, RightColumn, 1, RightSplitter);
                }
                break;
            case nameof(ViewModel.PreviewImage):
                FadeIn(PreviewImage);
                break;
            case nameof(ViewModel.SelectedPhoto):
                PushMiniMapMarker();
                break;
        }
    }

    private bool _miniMapReady;

    /// <summary>Initializes the right-panel mini-map (same virtual host + tile source as the map view).</summary>
    private async Task EnsureMiniMapAsync()
    {
        if (MiniMap.CoreWebView2 is not null)
        {
            _miniMapReady = true;
            return;
        }
        var host = await MapHostService.InitializeAsync(MiniMap, _ => { });
        if (host is not null)
        {
            var source = App.Services.GetRequiredService<AppState>().MapTileSource;
            MiniMap.CoreWebView2?.PostWebMessageAsJson($$"""{"type":"tiles","source":"{{source}}"}""");
            _miniMapReady = true;
            PushMiniMapMarker();
        }
    }

    /// <summary>Shows the selected photo's GPS as a red pin on the mini-map.</summary>
    private void PushMiniMapMarker()
    {
        if (!_miniMapReady || MiniMap.CoreWebView2 is null)
        {
            return;
        }
        var p = ViewModel.SelectedPhoto?.Photo;
        if (p is not null && p.GpsLatitude is { } lat && p.GpsLongitude is { } lon)
        {
            MiniMap.CoreWebView2.PostWebMessageAsJson($$"""{"type":"single","lat":{{lat}},"lon":{{lon}},"zoom":15}""");
        }
    }

    /// <summary>Soft opacity fade-in, ideal for swapping the preview image between photos.</summary>
    private static void FadeIn(UIElement? element)
    {
        if (element is null)
        {
            return;
        }
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.Opacity = 0f;
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, 0f);
        anim.InsertKeyFrame(1f, 1f);
        anim.Duration = TimeSpan.FromMilliseconds(180);
        anim.Target = "Opacity";
        visual.StartAnimation("Opacity", anim);
    }

    /// <summary>Ctrl + mouse wheel zooms thumbnails; plain wheel keeps normal scrolling.</summary>
    private void PhotoArea_OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        bool ctrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (!ctrlDown)
        {
            return;
        }

        // 鼠标不在照片网格区（左栏/右栏/顶部工具栏）时禁止 Ctrl+滚轮缩放。
        var pt = e.GetCurrentPoint(PhotoScroller).Position;
        if (pt.X < 0 || pt.X > PhotoScroller.ActualWidth || pt.Y < 0 || pt.Y > PhotoScroller.ActualHeight)
        {
            return;
        }

        // 以鼠标下的照片为锚点：记录光标在该瓦片内的相对位置，重建后保持光标停在同一张照片的同一相对点。
        double contentX = pt.X;
        double contentY = PhotoScroller.VerticalOffset + pt.Y;
        int anchor = ViewModel.GetPhotoIndexAt(contentX, contentY);
        double relFrac = -1;
        if (anchor >= 0)
        {
            double tileH = ViewModel.Photos[anchor].TileSize;
            if (tileH > 0)
            {
                relFrac = (contentY - ViewModel.GetPhotoOffset(anchor)) / tileH;
            }
        }
        else
        {
            anchor = ViewModel.GetAnchorPhotoIndex(PhotoScroller.VerticalOffset);
        }

        ViewModel.ChangeTileSize(e.GetCurrentPoint(this).Properties.MouseWheelDelta > 0 ? 1 : -1);

        double target = ViewModel.GetPhotoOffset(anchor);
        if (relFrac >= 0 && anchor < ViewModel.Photos.Count)
        {
            target = target + relFrac * ViewModel.Photos[anchor].TileSize - pt.Y;
        }
        RestoreScrollAfterLayout(target);
        e.Handled = true;
    }

    private bool _scrollRestorePending;
    private double _pendingScrollTarget;
    private double _lastScrollableHeight = -1;

    /// <summary>Restores the scroll once the rebuilt grid's content height has settled (avoids clamping to 0).</summary>
    /// <summary>
    /// Restores the scroll once the rebuilt grid's content height has settled (avoids clamping
    /// to 0). <paramref name="animated"/> smoothly glides back (window maximize/restore) instead
    /// of the hard jump used during Ctrl+wheel zoom.
    /// </summary>
    private void RestoreScrollAfterLayout(double target, bool animated = false)
    {
        _pendingScrollTarget = target;
        _pendingScrollAnimated = animated;
        _lastScrollableHeight = -1;
        if (!_scrollRestorePending)
        {
            PhotoScroller.LayoutUpdated += PhotoScroller_OnLayoutUpdatedForScroll;
        }
        _scrollRestorePending = true;
    }

    private bool _pendingScrollAnimated;

    private void PhotoScroller_OnLayoutUpdatedForScroll(object? sender, object e)
    {
        if (!_scrollRestorePending)
        {
            PhotoScroller.LayoutUpdated -= PhotoScroller_OnLayoutUpdatedForScroll;
            return;
        }
        double sh = PhotoScroller.ScrollableHeight;
        if (sh <= 0)
        {
            return;
        }
        if (Math.Abs(sh - _lastScrollableHeight) < 0.5)
        {
            // 内容高度已稳定：恢复滚动（目标超出范围则钳制到末尾）。
            _scrollRestorePending = false;
            PhotoScroller.LayoutUpdated -= PhotoScroller_OnLayoutUpdatedForScroll;
            PhotoScroller.ChangeView(null, Math.Min(_pendingScrollTarget, sh), null, disableAnimation: !_pendingScrollAnimated);
        }
        _lastScrollableHeight = sh;
    }

    private void OnPhotosChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // During a bulk repopulation the collection changes 20k+ times; rebuilding the day index
        // on every Add would be O(n²). The view model raises PhotosLoaded once after the batch.
        if (ViewModel.IsBulkUpdating)
        {
            return;
        }
        UpdateRulerDays();
    }

    private void OnPhotosLoaded() => UpdateRulerDays();

    private void UpdateRulerDays()
    {
        // Photos are DESC by time; group by day, each block keeps the first (newest) index.
        var blocks = new List<DayBlock>();
        for (int i = 0; i < ViewModel.Photos.Count; i++)
        {
            var day = (ViewModel.Photos[i].Photo.TakenAtUtc ?? ViewModel.Photos[i].Photo.FileModifiedUtc).Date;
            if (blocks.Count == 0 || blocks[^1].Day != day)
            {
                blocks.Add(new DayBlock(day, i, 1));
            }
            else
            {
                blocks[^1] = blocks[^1] with { Count = blocks[^1].Count + 1 };
            }
        }
        TimelineRuler.Days = blocks;
    }

    private void PhotoScroller_OnLoaded(object sender, RoutedEventArgs e) => WireRulerTarget();

    private CancellationTokenSource? _widthDebounce;

    private void PhotoScroller_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _widthDebounce?.Dispose();
        _widthDebounce = new CancellationTokenSource();
        var token = _widthDebounce.Token;

        // 记录视口顶部居中的锚点照片，及其上边相对视口顶部的偏移；
        // 窗口化/全屏切换后 GridSource 重建，滚动会重置到顶部，
        // 重建后按锚点照片的新位置 + 原偏移恢复，保持视觉位置丝滑。
        double savedOffset = PhotoScroller.VerticalOffset;
        double contentY = savedOffset + PhotoScroller.ViewportHeight * 0.25;
        int anchor = ViewModel.GetPhotoIndexAt(PhotoScroller.ViewportWidth / 2, contentY);
        double anchorRel = anchor >= 0 ? contentY - ViewModel.GetPhotoOffset(anchor) : 0;

        _ = Task.Delay(120, token).ContinueWith(_ =>
        {
            App.DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
                ViewModel.SetGridWidth(PhotoScroller.ViewportWidth);
                double target = savedOffset;
                if (anchor >= 0 && anchor < ViewModel.Photos.Count)
                {
                    target = ViewModel.GetPhotoOffset(anchor) + anchorRel;
                }
                // 窗口化/全屏切换用动画平滑回到锚点照片位置，避免硬跳。
                RestoreScrollAfterLayout(target, animated: true);
            });
        }, TaskContinuationOptions.NotOnCanceled);
        WireRulerTarget();
    }

    private void WireRulerTarget()
    {
        // The ruler replaces the native scrollbar; plain wheel scrolls normally,
        // Ctrl+wheel zooms (Ctrl+wheel isn't consumed because ZoomMode is disabled).
        PhotoScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        PhotoScroller.VerticalScrollMode = ScrollMode.Enabled;
        TimelineRuler.Target = PhotoScroller;
        TimelineRuler.JumpToIndex = ScrollToPhoto;
    }

    /// <summary>Mirrors the ruler's hover fade to the white scrim layer below the photo content.</summary>
    private void OnRulerActivationChanged(object? sender, bool active)
    {
        var anim = new DoubleAnimation
        {
            To = active ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, RulerScrim);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    /// <summary>Scrolls the adaptive grid so the row containing the photo at the given index is at the top.</summary>
    private void ScrollToPhoto(int index)
    {
        if (index < 0 || index >= ViewModel.Photos.Count)
        {
            return;
        }
        PhotoScroller.ChangeView(null, ViewModel.GetPhotoOffset(index), null, disableAnimation: true);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
            {
                return sv;
            }
            var nested = FindScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }

    /// <summary>
    /// Collapses a panel by sliding it out via a render transform (GPU, no per-frame layout),
    /// then collapses the column once the slide finishes.
    /// </summary>
    private void CollapsePanel(FrameworkElement panel, ColumnDefinition column, int direction, Thumb? splitter)
    {
        double width = column.Width.Value;
        var translate = new TranslateTransform();
        panel.RenderTransform = translate;

        var anim = new DoubleAnimation
        {
            To = direction * width,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            column.MinWidth = 0;
            column.Width = new GridLength(0);
            if (splitter is not null)
            {
                splitter.Visibility = Visibility.Collapsed;
            }
            UpdateEdgeButtons();
        };
        Storyboard.SetTarget(anim, translate);
        Storyboard.SetTargetProperty(anim, "X");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void ExpandPanel(FrameworkElement panel, ColumnDefinition column, int direction, double minWidth, double width, Thumb? splitter)
    {
        column.MinWidth = minWidth;
        column.Width = new GridLength(width);
        if (splitter is not null)
        {
            splitter.Visibility = Visibility.Visible;
        }

        var translate = new TranslateTransform { X = direction * width };
        panel.RenderTransform = translate;
        var anim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) => UpdateEdgeButtons();
        Storyboard.SetTarget(anim, translate);
        Storyboard.SetTargetProperty(anim, "X");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void LeftSplitter_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        double width = LeftColumn.ActualWidth + e.HorizontalChange;
        LeftColumn.Width = new GridLength(Math.Max(0, width));
        UpdateEdgeButtons();
    }

    private void RightSplitter_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        double width = RightColumn.ActualWidth - e.HorizontalChange;
        RightColumn.Width = new GridLength(Math.Max(0, width));
        UpdateEdgeButtons();
    }

    // ---- hover-reveal collapse/expand buttons on the panel inner edges ----

    /// <summary>
    /// Pins each edge hit zone to its panel's inner edge (the far screen edge when the
    /// panel is collapsed) and sets the button icon: a chevron pointing toward the panel
    /// means "collapse", away means "expand".
    /// </summary>
    private void UpdateEdgeButtons()
    {
        double leftW = LeftPanel.ActualWidth;
        double rightW = RightPanel.ActualWidth;
        LeftEdgeHit.Margin = new Thickness(Math.Max(0, leftW - EdgeZoneHalf), 0, 0, 0);
        RightEdgeHit.Margin = new Thickness(0, 0, Math.Max(0, rightW - EdgeZoneHalf), 0);

        bool leftOpen = ViewModel.IsLeftPanelOpen;
        bool rightOpen = ViewModel.IsRightPanelOpen;
        LeftEdgeIcon.Glyph = leftOpen ? "\uE76B" : "\uE76C";
        RightEdgeIcon.Glyph = rightOpen ? "\uE76C" : "\uE76B";
        ToolTipService.SetToolTip(LeftEdgeButton, leftOpen ? "收起左栏" : "展开左栏");
        ToolTipService.SetToolTip(RightEdgeButton, rightOpen ? "收起右栏" : "展开右栏");
    }

    private void LeftEdge_PointerEntered(object sender, PointerRoutedEventArgs e) => _leftEdgeTimer.Start();

    private void LeftEdge_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _leftEdgeTimer.Stop();
        HideEdgeButton(LeftEdgeButton);
    }

    private void RightEdge_PointerEntered(object sender, PointerRoutedEventArgs e) => _rightEdgeTimer.Start();

    private void RightEdge_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _rightEdgeTimer.Stop();
        HideEdgeButton(RightEdgeButton);
    }

    private void LeftEdgeButton_OnClick(object sender, RoutedEventArgs e)
    {
        _leftEdgeTimer.Stop();
        HideEdgeButton(LeftEdgeButton);
        ViewModel.IsLeftPanelOpen = !ViewModel.IsLeftPanelOpen;
    }

    private void RightEdgeButton_OnClick(object sender, RoutedEventArgs e)
    {
        _rightEdgeTimer.Stop();
        HideEdgeButton(RightEdgeButton);
        ViewModel.IsRightPanelOpen = !ViewModel.IsRightPanelOpen;
    }

    private static void ShowEdgeButton(Button button)
    {
        button.Visibility = Visibility.Visible;
        AnimateEdgeButtonOpacity(button, 1, onDone: null);
    }

    private static void HideEdgeButton(Button button) =>
        AnimateEdgeButtonOpacity(button, 0, () => button.Visibility = Visibility.Collapsed);

    private static void AnimateEdgeButtonOpacity(Button button, double to, Action? onDone)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (onDone is not null)
        {
            anim.Completed += (_, _) => onDone();
        }
        Storyboard.SetTarget(anim, button);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void FolderTree_OnItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is FolderTreeNode node)
        {
            ViewModel.SelectedFolderNode = node;
        }
    }

    private void LocationTree_OnItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is LocationNode node)
        {
            ViewModel.SelectedLocationNode = ReferenceEquals(ViewModel.SelectedLocationNode, node) ? null : node;
        }
    }

    private void TagList_OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TagFilterItem tag)
        {
            ViewModel.SelectedTag = ReferenceEquals(ViewModel.SelectedTag, tag) ? null : tag;
        }
    }

    private void GridTile_OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (FindPhotoItem(sender as DependencyObject) is { } item)
        {
            ViewModel.SelectPhoto(item);
        }
    }

    private void GridTile_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FindPhotoItem(e.OriginalSource as DependencyObject) is { } item)
        {
            OpenViewer(item);
        }
    }

    private void TimelineTile_OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (FindPhotoItem(e.OriginalSource as DependencyObject) is { } item)
        {
            ViewModel.SelectPhoto(item);
        }
    }

    private void TimelineTile_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FindPhotoItem(e.OriginalSource as DependencyObject) is { } item)
        {
            OpenViewer(item);
        }
    }

    /// <summary>Walks up the visual tree from the tapped element to find a PhotoGridItem data context.</summary>
    private static PhotoGridItem? FindPhotoItem(DependencyObject? start)
    {
        for (var el = start; el is not null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is FrameworkElement fe && fe.DataContext is PhotoGridItem item)
            {
                return item;
            }
        }
        return null;
    }

    private void OpenViewer(PhotoGridItem item)
    {
        var session = new ViewerSession
        {
            Photos = ViewModel.Photos.ToList(),
            StartIndex = ViewModel.Photos.IndexOf(item),
        };
        // Open a separate fullscreen window instead of navigating the main window.
        _ = new ViewerWindow(session);
    }

    // ---- card hover micro-animation (constant ~2px growth, independent of tile size) ----
    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Width > 0)
        {
            AnimateTileScale(el, (float)(1 + Math.Min(0.025, 2.0 / el.Width)));
        }
    }

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) =>
        AnimateTileScale(sender as UIElement, 1f);

    private static void AnimateTileScale(UIElement? element, float target)
    {
        if (element is null)
        {
            return;
        }
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.Target = "Scale";
        spring.InitialValue = new Vector3(visual.Scale.X, visual.Scale.Y, 1f);
        spring.FinalValue = new Vector3(target, target, 1f);
        spring.DampingRatio = 0.65f;
        spring.Period = TimeSpan.FromMilliseconds(80);
        visual.StartAnimation("Scale", spring);
    }

    private async void AddTag_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NewTagBox.Text))
        {
            await ViewModel.AddPhotoTagAsync(NewTagBox.Text);
            NewTagBox.Text = "";
        }
    }

    private async void NewTagBox_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(NewTagBox.Text))
        {
            await ViewModel.AddPhotoTagAsync(NewTagBox.Text);
            NewTagBox.Text = "";
        }
    }

    private async void RemoveTag_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            var tag = ViewModel.PhotoTags.FirstOrDefault(t => t.Name == name);
            if (tag is not null)
            {
                await ViewModel.RemovePhotoTagAsync(tag);
            }
        }
    }

    private async void EditExif_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPhoto is null)
        {
            return;
        }
        var photo = ViewModel.SelectedPhoto.Photo;
        var edit = await ShowExifEditDialogAsync(photo, isBatch: false);
        if (edit is null)
        {
            return;
        }
        var error = await ViewModel.ApplyExifEditAsync(edit);
        await ShowResultAsync(error is null ? "EXIF 已更新" : "EXIF 更新失败", error ?? "照片元数据已写入并重新索引。");
    }

    /// <summary>
    /// Builds and shows the EXIF edit dialog for one photo (or, in batch mode, a
    /// template that applies to every photo of the current filter).
    /// </summary>
    private async Task<ExifEditOptions?> ShowExifEditDialogAsync(PhotoRecord photo, bool isBatch)
    {
        if (!ViewModel.IsExifToolAvailable)
        {
            await ShowResultAsync("未找到 ExifTool",
                $"请下载 exiftool.exe 放入 {ExifWriterService.SuggestedInstallDir} 后重试。");
            return null;
        }

        var takenBox = new TextBox
        {
            Text = photo.TakenAtUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            PlaceholderText = "yyyy-MM-dd HH:mm:ss（留空不改）",
        };
        var makeBox = new TextBox { Text = photo.CameraMake ?? "", PlaceholderText = "相机品牌（留空不改）" };
        var modelBox = new TextBox { Text = photo.CameraModel ?? "", PlaceholderText = "相机型号（留空不改）" };
        var ratingBox = new TextBox
        {
            Text = photo.Rating > 0 ? photo.Rating.ToString() : "",
            PlaceholderText = "评分 0-5（留空不改）",
        };
        var latBox = new TextBox
        {
            Text = photo.GpsLatitude?.ToString("0.000000") ?? "",
            PlaceholderText = "纬度，如 31.230000（留空不改，0 清除）",
        };
        var lonBox = new TextBox
        {
            Text = photo.GpsLongitude?.ToString("0.000000") ?? "",
            PlaceholderText = "经度，如 121.470000（留空不改，0 清除）",
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = isBatch ? "批量编辑以下字段（应用到当前筛选结果）" : $"编辑 EXIF：{photo.FileName}",
            Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        panel.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "拍摄时间", Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"] },
                takenBox,
            },
        });
        panel.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "相机品牌", Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"] },
                makeBox,
            },
        });
        panel.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "相机型号", Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"] },
                modelBox,
            },
        });
        panel.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "评分", Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"] },
                ratingBox,
            },
        });
        panel.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "GPS 纬度 / 经度", Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"] },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { latBox, lonBox } },
            },
        });

        var dialog = new ContentDialog
        {
            Title = "编辑 EXIF",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        DateTime? newTaken = null;
        if (DateTime.TryParse(takenBox.Text.Trim(), out var parsed))
        {
            newTaken = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        }

        int? newRating = null;
        if (int.TryParse(ratingBox.Text.Trim(), out var r))
        {
            newRating = Math.Clamp(r, 0, 5);
        }

        bool clearGps = (latBox.Text.Trim() == "0" && lonBox.Text.Trim() == "0");
        double? newLat = !clearGps && double.TryParse(latBox.Text.Trim(), out var la) ? la : null;
        double? newLon = !clearGps && double.TryParse(lonBox.Text.Trim(), out var lo) ? lo : null;

        return new ExifEditOptions
        {
            FilePath = photo.FilePath,
            TakenAtUtc = newTaken,
            CameraMake = string.IsNullOrWhiteSpace(makeBox.Text) ? null : makeBox.Text.Trim(),
            CameraModel = string.IsNullOrWhiteSpace(modelBox.Text) ? null : modelBox.Text.Trim(),
            Rating = newRating,
            GpsLatitude = newLat,
            GpsLongitude = newLon,
            ClearGps = clearGps,
        };
    }

    private async void OpenInCameraRaw_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPhoto is null)
        {
            return;
        }
        var error = ViewModel.OpenInExternalEditor();
        if (error is not null)
        {
            await ShowResultAsync("无法打开外部编辑器", error);
        }
    }

    private async void BatchRename_OnClick(object sender, RoutedEventArgs e)
    {
        var photos = ViewModel.Photos.Select(p => p.Photo).ToList();
        if (photos.Count == 0)
        {
            await ShowResultAsync("无可重命名的照片", "当前筛选结果为空。");
            return;
        }

        var templateBox = new TextBox { Text = "{date}_{time}_{camera}_{index}", PlaceholderText = "模板，如 {date}_{time}_{camera}_{index}" };
        var sample = new TextBlock
        {
            Text = $"示例：{BatchFileService.BuildName("{date}_{time}_{camera}_{index}", photos[0], 0)}",
            Foreground = ThemeBrush.Resolve(this, "TextFillColorSecondaryBrush"),
        };
        templateBox.TextChanged += (_, _) =>
        {
            if (photos.Count > 0)
            {
                sample.Text = $"示例：{BatchFileService.BuildName(templateBox.Text, photos[0], 0)}";
            }
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = $"将重命名 {photos.Count} 张照片。支持 {{date}} {{time}} {{year}} {{month}} {{day}} {{camera}} {{index}} {{ext}} {{name}}。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(templateBox);
        panel.Children.Add(sample);

        var dialog = new ContentDialog
        {
            Title = "批量重命名",
            Content = panel,
            PrimaryButtonText = "重命名",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var batch = new BatchFileService(App.Services.GetRequiredService<MyAlbum.Core.Data.PhotoDatabase>());
        var progress = new Progress<(int Done, int Total, string File)>(p => ViewModel.StatusText = $"重命名 {p.Done}/{p.Total}");
        var results = await batch.RenameBatchAsync(photos, templateBox.Text.Trim(), progress);
        int ok = results.Count(r => r.Success);
        int failed = results.Count - ok;
        await ViewModel.RefreshAsync();
        await ShowResultAsync($"重命名完成：成功 {ok}，失败 {failed}",
            failed == 0 ? null : string.Join("\n", results.Where(r => !r.Success).Take(5).Select(r => $"{Path.GetFileName(r.OldPath)}: {r.Message}")));
    }

    private async void BatchExport_OnClick(object sender, RoutedEventArgs e)
    {
        var photos = ViewModel.Photos.Select(p => p.Photo).ToList();
        if (photos.Count == 0)
        {
            await ShowResultAsync("无可导出的照片", "当前筛选结果为空。");
            return;
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Window.AppWindow.Id)
        {
            Title = "选择导出目录",
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
        };
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        ViewModel.IsBusy = true;
        try
        {
            var batch = new BatchFileService(App.Services.GetRequiredService<MyAlbum.Core.Data.PhotoDatabase>());
            var progress = new Progress<(int Done, int Total, string File)>(p => ViewModel.StatusText = $"导出 {p.Done}/{p.Total}");
            var results = await batch.ExportBatchAsync(photos, folder.Path, progress);
            int ok = results.Count(r => r.Success);
            int failed = results.Count - ok;
            await ShowResultAsync($"导出完成：成功 {ok}，失败 {failed}",
                failed == 0 ? null : string.Join("\n", results.Where(r => !r.Success).Take(5).Select(r => $"{Path.GetFileName(r.SourcePath)}: {r.Message}")));
        }
        finally
        {
            ViewModel.IsBusy = false;
            ViewModel.StatusText = "就绪";
        }
    }

    private void FindDuplicates_OnClick(object sender, RoutedEventArgs e)
    {
        _ = new DedupToolWindow(ViewModel.Photos.Select(p => p.Photo).ToList());
    }

    private void GpsTool_OnClick(object sender, RoutedEventArgs e)
    {
        _ = new GpsToolWindow();
    }

    private void FixPhotoDates_OnClick(object sender, RoutedEventArgs e)
    {
        _ = new DateFixToolWindow(ViewModel.Photos.Select(p => p.Photo).ToList());
    }

    private void DbFileDiff_OnClick(object sender, RoutedEventArgs e)
    {
        _ = new DbFileDiffWindow();
    }

    private void FormatCleanup_OnClick(object sender, RoutedEventArgs e)
    {
        _ = new FormatCleanupToolWindow(ViewModel.Photos.Select(p => p.Photo).ToList());
    }

    private void QualityCleanup_OnClick(object sender, RoutedEventArgs e)
    {
        _ = new QualityToolWindow();
    }

    private async Task ShowResultAsync(string title, string? message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
