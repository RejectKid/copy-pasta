using Avalonia.Controls;

namespace CopyPasta;

internal enum CopyPastaHotkey
{
    Capture,
    Type,
    Stop,
    Palette
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

internal interface ICursorPositionService
{
    bool TryGetCursorPosition(out int x, out int y);
}

internal static class PlatformServices
{
    public static IGlobalHotkeyService CreateHotkeyService() => PlatformServicesFactory.CreateHotkeyService();

    public static ISelectionCaptureService CreateSelectionCaptureService() => PlatformServicesFactory.CreateSelectionCaptureService();

    public static ITextOutputService CreateTextOutputService() => PlatformServicesFactory.CreateTextOutputService();

    public static ICursorPositionService CreateCursorPositionService() => PlatformServicesFactory.CreateCursorPositionService();
}
