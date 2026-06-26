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

internal sealed record ContextMenuIntegrationStatus(
    bool IsSupported,
    bool IsInstalled,
    string Title,
    string Detail);

internal sealed record ContextMenuIntegrationResult(
    bool Succeeded,
    string Message);

internal interface IContextMenuIntegrationService
{
    ContextMenuIntegrationStatus GetStatus();

    ContextMenuIntegrationResult Install();

    ContextMenuIntegrationResult Uninstall();
}

internal static class PlatformServices
{
    public static IGlobalHotkeyService CreateHotkeyService() => PlatformServicesFactory.CreateHotkeyService();

    public static ISelectionCaptureService CreateSelectionCaptureService() => PlatformServicesFactory.CreateSelectionCaptureService();

    public static ITextOutputService CreateTextOutputService() => PlatformServicesFactory.CreateTextOutputService();

    public static IContextMenuIntegrationService CreateContextMenuIntegrationService() => PlatformServicesFactory.CreateContextMenuIntegrationService();
}
