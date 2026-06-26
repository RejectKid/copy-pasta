using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace CopyPasta;

internal sealed class QuickPaletteWindow : Window
{
    private readonly ListBox _historyList = new();

    public QuickPaletteWindow(IReadOnlyList<HistoryEntry> history)
    {
        Title = "Copy Pasta";
        Width = 360;
        Height = 420;
        MinWidth = 320;
        MinHeight = 300;
        Topmost = true;
        CanResize = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.BorderOnly;

        var captureButton = CreateActionButton("Capture selected text");
        var typeButton = CreateActionButton("Type selected history item");
        var stopButton = CreateActionButton("Stop typing");

        captureButton.Click += (_, _) => Complete(QuickPaletteAction.Capture);
        typeButton.Click += (_, _) => Complete(QuickPaletteAction.TypeSelected);
        stopButton.Click += (_, _) => Complete(QuickPaletteAction.Stop);

        _historyList.ItemsSource = history.Take(12).ToList();
        _historyList.MinHeight = 160;
        _historyList.DoubleTapped += (_, _) => TypeHighlightedEntry();
        _historyList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TypeHighlightedEntry();
                e.Handled = true;
            }
        };

        Content = new Border
        {
            Padding = new Thickness(12),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(210, 214, 220)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Copy Pasta",
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold
                    },
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            captureButton,
                            typeButton,
                            stopButton
                        }
                    },
                    new TextBlock
                    {
                        Text = "Recent",
                        FontWeight = FontWeight.SemiBold,
                        Margin = new Thickness(0, 6, 0, 0)
                    },
                    _historyList
                }
            }
        };
    }

    public event EventHandler<QuickPaletteSelection>? Completed;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.C:
                Complete(QuickPaletteAction.Capture);
                e.Handled = true;
                break;
            case Key.V:
                Complete(QuickPaletteAction.TypeSelected);
                e.Handled = true;
                break;
            case Key.X:
                Complete(QuickPaletteAction.Stop);
                e.Handled = true;
                break;
        }
    }

    private static Button CreateActionButton(string text)
    {
        return new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 7),
            Margin = new Thickness(0, 0, 0, 6)
        };
    }

    private void TypeHighlightedEntry()
    {
        if (_historyList.SelectedItem is HistoryEntry entry)
        {
            Complete(QuickPaletteAction.TypeEntry, entry);
        }
    }

    private void Complete(QuickPaletteAction action, HistoryEntry? entry = null)
    {
        Completed?.Invoke(this, new QuickPaletteSelection(action, entry));
        Close();
    }
}

internal enum QuickPaletteAction
{
    Capture,
    TypeSelected,
    TypeEntry,
    Stop
}

internal sealed record QuickPaletteSelection(QuickPaletteAction Action, HistoryEntry? Entry = null);
