using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace CopyPasta;

internal sealed class MacOSGlobalHotkeyService : IGlobalHotkeyService
{
    private const int KeyCodeC = 8;
    private const int KeyCodeV = 9;
    private const int KeyCodeX = 7;
    private const int KeyCodeSpace = 49;
    private const ulong EventMaskKeyDown = 1UL << 10;
    private const ulong EventFlagMaskControl = 0x0004_0000;
    private const ulong EventFlagMaskAlternate = 0x0008_0000;
    private const ulong EventFlagMaskCommand = 0x0010_0000;

    private IntPtr _eventTap;
    private IntPtr _runLoopSource;
    private MacNative.CGEventTapCallBack? _callback;

    public event EventHandler<CopyPastaHotkey>? HotkeyPressed;

    public bool Register(Window window, out string? error)
    {
        error = null;

        if (!MacNative.AXIsProcessTrusted())
        {
            error = "macOS Accessibility permission is required for global hotkeys, selection capture, and text output.";
            return false;
        }

        _callback = EventTapCallback;
        _eventTap = MacNative.CGEventTapCreate(
            0,
            0,
            0,
            EventMaskKeyDown,
            _callback,
            IntPtr.Zero);

        if (_eventTap == IntPtr.Zero)
        {
            error = "Could not create a macOS keyboard event tap. Check Accessibility/Input Monitoring permissions.";
            return false;
        }

        _runLoopSource = MacNative.CFMachPortCreateRunLoopSource(IntPtr.Zero, _eventTap, 0);
        if (_runLoopSource == IntPtr.Zero)
        {
            error = "Could not attach macOS keyboard event tap to the run loop.";
            return false;
        }

        var runLoop = MacNative.CFRunLoopGetCurrent();
        MacNative.CFRunLoopAddSource(runLoop, _runLoopSource, MacNative.kCFRunLoopCommonModes);
        MacNative.CGEventTapEnable(_eventTap, true);
        return true;
    }

    public void Dispose()
    {
        if (_eventTap != IntPtr.Zero)
        {
            MacNative.CGEventTapEnable(_eventTap, false);
            MacNative.CFRelease(_eventTap);
        }

        if (_runLoopSource != IntPtr.Zero)
        {
            MacNative.CFRelease(_runLoopSource);
        }

        _eventTap = IntPtr.Zero;
        _runLoopSource = IntPtr.Zero;
        _callback = null;
        HotkeyPressed = null;
    }

    private IntPtr EventTapCallback(IntPtr proxy, int type, IntPtr eventRef, IntPtr userInfo)
    {
        if (type != 10)
        {
            return eventRef;
        }

        var flags = (ulong)MacNative.CGEventGetFlags(eventRef);
        var keyCode = (int)MacNative.CGEventGetIntegerValueField(eventRef, 9);
        var hasModifiers = (flags & EventFlagMaskAlternate) != 0 &&
            (flags & (EventFlagMaskControl | EventFlagMaskCommand)) != 0;

        if (!hasModifiers)
        {
            return eventRef;
        }

        switch (keyCode)
        {
            case KeyCodeC:
                HotkeyPressed?.Invoke(this, CopyPastaHotkey.Capture);
                return IntPtr.Zero;
            case KeyCodeV:
                HotkeyPressed?.Invoke(this, CopyPastaHotkey.Type);
                return IntPtr.Zero;
            case KeyCodeX:
                HotkeyPressed?.Invoke(this, CopyPastaHotkey.Stop);
                return IntPtr.Zero;
            case KeyCodeSpace:
                HotkeyPressed?.Invoke(this, CopyPastaHotkey.Palette);
                return IntPtr.Zero;
            default:
                return eventRef;
        }
    }
}

internal sealed class MacOSSelectionCaptureService : ISelectionCaptureService
{
    public SelectionReadResult? TryCapture(out string? error)
    {
        error = null;

        if (!MacNative.AXIsProcessTrusted())
        {
            error = "macOS Accessibility permission is required for selection capture.";
            return null;
        }

        var systemWide = MacNative.AXUIElementCreateSystemWide();
        try
        {
            var result = MacNative.AXUIElementCopyAttributeValue(systemWide, "AXFocusedUIElement", out var focusedElement);
            if (result != 0 || focusedElement == IntPtr.Zero)
            {
                error = "No focused accessibility element was available.";
                return null;
            }

            try
            {
                result = MacNative.AXUIElementCopyAttributeValue(focusedElement, "AXSelectedText", out var selectedText);
                if (result != 0 || selectedText == IntPtr.Zero)
                {
                    error = "The focused element did not expose selected text through Accessibility.";
                    return null;
                }

                try
                {
                    var text = MacNative.StringFromCFString(selectedText);
                    return string.IsNullOrWhiteSpace(text)
                        ? null
                        : new SelectionReadResult(text, "macOS Accessibility");
                }
                finally
                {
                    MacNative.CFRelease(selectedText);
                }
            }
            finally
            {
                MacNative.CFRelease(focusedElement);
            }
        }
        finally
        {
            MacNative.CFRelease(systemWide);
        }
    }
}

internal sealed class MacOSTextOutputService : ITextOutputService
{
    public Task WaitForTypeHotkeyReleaseAsync(CancellationToken cancellationToken)
    {
        return Task.Delay(120, cancellationToken);
    }

    public async Task TypeTextAsync(string text, int delayMs, CancellationToken cancellationToken, IProgress<int>? progress = null)
    {
        for (var i = 0; i < text.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MacNative.PostUnicodeCharacter(text[i]);
            progress?.Report(i + 1);

            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }
        }
    }
}

internal sealed class MacOSCursorPositionService : ICursorPositionService
{
    public bool TryGetCursorPosition(out int x, out int y)
    {
        var eventRef = MacNative.CGEventCreate(IntPtr.Zero);
        if (eventRef == IntPtr.Zero)
        {
            x = 0;
            y = 0;
            return false;
        }

        try
        {
            var point = MacNative.CGEventGetLocation(eventRef);
            x = (int)Math.Round(point.X);
            y = (int)Math.Round(point.Y);
            return true;
        }
        finally
        {
            MacNative.CFRelease(eventRef);
        }
    }
}
internal static class MacNative
{
    private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public static readonly IntPtr kCFRunLoopCommonModes = GetCoreFoundationStringConstant("kCFRunLoopCommonModes");

    public delegate IntPtr CGEventTapCallBack(IntPtr proxy, int type, IntPtr eventRef, IntPtr userInfo);

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint
    {
        public double X;
        public double Y;
    }

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern bool AXIsProcessTrusted();

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern IntPtr AXUIElementCreateSystemWide();

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern int AXUIElementCopyAttributeValue(IntPtr element, string attribute, out IntPtr value);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern IntPtr CGEventTapCreate(int tap, int place, int options, ulong eventsOfInterest, CGEventTapCallBack callback, IntPtr userInfo);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern void CGEventTapEnable(IntPtr tap, bool enable);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern ulong CGEventGetFlags(IntPtr eventRef);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern long CGEventGetIntegerValueField(IntPtr eventRef, int field);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, bool keyDown);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern void CGEventKeyboardSetUnicodeString(IntPtr eventRef, nint stringLength, char[] unicodeString);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern void CGEventPost(int tap, IntPtr eventRef);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    public static extern CGPoint CGEventGetLocation(IntPtr eventRef);

    [DllImport(CoreFoundationPath)]
    public static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, nint order);

    [DllImport(CoreFoundationPath)]
    public static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(CoreFoundationPath)]
    public static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

    [DllImport(CoreFoundationPath)]
    public static extern nint CFStringGetLength(IntPtr theString);

    [DllImport(CoreFoundationPath)]
    public static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundationPath)]
    public static extern void CFRelease(IntPtr cf);

    private static IntPtr GetCoreFoundationStringConstant(string name)
    {
        var library = NativeLibrary.Load(CoreFoundationPath);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    }

    public static string StringFromCFString(IntPtr cfString)
    {
        var length = CFStringGetLength(cfString);
        var buffer = new byte[(length * 4) + 1];
        return CFStringGetCString(cfString, buffer, buffer.Length, 0x08000100)
            ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0')
            : string.Empty;
    }

    public static void PostUnicodeCharacter(char character)
    {
        var chars = new[] { character };
        var keyDown = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, true);
        var keyUp = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, false);

        try
        {
            CGEventKeyboardSetUnicodeString(keyDown, chars.Length, chars);
            CGEventKeyboardSetUnicodeString(keyUp, chars.Length, chars);
            CGEventPost(0, keyDown);
            CGEventPost(0, keyUp);
        }
        finally
        {
            if (keyDown != IntPtr.Zero)
            {
                CFRelease(keyDown);
            }

            if (keyUp != IntPtr.Zero)
            {
                CFRelease(keyUp);
            }
        }
    }
}
