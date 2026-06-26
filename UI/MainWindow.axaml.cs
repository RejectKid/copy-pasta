using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace CopyPasta;

public partial class MainWindow : Window
{
    private const int DefaultTypingDelayMs = 12;

    private readonly HistoryStore _historyStore = new();
    private readonly ObservableCollection<HistoryEntry> _history = [];
    private readonly IGlobalHotkeyService _hotkeys = PlatformServices.CreateHotkeyService();
    private readonly ISelectionCaptureService _selectionCapture = PlatformServices.CreateSelectionCaptureService();
    private readonly ITextOutputService _textOutput = PlatformServices.CreateTextOutputService();
    private readonly IContextMenuIntegrationService _contextMenus = PlatformServices.CreateContextMenuIntegrationService();

    private bool _isTyping;
    private CancellationTokenSource? _typingCancellation;

    public MainWindow()
    {
        InitializeComponent();

        HistoryList.ItemsSource = _history;
        LoadHistory();
        AddStartupHistoryTexts();

        Opened += MainWindow_Opened;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        _hotkeys.HotkeyPressed += Hotkeys_HotkeyPressed;

        if (!_hotkeys.Register(this, out var error))
        {
            SetStatus(error ?? "Global hotkeys are not available on this platform.");
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        StopTyping();
        _hotkeys.HotkeyPressed -= Hotkeys_HotkeyPressed;
        _hotkeys.Dispose();
    }

    private void Hotkeys_HotkeyPressed(object? sender, CopyPastaHotkey hotkey)
    {
        switch (hotkey)
        {
            case CopyPastaHotkey.Capture:
                CaptureTextSelection();
                break;
            case CopyPastaHotkey.Type:
                _ = TypeSelectedAsync();
                break;
            case CopyPastaHotkey.Stop:
                StopTyping();
                break;
        }
    }

    private void RemoveButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RemoveSelected();
    }

    private void ClearButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ClearHistory();
    }

    private async void ContextMenusButton_Click(object? sender, RoutedEventArgs e)
    {
        await ShowContextMenuSettingsAsync();
    }

    private void HistoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ShowSelectedPreview();
    }

    private void LoadHistory()
    {
        foreach (var entry in _historyStore.Load())
        {
            _history.Add(entry);
        }

        if (_history.Count > 0)
        {
            HistoryList.SelectedIndex = 0;
        }
        else
        {
            ShowSelectedPreview();
        }
    }

    private void AddStartupHistoryTexts()
    {
        foreach (var text in Program.StartupHistoryTexts.Distinct(StringComparer.Ordinal))
        {
            AddOrPromoteHistoryEntry(
                new HistoryEntry
                {
                    Text = text,
                    CapturedAt = DateTimeOffset.Now
                },
                "Explorer context menu",
                "Added");
        }
    }

    private void CaptureTextSelection()
    {
        var result = _selectionCapture.TryCapture(out var error);
        if (result is null)
        {
            SetStatus(error ?? "No supported selection found.");
            return;
        }

        AddOrPromoteHistoryEntry(
            new HistoryEntry
            {
                Text = result.Text,
                Style = result.Style,
                CapturedAt = DateTimeOffset.Now
            },
            result.Source,
            "Captured");
    }

    private void AddOrPromoteHistoryEntry(HistoryEntry newEntry, string source, string action)
    {
        if (!newEntry.HasContent)
        {
            SetStatus("Selection was empty.");
            return;
        }

        var existing = _history.FirstOrDefault(entry => HasSameContent(entry, newEntry));
        if (existing is not null)
        {
            _history.Remove(existing);
            existing.CapturedAt = DateTimeOffset.Now;
            _history.Insert(0, existing);
        }
        else
        {
            newEntry.CapturedAt = DateTimeOffset.Now;
            _history.Insert(0, newEntry);

            while (_history.Count > HistoryStore.MaxItems)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }

        HistoryList.SelectedIndex = 0;
        _historyStore.Save(_history);
        SetStatus($"{action} {DescribeEntry(newEntry)} from {source}.");
    }

    private async Task ShowContextMenuSettingsAsync()
    {
        var titleText = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        };
        var statusText = new TextBlock
        {
            FontWeight = FontWeight.SemiBold
        };
        var detailText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        var installButton = new Button
        {
            Content = "Install",
            MinWidth = 92,
            Padding = new Thickness(12, 6)
        };
        var removeButton = new Button
        {
            Content = "Remove",
            MinWidth = 92,
            Padding = new Thickness(12, 6)
        };
        var closeButton = new Button
        {
            Content = "Close",
            MinWidth = 92,
            Padding = new Thickness(12, 6)
        };

        var dialog = new Window
        {
            Title = "Context menus",
            Width = 460,
            Height = 250,
            MinWidth = 420,
            MinHeight = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 10,
                Children =
                {
                    titleText,
                    statusText,
                    detailText,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 18, 0, 0),
                        Children =
                        {
                            installButton,
                            removeButton,
                            closeButton
                        }
                    }
                }
            }
        };

        void Refresh()
        {
            var status = _contextMenus.GetStatus();
            titleText.Text = status.Title;
            statusText.Text = status.IsInstalled ? "Installed" : "Not installed";
            detailText.Text = status.Detail;
            installButton.IsEnabled = status.IsSupported;
            removeButton.IsEnabled = status.IsSupported && status.IsInstalled;
        }

        installButton.Click += (_, _) =>
        {
            var result = _contextMenus.Install();
            SetStatus(result.Message);
            Refresh();
        };
        removeButton.Click += (_, _) =>
        {
            var result = _contextMenus.Uninstall();
            SetStatus(result.Message);
            Refresh();
        };
        closeButton.Click += (_, _) => dialog.Close();

        Refresh();
        await dialog.ShowDialog(this);
    }

    private async Task TypeSelectedAsync()
    {
        if (_isTyping)
        {
            return;
        }

        var entry = SelectedEntry();
        if (entry is null)
        {
            SetStatus("No history item selected.");
            return;
        }

        _typingCancellation = new CancellationTokenSource();
        _isTyping = true;

        try
        {
            SetStatus("Release Ctrl+Alt+V to continue.");
            await _textOutput.WaitForTypeHotkeyReleaseAsync(_typingCancellation.Token);

            var lastProgress = 0;
            var progress = new Progress<int>(count =>
            {
                if (count - lastProgress >= 25 || count == entry.Text.Length)
                {
                    lastProgress = count;
                    SetStatus($"Typing {count:N0}/{entry.Text.Length:N0} characters...");
                }
            });

            await _textOutput.TypeTextAsync(entry.Text, DefaultTypingDelayMs, _typingCancellation.Token, progress);
            SetStatus($"Typed {entry.Text.Length:N0} characters.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Typing stopped.");
        }
        catch (Exception ex)
        {
            SetStatus($"Typing failed: {ex.Message}");
        }
        finally
        {
            _typingCancellation?.Dispose();
            _typingCancellation = null;
            _isTyping = false;
        }
    }

    private void StopTyping()
    {
        _typingCancellation?.Cancel();
    }

    private void RemoveSelected()
    {
        var index = HistoryList.SelectedIndex;
        if (index < 0 || index >= _history.Count)
        {
            return;
        }

        _history.RemoveAt(index);
        if (_history.Count > 0)
        {
            HistoryList.SelectedIndex = Math.Min(index, _history.Count - 1);
        }

        _historyStore.Save(_history);
        ShowSelectedPreview();
        SetStatus("Removed history item.");
    }

    private void ClearHistory()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _history.Clear();
        _historyStore.Save(_history);
        ShowSelectedPreview();
        SetStatus("History cleared.");
    }

    private void ShowSelectedPreview()
    {
        var entry = SelectedEntry();
        if (entry is null)
        {
            PreviewText.Text = string.Empty;
            DetailsText.Text = "0 characters";
            return;
        }

        PreviewText.Text = entry.Text;
        ApplyPreviewStyle(entry.Style);

        var styleDetails = FormatStyleDetails(entry.Style);
        DetailsText.Text = $"{entry.Text.Length:N0} characters captured {entry.CapturedAt.LocalDateTime:g}{styleDetails}";
    }

    private HistoryEntry? SelectedEntry()
    {
        return HistoryList.SelectedItem as HistoryEntry;
    }

    private static string DescribeEntry(HistoryEntry entry)
    {
        return $"{entry.Text.Length:N0} character {(entry.HasStyle ? "rich text" : "text")} item";
    }

    private static bool HasSameContent(HistoryEntry left, HistoryEntry right)
    {
        return left.Text == right.Text &&
            StyleValue(left.Style?.FontName) == StyleValue(right.Style?.FontName) &&
            left.Style?.FontSize == right.Style?.FontSize &&
            left.Style?.FontWeight == right.Style?.FontWeight &&
            left.Style?.IsItalic == right.Style?.IsItalic &&
            left.Style?.ForegroundColor == right.Style?.ForegroundColor &&
            left.Style?.BackgroundColor == right.Style?.BackgroundColor;
    }

    private static string StyleValue(string? value) => value ?? string.Empty;

    private void ApplyPreviewStyle(TextStyleSnapshot? style)
    {
        PreviewText.FontFamily = new FontFamily("Consolas");
        PreviewText.FontSize = 13;
        PreviewText.FontWeight = FontWeight.Normal;
        PreviewText.FontStyle = FontStyle.Normal;
        PreviewText.Foreground = Brushes.Black;
        PreviewText.Background = Brushes.White;

        if (style is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(style.FontName))
        {
            PreviewText.FontFamily = new FontFamily(style.FontName);
        }

        if (style.FontSize is > 0)
        {
            PreviewText.FontSize = style.FontSize.Value;
        }

        if (style.FontWeight.HasValue)
        {
            PreviewText.FontWeight = style.FontWeight.Value >= 700 ? FontWeight.Bold : FontWeight.Normal;
        }

        if (style.IsItalic.HasValue)
        {
            PreviewText.FontStyle = style.IsItalic.Value ? FontStyle.Italic : FontStyle.Normal;
        }

        if (style.ForegroundColor.HasValue)
        {
            PreviewText.Foreground = new SolidColorBrush(ColorFromColorRef(style.ForegroundColor.Value));
        }

        if (style.BackgroundColor.HasValue)
        {
            PreviewText.Background = new SolidColorBrush(ColorFromColorRef(style.BackgroundColor.Value));
        }
    }

    private static Color ColorFromColorRef(int colorRef)
    {
        var red = (byte)(colorRef & 0xFF);
        var green = (byte)((colorRef >> 8) & 0xFF);
        var blue = (byte)((colorRef >> 16) & 0xFF);
        return Color.FromRgb(red, green, blue);
    }

    private static string FormatStyleDetails(TextStyleSnapshot? style)
    {
        if (style is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(style.FontName))
        {
            parts.Add(style.FontName);
        }

        if (style.FontSize is > 0)
        {
            parts.Add($"{style.FontSize.Value:N0} pt");
        }

        if (style.FontWeight is >= 700)
        {
            parts.Add("bold");
        }

        if (style.IsItalic == true)
        {
            parts.Add("italic");
        }

        return parts.Count == 0 ? string.Empty : $" | {string.Join(", ", parts)}";
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}
