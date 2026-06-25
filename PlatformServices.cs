using Avalonia.Controls;

namespace CopyPasta;

internal enum CopyPastaHotkey
{
    Capture,
    Type,
    Stop
}

internal interface IGlobalHotkeyService : IDisposable
{
    event EventHandler<CopyPastaHotkey>? HotkeyPressed;

    bool Register(Window window, out string? error);
}

internal interface ISelectionCaptureService
{
    SelectionReadResult? TryCapture(out string? error);
}

internal interface ITextOutputService
{
    Task WaitForTypeHotkeyReleaseAsync(CancellationToken cancellationToken);

    Task TypeTextAsync(string text, int delayMs, CancellationToken cancellationToken, IProgress<int>? progress = null);
}

internal static class PlatformServices
{
    public static IGlobalHotkeyService CreateHotkeyService() => PlatformServicesFactory.CreateHotkeyService();

    public static ISelectionCaptureService CreateSelectionCaptureService() => PlatformServicesFactory.CreateSelectionCaptureService();

    public static ITextOutputService CreateTextOutputService() => PlatformServicesFactory.CreateTextOutputService();
}
