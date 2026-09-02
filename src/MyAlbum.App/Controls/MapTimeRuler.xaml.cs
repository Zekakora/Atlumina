using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace MyAlbum_App.Controls;

/// <summary>
/// Horizontal time-range selector for the map (top-left corner). Shows the full photo
/// date span with year ticks/labels and two draggable handles plus a movable band.
/// Exposes <see cref="SelectedMin"/>/<see cref="SelectedMax"/> and raises
/// <see cref="RangeChanged"/> while dragging and <see cref="RangeCommitted"/> on release.
/// The band/handles use a uniform pixel→time mapping over the whole span.
/// </summary>
public sealed partial class MapTimeRuler : UserControl
{
    private const double MarginX = 12;
    private const double TrackY = 34;
    private const double TrackH = 6;
    private const double HandleW = 8;
    private const double HandleH = 18;
    private const double HitRadius = 10;

    private DateTime _minDate;   // oldest photo in the whole span
    private DateTime _maxDate;   // newest photo in the whole span
    private DateTime _selMin;    // current left handle date
    private DateTime _selMax;    // current right handle date

    private readonly Rectangle _track;
    private readonly Rectangle _band;
    private readonly Rectangle _leftHandle;
    private readonly Rectangle _rightHandle;
    private readonly List<TextBlock> _yearLabels = new();
    private readonly TextBlock _tooltip;

    private enum DragKind { None, Left, Right, Band }
    private DragKind _drag = DragKind.None;
    private double _dragOffsetX; // pointer x minus the grabbed feature's x (for band move)

    public event EventHandler? RangeChanged;
    public event EventHandler? RangeCommitted;

    public DateTime SelectedMin => _selMin;
    public DateTime SelectedMax => _selMax;

    public MapTimeRuler()
    {
        InitializeComponent();

        _track = new Rectangle
        {
            Height = TrackH,
            RadiusX = TrackH / 2,
            RadiusY = TrackH / 2,
            Fill = ThemeBrush.Resolve(this, "DividerStrokeColorDefaultBrush"),
        };
        _band = new Rectangle
        {
            Height = TrackH,
            RadiusX = TrackH / 2,
            RadiusY = TrackH / 2,
            Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(102, 0, 120, 212)),
        };
        _leftHandle = MakeHandle();
        _rightHandle = MakeHandle();
        Root.Children.Add(_track);
        Root.Children.Add(_band);
        Root.Children.Add(_leftHandle);
        Root.Children.Add(_rightHandle);

        _tooltip = new TextBlock
        {
            FontSize = 10,
            Visibility = Visibility.Collapsed,
            Foreground = ThemeBrush.Resolve(this, "TextFillColorSecondaryBrush"),
        };
        Root.Children.Add(_tooltip);

        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
        ActualThemeChanged += (_, _) =>
        {
            _track.Fill = ThemeBrush.Resolve(this, "DividerStrokeColorDefaultBrush");
            _tooltip.Foreground = ThemeBrush.Resolve(this, "TextFillColorSecondaryBrush");
            Redraw();
        };

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCanceled += (_, _) => { _drag = DragKind.None; HideTooltip(); };
        Root.PointerExited += (_, _) => { if (_drag == DragKind.None) HideTooltip(); };
    }

    private static Rectangle MakeHandle() => new()
    {
        Width = HandleW,
        Height = HandleH,
        RadiusX = 2,
        RadiusY = 2,
        Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 120, 212)),
    };

    /// <summary>Sets the full photo date span; also resets the selection to the whole span.</summary>
    public void SetRange(DateTime? min, DateTime? max)
    {
        if (min is null || max is null || max <= min)
        {
            _minDate = _maxDate = _selMin = _selMax = default;
            Redraw();
            return;
        }
        _minDate = min.Value;
        _maxDate = max.Value;
        _selMin = _minDate;
        _selMax = _maxDate;
        Redraw();
    }

    private void Reset_OnClick(object sender, RoutedEventArgs e)
    {
        _selMin = _minDate;
        _selMax = _maxDate;
        Redraw();
        RangeCommitted?.Invoke(this, EventArgs.Empty);
    }

    // ---------- pixel <-> time ----------

    private double SpanTicks => Math.Max(1, (_maxDate - _minDate).Ticks);

    private double FracOf(DateTime d) => (d - _minDate).Ticks / (double)SpanTicks;

    private double XOf(DateTime d) => MarginX + FracOf(d) * (Root.ActualWidth - 2 * MarginX);

    private DateTime DateAtX(double x)
    {
        double frac = (x - MarginX) / Math.Max(1, Root.ActualWidth - 2 * MarginX);
        frac = Math.Clamp(frac, 0, 1);
        return _minDate + TimeSpan.FromTicks((long)(SpanTicks * frac));
    }

    // ---------- interaction ----------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_minDate == default)
        {
            return;
        }
        Root.CapturePointer(e.Pointer);
        var x = e.GetCurrentPoint(Root).Position.X;
        double lx = XOf(_selMin), rx = XOf(_selMax);
        if (Math.Abs(x - lx) <= HitRadius)
        {
            _drag = DragKind.Left;
        }
        else if (Math.Abs(x - rx) <= HitRadius)
        {
            _drag = DragKind.Right;
        }
        else if (x >= lx && x <= rx)
        {
            _drag = DragKind.Band;
            _dragOffsetX = x - lx;
        }
        else
        {
            _drag = DragKind.None;
            return;
        }
        e.Handled = true;
        ShowTooltip();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == DragKind.None)
        {
            return;
        }
        var x = e.GetCurrentPoint(Root).Position.X;
        var d = DateAtX(x);
        switch (_drag)
        {
            case DragKind.Left:
                _selMin = d;
                if (_selMin > _selMax) _selMin = _selMax;
                break;
            case DragKind.Right:
                _selMax = d;
                if (_selMax < _selMin) _selMax = _selMin;
                break;
            case DragKind.Band:
            {
                double newLeft = x - _dragOffsetX;
                double delta = (newLeft - XOf(_selMin));
                long dt = (long)(delta / Math.Max(1, Root.ActualWidth - 2 * MarginX) * SpanTicks);
                _selMin += TimeSpan.FromTicks(dt);
                _selMax += TimeSpan.FromTicks(dt);
                if (_selMin < _minDate)
                {
                    _selMax -= _selMin - _minDate;
                    _selMin = _minDate;
                }
                if (_selMax > _maxDate)
                {
                    _selMin -= _selMax - _maxDate;
                    _selMax = _maxDate;
                }
                break;
            }
        }
        Redraw();
        ShowTooltip();
        RangeChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == DragKind.None)
        {
            return;
        }
        _drag = DragKind.None;
        Root.ReleasePointerCapture(e.Pointer);
        HideTooltip();
        RangeCommitted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void ShowTooltip()
    {
        double cx = (XOf(_selMin) + XOf(_selMax)) / 2;
        _tooltip.Text = $"{_selMin:yyyy-MM-dd} ~ {_selMax:yyyy-MM-dd}";
        _tooltip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = _tooltip.DesiredSize.Width;
        _tooltip.Margin = new Thickness(Math.Clamp(cx - tw / 2, 0, Math.Max(0, Root.ActualWidth - tw)), 8, 0, 0);
        _tooltip.Visibility = Visibility.Visible;
    }

    private void HideTooltip()
    {
        _tooltip.Visibility = Visibility.Collapsed;
    }

    // ---------- drawing ----------

    private void Redraw()
    {
        if (_minDate == default || Root.ActualWidth <= 2)
        {
            return;
        }
        double w = Root.ActualWidth;

        Canvas.SetLeft(_track, MarginX);
        Canvas.SetTop(_track, TrackY - TrackH / 2);
        _track.Width = w - 2 * MarginX;

        double lx = XOf(_selMin), rx = XOf(_selMax);
        Canvas.SetLeft(_band, lx);
        Canvas.SetTop(_band, TrackY - TrackH / 2);
        _band.Width = Math.Max(2, rx - lx);

        Canvas.SetLeft(_leftHandle, lx - HandleW / 2);
        Canvas.SetTop(_leftHandle, TrackY - HandleH / 2);
        Canvas.SetLeft(_rightHandle, rx - HandleW / 2);
        Canvas.SetTop(_rightHandle, TrackY - HandleH / 2);

        // Year ticks + labels at each Jan-1 on the uniform time axis.
        foreach (var label in _yearLabels)
        {
            Root.Children.Remove(label);
        }
        _yearLabels.Clear();
        var years = Enumerable.Range(_minDate.Year, _maxDate.Year - _minDate.Year + 1).ToList();
        foreach (var y in years)
        {
            double px = XOf(new DateTime(y, 1, 1));
            if (px < MarginX || px > w - MarginX)
            {
                continue;
            }
            var tick = new Rectangle
            {
                Width = 1,
                Height = 5,
                Fill = ThemeBrush.Resolve(this, "TextFillColorSecondaryBrush"),
                Opacity = 0.5,
            };
            Root.Children.Add(tick);
            Canvas.SetLeft(tick, px);
            Canvas.SetTop(tick, TrackY + TrackH / 2 + 1);

            var label = new TextBlock
            {
                Text = y.ToString(),
                FontSize = 9,
                Opacity = 0.8,
                Foreground = ThemeBrush.Resolve(this, "TextFillColorSecondaryBrush"),
            };
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Root.Children.Add(label);
            Canvas.SetLeft(label, Math.Clamp(px - label.DesiredSize.Width / 2, 0, Math.Max(0, w - label.DesiredSize.Width)));
            Canvas.SetTop(label, TrackY + TrackH / 2 + 5);
            _yearLabels.Add(label);
        }
    }
}
