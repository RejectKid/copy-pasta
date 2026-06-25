using Avalonia.Controls;

namespace CopyPasta;

internal static class PlatformServicesFactory
{
    public static IGlobalHotkeyService CreateHotkeyService() => new UnsupportedGlobalHotkeyService();

    public static ISelectionCaptureService CreateSelectionCaptureService() => new UnsupportedSelectionCaptureService();

    public static ITextOutputService CreateTextOutputService() => new UnsupportedTextOutputService();
}

internal sealed class UnsupportedGlobalHotkeyService : IGlobalHotkeyService
{
    public event EventHandler<CopyPastaHotkey>? HotkeyPressed
    {
        add { }
        remove { }
    }

    public bool Register(Window window, out string? error)
    {
        error = "Global hotkeys are not implemented for this platform yet.";
        return false;
    }

    public void Dispose()
    {
    }
}

internal sealed class UnsupportedSelectionCaptureService : ISelectionCaptureService
{
    public SelectionReadResult? TryCapture(out string? error)
    {
        error = "Selection capture is not implemented for this platform yet.";
        return null;
    }
}

internal sealed class UnsupportedTextOutputService : ITextOutputService
{
    public Task WaitForTypeHotkeyReleaseAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task TypeTextAsync(string text, int delayMs, CancellationToken cancellationToken, IProgress<int>? progress = null)
    {
        throw new PlatformNotSupportedException("Text output is not implemented for this platform yet.");
    }
}
