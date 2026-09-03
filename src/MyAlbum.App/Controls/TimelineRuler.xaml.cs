using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace MyAlbum_App.Controls;

/// <summary>A photo day in the day index: its date and the index of its first (newest) photo.</summary>
public readonly record struct DayBlock(DateTime Day, int FirstIndexInDesc, int Count);

/// <summary>
/// Desktop timeline scrubber overlaid on the photo area.
/// - Inactive → faint hairline only. Hover → ruler fades in (year/month ticks) and the
///   tooltip shows the month. Drag → immediate scroll to the day + day-precision tooltip.
/// - Uniform pixel→date mapping over the whole time span; day lookup via binary search.
/// </summary>
public sealed partial class TimelineRuler : UserControl
{
    private const double TilePitch = 188;

    private ScrollViewer? _target;
    private List<DayBlock> _days = new();
    private bool _dragging;

    private double TrackHeight => Math.Max(1, Root.ActualHeight - Thumb.Height);

    public TimelineRuler()
    {
        InitializeComponent();
        SizeChanged += (_, _) => { RedrawTicks(); RefreshFromScroll(); };
        Loaded += (_, _) => { RedrawTicks(); RefreshFromScroll(); };
        ActualThemeChanged += (_, _) => { RedrawTicks(); RefreshFromScroll(); };

        HitStrip.PointerEntered += (_, e) => Safe(() => OnActivated(e));
        HitStrip.PointerExited += (_, _) => { if (!_dragging) Safe(OnDeactivated); };
        HitStrip.PointerPressed += (s, e) => Safe(() => OnPointerPressed(s, e));
        HitStrip.PointerMoved += (s, e) => Safe(() => OnPointerMoved(s, e));
        HitStrip.PointerReleased += (s, e) => Safe(() => OnPointerReleased(s, e));
        HitStrip.PointerCanceled += (_, _) => { _dragging = false; Safe(OnDeactivated); };
    }

    /// <summary>Swallows any exception from interaction code so the ruler can never crash the app.</summary>
    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // the timeline scrubber is cosmetic; never let it take the app down
        }
    }

    /// <summary>The scroll container this scrubber controls (the active photo list's ScrollViewer).</summary>
    public ScrollViewer? Target
    {
        get => _target;
        set
        {
            if (_target is not null)
            {
                _target.ViewChanged -= OnTargetViewChanged;
            }
            _target = value;
            if (_target is not null)
            {
                _target.ViewChanged += OnTargetViewChanged;
            }
            RefreshFromScroll();
        }
    }

    /// <summary>Day index (ascending) for the current photo list; each day holds the first photo index in the DESC list.</summary>
    public IReadOnlyList<DayBlock> Days
    {
        set
        {
            _days = value.ToList();
            // Defer until the control has its final layout size, so tick/label positions are correct.
            DispatcherQueue.TryEnqueue(() =>
            {
                RedrawTicks();
                RefreshFromScroll();
            });
        }
    }

    // ---------- scroll sync ----------

    private void OnTargetViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_dragging)
        {
            return;
        }
        RefreshFromScroll();
    }

    private void RefreshFromScroll()
    {
        if (_target is null || _target.ScrollableHeight <= 0)
        {
            Thumb.Visibility = Visibility.Collapsed;
            return;
        }
        Thumb.Visibility = Visibility.Visible;
        double viewportFraction = _target.ViewportHeight / Math.Max(1, _target.ViewportHeight + _target.ScrollableHeight);
        Thumb.Height = Math.Max(26, Root.ActualHeight * viewportFraction);

        // Position the thumb by the TIME of the day currently at the top of the viewport
        // (not the raw scroll fraction). This is the same time coordinate the drag path
        // uses, so the thumb never jumps when the user lets go of a scrub.
        double scrollFrac = _target.VerticalOffset / Math.Max(1, _target.ScrollableHeight);
        double timeFrac = VisibleDayFractionAtScroll(scrollFrac);
        Thumb.Margin = new Thickness(0, timeFrac * TrackHeight, 11, 0);
    }

    /// <summary>Total number of photos covered by the day index (last day's last photo + 1).</summary>
    private int TotalPhotos => _days.Count > 0 ? _days[^1].FirstIndexInDesc + _days[^1].Count : 1;

    /// <summary>
    /// Index-space fraction [0,1] of a photo index along the track. The photo grid is laid
    /// out by photo index (each cell equal), so this is what actually matches the left content.
    /// 0 = top (newest photo), 1 = bottom (oldest photo).
    /// </summary>
    private double IndexFractionOf(int photoIndex)
    {
        return photoIndex / (double)Math.Max(1, TotalPhotos);
    }

    /// <summary>Maps a scroll fraction to the index fraction of the photo at the top of the viewport.</summary>
    private double VisibleDayFractionAtScroll(double scrollFrac)
    {
        if (_days.Count == 0)
        {
            return 0;
        }
        int total = TotalPhotos;
        int indexAtTop = (int)Math.Clamp(scrollFrac * total, 0, total - 1);
        return IndexFractionOf(indexAtTop);
    }

    /// <summary>Index of the day block (ascending by FirstIndexInDesc) whose range contains <paramref name="index"/>.</summary>
    private int DayIndexForPhotoIndex(int index)
    {
        int lo = 0, hi = _days.Count - 1, best = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_days[mid].FirstIndexInDesc <= index)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return best;
    }

    // ---------- activation (hover) ----------

    private void OnActivated(PointerRoutedEventArgs e)
    {
        AnimateOpacity(RulerVisual, 1, 140);
        AnimateOpacity(Scrim, 0.9, 140);
        var y = e.GetCurrentPoint(this).Position.Y;
        ShowTooltipAtPointer(y, dayPrecision: false);
    }

    private void OnDeactivated()
    {
        AnimateOpacity(RulerVisual, 0, 140);
        AnimateOpacity(Scrim, 0.5, 140);
        HideTooltip();
    }

    // ---------- interaction ----------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        HitStrip.CapturePointer(e.Pointer);
        _dragging = true;
        var y = e.GetCurrentPoint(this).Position.Y;
        ScrubTo(y);
        ShowTooltipAtPointer(y, dayPrecision: true);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(this).Position.Y;
        if (_dragging)
        {
            ScrubTo(y);
            ShowTooltipAtPointer(y, dayPrecision: true);
        }
        else
        {
            ShowTooltipAtPointer(y, dayPrecision: false);
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        HitStrip.ReleasePointerCapture(e.Pointer);
        var y = e.GetCurrentPoint(this).Position.Y;
        ScrubTo(y);
        ShowTooltipAtPointer(y, dayPrecision: true);
        FadeTooltipOut();
        e.Handled = true;
    }

    /// <summary>Called to scroll the photo list to a photo index (newest-first). Set by the host page.</summary>
    public Action<int>? JumpToIndex;

    /// <summary>Uniform pixel→photo mapping: scroll the grid to the photo under the pointer.</summary>
    private void ScrubTo(double pointerY)
    {
        if (_days.Count == 0)
        {
            return;
        }
        double frac = Math.Clamp(pointerY / Math.Max(1, Root.ActualHeight), 0, 1);
        int idx = TargetPhotoIndexAtFraction(frac);
        // Snap the thumb to the photo's index position so releasing the drag never shifts it.
        Thumb.Margin = new Thickness(0, IndexFractionOf(idx) * TrackHeight, 12, 0);

        try
        {
            if (JumpToIndex is not null)
            {
                JumpToIndex(idx);
            }
            else if (_target is not null)
            {
                double offset = IndexToScrollOffset(idx);
                _target.ChangeView(null, offset, null, disableAnimation: true);
            }
        }
        catch
        {
            // scrolling the grid is best-effort; never crash the app
        }
    }

    /// <summary>
    /// Photo index (DESC order) under the pointer fraction, with exact-photo precision.
    /// Photo-level (rather than day-first) mapping lets a scrub still slide within a single
    /// day — a filtered set that all falls on one day previously always jumped back to photo 0.
    /// </summary>
    private int TargetPhotoIndexAtFraction(double frac)
    {
        int total = TotalPhotos;
        return (int)Math.Clamp(frac * total, 0, total - 1);
    }

    private DayBlock TargetDayAtFraction(double frac)
    {
        int idx = TargetPhotoIndexAtFraction(frac);
        return _days[DayIndexForPhotoIndex(idx)];
    }

    /// <summary>Index of the day block whose date is closest to <paramref name="target"/>.</summary>
    private int DayIndexForDate(DateTime target)
    {
        int lo = 0, hi = _days.Count - 1;
        int best = 0;
        long bestDist = long.MaxValue;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            long dist = Math.Abs((_days[mid].Day - target).Ticks);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = mid;
            }
            if (_days[mid].Day > target)
            {
                lo = mid + 1; // mid is newer than target → go toward older days
            }
            else
            {
                hi = mid - 1;
            }
        }
        return best;
    }

    private double IndexToScrollOffset(int index)
    {
        if (_target is null || _target.ScrollableHeight <= 0)
        {
            return 0;
        }
        int total = _days.Count > 0 ? _days[^1].FirstIndexInDesc + _days[^1].Count : 1;
        return _target.ScrollableHeight * (index / (double)Math.Max(1, total));
    }

    // ---------- tooltip (month on hover, day on drag) ----------

    private void ShowTooltipAtPointer(double pointerY, bool dayPrecision)
    {
        if (_days.Count == 0)
        {
            return;
        }
        double frac = Math.Clamp(pointerY / Math.Max(1, Root.ActualHeight), 0, 1);
        var day = TargetDayAtFraction(frac);
        TooltipText.Text = dayPrecision ? day.Day.ToString("yyyy年MM月dd日") : day.Day.ToString("yyyy年MM月");

        TooltipCard.Visibility = Visibility.Visible;
        TooltipCard.Margin = new Thickness(0, Math.Clamp(pointerY - 14, 0, Math.Max(0, Root.ActualHeight - 30)), 34, 0);
        TooltipCard.Opacity = 1;
    }

    private void HideTooltip()
    {
        TooltipCard.Visibility = Visibility.Collapsed;
    }

    private void FadeTooltipOut()
    {
        var anim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(150),
        };
        anim.Completed += (_, _) => TooltipCard.Visibility = Visibility.Collapsed;
        Storyboard.SetTarget(anim, TooltipCard);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    // ---------- year / month ticks + year labels ----------

    private void RedrawTicks()
    {
        TickCanvas.Children.Clear();
        LabelCanvas.Children.Clear();
        double h = Root.ActualHeight;
        if (h <= 2)
        {
            h = LabelCanvas.ActualHeight;
        }
        if (h <= 2 || _days.Count == 0)
        {
            return;
        }
        // _days is DESCENDING (newest photo day first). Top of the ruler = newest photo.
        // Positions are index-space so the ticks/labels line up with the left grid.
        int total = TotalPhotos;
        double Frac(int photoIndex) => photoIndex / (double)Math.Max(1, total);

        var newest = _days[0].Day;
        var oldest = _days[^1].Day;

        // Month ticks (minor). Only draw them for short-ish spans; over many years they crowd the track.
        bool drawMonths = _days.Count <= 366 * 8;
        if (drawMonths)
        {
            var cursor = new DateTime(oldest.Year, oldest.Month, 1);
            while (cursor <= newest)
            {
                if (cursor.Month != 1)
                {
                    int k = DayIndexForDate(cursor);
                    AddTick(Frac(_days[k].FirstIndexInDesc) * h, major: false);
                }
                cursor = cursor.AddMonths(1);
            }
        }

        // Year labels: each year sits at its Jan-1 position in index-space (the day nearest that boundary).
        var years = _days.Select(d => d.Day.Year).Distinct().OrderByDescending(y => y).ToList();
        foreach (var y in years)
        {
            int k = DayIndexForDate(new DateTime(y, 1, 1));
            double py = Frac(_days[k].FirstIndexInDesc) * h;
            // A single year covers the whole ruler (its Jan-1 is below the oldest photo),
            // so pin the label at the top where the newest edge is.
            if (years.Count == 1)
            {
                py = 0;
            }
            AddTick(py, major: true);
            AddYearLabel(py, y);
        }
    }

    private void AddTick(double y, bool major)
    {
        var line = new Rectangle
        {
            Width = major ? 18 : 9,
            Height = major ? 2 : 1,
            Fill = ThemeBrush.Resolve(this, "TextFillColorSecondaryBrush"),
            Opacity = major ? 0.9 : 0.4,
        };
        TickCanvas.Children.Add(line);
        Canvas.SetTop(line, Math.Clamp(y, 0, Math.Max(0, Root.ActualHeight - 1)));
        Canvas.SetLeft(line, 0);
    }

    private void AddYearLabel(double y, int year)
    {
        var label = new TextBlock
        {
            Text = year.ToString(),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.95,
            Foreground = ThemeBrush.Resolve(this, "TextFillColorSecondaryBrush"),
        };
        label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        LabelCanvas.Children.Add(label);
        Canvas.SetLeft(label, Math.Max(0, LabelCanvas.Width - label.DesiredSize.Width)); // hug the track
        Canvas.SetTop(label, Math.Clamp(y - label.DesiredSize.Height / 2, 0, Math.Max(0, Root.ActualHeight - label.DesiredSize.Height)));
    }

    private static void AnimateOpacity(FrameworkElement element, double to, int ms)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, element);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }
}
