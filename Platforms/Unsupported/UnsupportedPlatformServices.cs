using Avalonia.Controls;
using System.Runtime.InteropServices;

namespace CopyPasta;

internal static class PlatformServicesFactory
{
    public static IGlobalHotkeyService CreateHotkeyService()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOSGlobalHotkeyService();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxGlobalHotkeyService();
        }

        return new UnsupportedGlobalHotkeyService("Global hotkeys are not implemented for this platform.");
    }

    public static ISelectionCaptureService CreateSelectionCaptureService()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOSSelectionCaptureService();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxSelectionCaptureService();
        }

        return new UnsupportedSelectionCaptureService("Selection capture is not implemented for this platform.");
    }

    public static ITextOutputService CreateTextOutputService()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOSTextOutputService();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxTextOutputService();
        }

        return new UnsupportedTextOutputService("Text output is not implemented for this platform.");
    }
}

internal sealed class UnsupportedGlobalHotkeyService : IGlobalHotkeyService
{
    private readonly string _message;

    public UnsupportedGlobalHotkeyService(string message)
    {
        _message = message;
    }

    public event EventHandler<CopyPastaHotkey>? HotkeyPressed
    {
        add { }
        remove { }
    }

    public bool Register(Window window, out string? error)
    {
        error = _message;
        return false;
    }

    public void Dispose()
    {
    }
}

internal sealed class UnsupportedSelectionCaptureService : ISelectionCaptureService
{
    private readonly string _message;

    public UnsupportedSelectionCaptureService(string message)
    {
        _message = message;
    }

    public SelectionReadResult? TryCapture(out string? error)
    {
        error = _message;
        return null;
    }
}

internal sealed class UnsupportedTextOutputService : ITextOutputService
{
    private readonly string _message;

    public UnsupportedTextOutputService(string message)
    {
        _message = message;
    }

    public Task WaitForTypeHotkeyReleaseAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task TypeTextAsync(string text, int delayMs, CancellationToken cancellationToken, IProgress<int>? progress = null)
    {
        throw new PlatformNotSupportedException(_message);
    }
}
