using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MyAlbum_App.Services;

/// <summary>
/// Builds severity-styled confirmation dialogs with solid backgrounds: amber/orange for
/// warnings, red for destructive actions. The red variant holds back its confirm button
/// for a few seconds (countdown) so a misclick cannot confirm immediately. The confirm and
/// cancel buttons are placed on the same row inside the dialog.
///
/// The solid background is applied via the dialog's theme resources
/// ("ContentDialogBackground" / "ContentDialogBorderBrush") because WinUI 3's ContentDialog
/// template does not honor the plain <c>Background</c> property. Text colors are set
/// explicitly to keep contrast on the colored background in either app theme.
/// </summary>
public static class ConfirmationDialog
{
    // Amber/orange warning: bright solid amber with dark text (best contrast).
    private static readonly Color WarningBg = Color.FromArgb(0xFF, 0xF5, 0xA6, 0x23);
    private static readonly Color WarningText = Color.FromArgb(0xFF, 0x3B, 0x2B, 0x00);
    private static readonly Color WarningBorder = Color.FromArgb(0xFF, 0xC0, 0x7A, 0x00);

    // Red critical: solid red with white text.
    private static readonly Color CriticalBg = Color.FromArgb(0xFF, 0xB7, 0x1C, 0x1C);
    private static readonly Color CriticalBorder = Color.FromArgb(0xFF, 0x8E, 0x13, 0x12);
    private static readonly Color CriticalText = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color CriticalIcon = Color.FromArgb(0xFF, 0xFF, 0xCD, 0xD2);

    /// <summary>Amber/orange warning dialog with a solid background (used by 恢复数据库).</summary>
    public static ContentDialog Warning(XamlRoot root, string title, string message, string confirm, string cancel)
        => Build(root, title, message, confirm, cancel, WarningBg, WarningBorder, WarningText, WarningText, ElementTheme.Light);

    /// <summary>Red critical dialog with a solid background (used by 重置数据库).</summary>
    public static ContentDialog Critical(XamlRoot root, string title, string message, string confirm, string cancel)
        => Build(root, title, message, confirm, cancel, CriticalBg, CriticalBorder, CriticalText, CriticalIcon, ElementTheme.Dark);

    /// <summary>
    /// Red critical dialog whose confirm button is disabled for <paramref name="delaySeconds"/>
    /// seconds (countdown shown). Confirming sets <c>dialog.Tag = true</c> before closing, so
    /// the caller checks <c>dialog.Tag is true</c> to decide whether to proceed. The confirm
    /// and cancel buttons share one row inside the dialog content.
    /// </summary>
    public static ContentDialog CriticalCountdown(XamlRoot root, string title, string message, string confirm, string cancel, int delaySeconds = 3)
    {
        var panel = new StackPanel { Spacing = 14, MinWidth = 400, MaxWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(CriticalText),
        });

        var countdown = new TextBlock
        {
            Text = $"确认按钮将在 {delaySeconds} 秒后可用",
            FontSize = 11,
            Foreground = new SolidColorBrush(CriticalIcon),
        };
        panel.Children.Add(countdown);

        var confirmButton = new Button
        {
            Content = confirm,
            IsEnabled = false,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(CriticalBg),
            FontWeight = FontWeights.SemiBold,
            MinWidth = 96,
        };
        var cancelButton = new Button
        {
            Content = cancel,
            MinWidth = 96,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(CriticalText),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(confirmButton);
        panel.Children.Add(buttons);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = BuildTitle(title, CriticalIcon, CriticalText),
            Content = panel,
            // Force a known theme so text stays readable even if the background
            // resource override below is ever ignored (worst case: dark default bg
            // with white text).
            RequestedTheme = ElementTheme.Dark,
        };
        ApplySolidBackground(dialog, CriticalBg, CriticalBorder);

        int remaining = delaySeconds;
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0)
            {
                timer.Stop();
                confirmButton.IsEnabled = true;
                countdown.Visibility = Visibility.Collapsed;
            }
            else
            {
                countdown.Text = $"确认按钮将在 {remaining} 秒后可用";
            }
        };
        timer.Start();
        dialog.Closed += (_, _) => timer.Stop();
        confirmButton.Click += (_, _) =>
        {
            dialog.Tag = true;
            dialog.Hide();
        };
        cancelButton.Click += (_, _) => dialog.Hide();
        return dialog;
    }

    private static ContentDialog Build(XamlRoot root, string title, string message, string confirm, string cancel,
        Color background, Color border, Color text, Color icon, ElementTheme theme)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = BuildTitle(title, icon, text),
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 480,
                Foreground = new SolidColorBrush(text),
            },
            PrimaryButtonText = confirm,
            CloseButtonText = cancel,
            DefaultButton = ContentDialogButton.Close,
            // 跟随背景的明暗：黄色警告用浅色主题（深字），红色危险用深色主题（白字）。
            // 即使背景覆盖失效，文字也始终与主题底色对比可读。
            RequestedTheme = theme,
        };
        ApplySolidBackground(dialog, background, border);
        return dialog;
    }

    private static void ApplySolidBackground(ContentDialog dialog, Color background, Color border)
    {
        var bgBrush = new SolidColorBrush(background);
        var borderBrush = new SolidColorBrush(border);
        dialog.Background = bgBrush;
        // The ContentDialog template reads these theme resources, not "Background".
        dialog.Resources["ContentDialogBackground"] = bgBrush;
        dialog.Resources["ContentDialogBorderBrush"] = borderBrush;
        dialog.Resources["ContentDialogBorderWidth"] = 1.0;
    }

    private static StackPanel BuildTitle(string title, Color icon, Color text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon
        {
            Glyph = "\uE7BA",
            FontSize = 16,
            Foreground = new SolidColorBrush(icon),
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(text),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }
}
