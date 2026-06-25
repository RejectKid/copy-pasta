using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CopyPasta;

public partial class MainWindow : Window
{
    private const int CaptureHotKeyId = 100;
    private const int TypeHotKeyId = 101;
    private const int StopHotKeyId = 102;
    private const int DefaultTypingDelayMs = 12;

    private const uint VirtualKeyC = 0x43;
    private const uint VirtualKeyV = 0x56;
    private const uint VirtualKeyX = 0x58;

    private readonly HistoryStore _historyStore = new();
    private readonly ObservableCollection<HistoryEntry> _history = [];

    private HwndSource? _source;
    private bool _hotkeysRegistered;
    private bool _isTyping;
    private CancellationTokenSource? _typingCancellation;

    public MainWindow()
    {
        InitializeComponent();

        HistoryList.ItemsSource = _history;
        LoadHistory();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var helper = new WindowInteropHelper(this);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
        RegisterHotKeys(helper.Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopTyping();
        UnregisterHotKeys();
        _source?.RemoveHook(WndProc);
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotKey)
        {
            return IntPtr.Zero;
        }

        switch (wParam.ToInt32())
        {
            case CaptureHotKeyId:
                CaptureTextSelection();
                break;
            case TypeHotKeyId:
                _ = TypeSelectedAsync();
                break;
            case StopHotKeyId:
                StopTyping();
                break;
        }

        handled = true;
        return IntPtr.Zero;
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveSelected();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearHistory();
    }

    private void HistoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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

    private void RegisterHotKeys(IntPtr handle)
    {
        if (_hotkeysRegistered)
        {
            return;
        }

        var modifiers = NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat;
        var captureRegistered = NativeMethods.RegisterHotKey(handle, CaptureHotKeyId, modifiers, VirtualKeyC);
        var typeRegistered = NativeMethods.RegisterHotKey(handle, TypeHotKeyId, modifiers, VirtualKeyV);
        var stopRegistered = NativeMethods.RegisterHotKey(handle, StopHotKeyId, modifiers, VirtualKeyX);

        _hotkeysRegistered = captureRegistered || typeRegistered || stopRegistered;

        if (!captureRegistered || !typeRegistered || !stopRegistered)
        {
            SetStatus("One or more hotkeys are already in use by another app.");
        }
    }

    private void UnregisterHotKeys()
    {
        if (!_hotkeysRegistered || _source is null)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_source.Handle, CaptureHotKeyId);
        NativeMethods.UnregisterHotKey(_source.Handle, TypeHotKeyId);
        NativeMethods.UnregisterHotKey(_source.Handle, StopHotKeyId);
        _hotkeysRegistered = false;
    }

    private void CaptureTextSelection()
    {
        var result = SelectionReader.TryReadSelectedText(out var error);
        if (result is null)
        {
            SetStatus(error is null
                ? "No UI Automation selection found."
                : $"Selection read failed: {error}");
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
            await TextTyper.WaitForPasteHotKeyReleaseAsync(_typingCancellation.Token);

            var lastProgress = 0;
            var progress = new Progress<int>(count =>
            {
                if (count - lastProgress >= 25 || count == entry.Text.Length)
                {
                    lastProgress = count;
                    SetStatus($"Typing {count:N0}/{entry.Text.Length:N0} characters...");
                }
            });

            await TextTyper.TypeTextAsync(entry.Text, DefaultTypingDelayMs, _typingCancellation.Token, progress);
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
        PreviewText.ClearValue(FontFamilyProperty);
        PreviewText.ClearValue(FontSizeProperty);
        PreviewText.ClearValue(FontWeightProperty);
        PreviewText.ClearValue(FontStyleProperty);
        PreviewText.ClearValue(ForegroundProperty);
        PreviewText.ClearValue(BackgroundProperty);

        if (style is null)
        {
            PreviewText.FontFamily = new FontFamily("Consolas");
            PreviewText.FontSize = 13;
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
            PreviewText.FontWeight = style.FontWeight.Value >= 700 ? FontWeights.Bold : FontWeights.Normal;
        }

        if (style.IsItalic.HasValue)
        {
            PreviewText.FontStyle = style.IsItalic.Value ? FontStyles.Italic : FontStyles.Normal;
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
