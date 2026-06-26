using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;

namespace CopyPasta;

internal sealed class LinuxGlobalHotkeyService : IGlobalHotkeyService
{
    private const int KeyPress = 2;
    private const int ControlMask = 1 << 2;
    private const int Mod1Mask = 1 << 3;
    private const int LockMask = 1 << 1;
    private const int Mod2Mask = 1 << 4;

    private static readonly int[] IgnoredModifierMasks =
    [
        0,
        LockMask,
        Mod2Mask,
        LockMask | Mod2Mask
    ];

    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<uint, CopyPastaHotkey> _keycodes = [];
    private IntPtr _display;
    private IntPtr _rootWindow;
    private Task? _messagePump;

    public event EventHandler<CopyPastaHotkey>? HotkeyPressed;

    public bool Register(Window window, out string? error)
    {
        error = null;
        _display = LinuxNative.XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
        {
            error = "Linux global hotkeys require an X11 session. Wayland is not supported yet.";
            return false;
        }

        _rootWindow = LinuxNative.XDefaultRootWindow(_display);
        RegisterHotkey("c", CopyPastaHotkey.Capture);
        RegisterHotkey("v", CopyPastaHotkey.Type);
        RegisterHotkey("x", CopyPastaHotkey.Stop);
        LinuxNative.XSync(_display, false);

        _messagePump = Task.Run(EventLoop);
        return true;
    }

    public void Dispose()
    {
        _cancellation.Cancel();

        if (_display != IntPtr.Zero)
        {
            foreach (var keycode in _keycodes.Keys)
            {
                foreach (var ignoredModifiers in IgnoredModifierMasks)
                {
                    LinuxNative.XUngrabKey(_display, (int)keycode, ControlMask | Mod1Mask | ignoredModifiers, _rootWindow);
                }
            }

            LinuxNative.XCloseDisplay(_display);
        }

        _display = IntPtr.Zero;
        HotkeyPressed = null;
        _cancellation.Dispose();
    }

    private void RegisterHotkey(string keysymName, CopyPastaHotkey hotkey)
    {
        var keysym = LinuxNative.XStringToKeysym(keysymName);
        var keycode = LinuxNative.XKeysymToKeycode(_display, keysym);
        if (keycode == 0)
        {
            return;
        }

        _keycodes[keycode] = hotkey;
        foreach (var ignoredModifiers in IgnoredModifierMasks)
        {
            LinuxNative.XGrabKey(_display, (int)keycode, ControlMask | Mod1Mask | ignoredModifiers, _rootWindow, true, 1, 1);
        }
    }

    private void EventLoop()
    {
        while (!_cancellation.IsCancellationRequested && _display != IntPtr.Zero)
        {
            LinuxNative.XNextEvent(_display, out var xEvent);
            if (xEvent.type == KeyPress &&
                _keycodes.TryGetValue(xEvent.xkey.keycode, out var hotkey) &&
                (xEvent.xkey.state & (ControlMask | Mod1Mask)) == (ControlMask | Mod1Mask))
            {
                HotkeyPressed?.Invoke(this, hotkey);
            }
        }
    }
}

internal sealed class LinuxSelectionCaptureService : ISelectionCaptureService
{
    private const int SelectionNotify = 31;

    public SelectionReadResult? TryCapture(out string? error)
    {
        error = null;
        var display = LinuxNative.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            error = "Selection capture requires an X11 session. Wayland is not supported yet.";
            return null;
        }

        var window = IntPtr.Zero;
        try
        {
            var root = LinuxNative.XDefaultRootWindow(display);
            window = LinuxNative.XCreateSimpleWindow(display, root, 0, 0, 1, 1, 0, 0, 0);
            var primary = LinuxNative.XInternAtom(display, "PRIMARY", false);
            var utf8 = LinuxNative.XInternAtom(display, "UTF8_STRING", false);
            var property = LinuxNative.XInternAtom(display, "COPY_PASTA_SELECTION", false);

            LinuxNative.XConvertSelection(display, primary, utf8, property, window, 0);
            LinuxNative.XFlush(display);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                LinuxNative.XNextEvent(display, out var xEvent);
                if (xEvent.type != SelectionNotify)
                {
                    continue;
                }

                if (xEvent.xselection.property == IntPtr.Zero)
                {
                    error = "The active X11 selection owner did not provide text.";
                    return null;
                }

                var text = ReadWindowProperty(display, window, property);
                return string.IsNullOrWhiteSpace(text)
                    ? null
                    : new SelectionReadResult(text, "X11 PRIMARY selection");
            }

            error = "Timed out waiting for X11 selected text.";
            return null;
        }
        finally
        {
            if (window != IntPtr.Zero)
            {
                LinuxNative.XDestroyWindow(display, window);
            }

            LinuxNative.XCloseDisplay(display);
        }
    }

    private static string ReadWindowProperty(IntPtr display, IntPtr window, IntPtr property)
    {
        var status = LinuxNative.XGetWindowProperty(
            display,
            window,
            property,
            IntPtr.Zero,
            new IntPtr(1024 * 1024),
            false,
            IntPtr.Zero,
            out _,
            out _,
            out var itemCount,
            out _,
            out var data);

        if (status != 0 || data == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var bytes = new byte[itemCount.ToInt64()];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            LinuxNative.XFree(data);
        }
    }
}

internal sealed class LinuxTextOutputService : ITextOutputService
{
    private const uint ShiftKeysym = 0xffe1;
    private const uint ControlLeftKeysym = 0xffe3;
    private const uint ControlRightKeysym = 0xffe4;
    private const uint AltLeftKeysym = 0xffe9;
    private const uint AltRightKeysym = 0xffea;

    public async Task WaitForTypeHotkeyReleaseAsync(CancellationToken cancellationToken)
    {
        var display = LinuxNative.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            await Task.Delay(120, cancellationToken);
            return;
        }

        try
        {
            var watchedKeycodes = new[]
            {
                LinuxNative.XKeysymToKeycode(display, LinuxNative.XStringToKeysym("v")),
                LinuxNative.XKeysymToKeycode(display, ControlLeftKeysym),
                LinuxNative.XKeysymToKeycode(display, ControlRightKeysym),
                LinuxNative.XKeysymToKeycode(display, AltLeftKeysym),
                LinuxNative.XKeysymToKeycode(display, AltRightKeysym)
            }.Where(keycode => keycode != 0).ToArray();

            var keymap = new byte[32];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LinuxNative.XQueryKeymap(display, keymap);

                if (!watchedKeycodes.Any(keycode => IsKeyDown(keymap, keycode)))
                {
                    return;
                }

                await Task.Delay(20, cancellationToken);
            }
        }
        finally
        {
            LinuxNative.XCloseDisplay(display);
        }
    }

    public async Task TypeTextAsync(string text, int delayMs, CancellationToken cancellationToken, IProgress<int>? progress = null)
    {
        var display = LinuxNative.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException("Text output requires an X11 session. Wayland is not supported yet.");
        }

        try
        {
            for (var i = 0; i < text.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TypeCharacter(display, text[i]);
                progress?.Report(i + 1);

                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken);
                }
            }
        }
        finally
        {
            LinuxNative.XCloseDisplay(display);
        }
    }

    private static void TypeCharacter(IntPtr display, char character)
    {
        if (!TryGetKeysym(character, out var keysym, out var needsShift))
        {
            throw new NotSupportedException($"Linux X11 text output does not support character U+{(int)character:X4} yet.");
        }

        var keycode = LinuxNative.XKeysymToKeycode(display, keysym);
        if (keycode == 0)
        {
            throw new NotSupportedException($"No X11 keycode exists for character '{character}'.");
        }

        var shiftKeycode = LinuxNative.XKeysymToKeycode(display, ShiftKeysym);
        if (needsShift)
        {
            LinuxNative.XTestFakeKeyEvent(display, shiftKeycode, true, 0);
        }

        LinuxNative.XTestFakeKeyEvent(display, keycode, true, 0);
        LinuxNative.XTestFakeKeyEvent(display, keycode, false, 0);

        if (needsShift)
        {
            LinuxNative.XTestFakeKeyEvent(display, shiftKeycode, false, 0);
        }

        LinuxNative.XFlush(display);
    }

    private static bool TryGetKeysym(char character, out uint keysym, out bool needsShift)
    {
        needsShift = false;
        keysym = character switch
        {
            '\r' or '\n' => 0xff0d,
            '\t' => 0xff09,
            ' ' => 0x020,
            >= 'a' and <= 'z' => character,
            >= '0' and <= '9' => character,
            >= 'A' and <= 'Z' => char.ToLowerInvariant(character),
            '!' => '1',
            '@' => '2',
            '#' => '3',
            '$' => '4',
            '%' => '5',
            '^' => '6',
            '&' => '7',
            '*' => '8',
            '(' => '9',
            ')' => '0',
            '-' or '_' => '-',
            '=' or '+' => '=',
            '[' or '{' => '[',
            ']' or '}' => ']',
            ';' or ':' => ';',
            '\'' or '"' => '\'',
            ',' or '<' => ',',
            '.' or '>' => '.',
            '/' or '?' => '/',
            '\\' or '|' => '\\',
            '`' or '~' => '`',
            _ => 0
        };

        needsShift = char.IsUpper(character) || "!@#$%^&*()_+{}:\"<>?|~".Contains(character);
        return keysym != 0;
    }

    private static bool IsKeyDown(byte[] keymap, uint keycode)
    {
        var index = keycode / 8;
        var mask = 1 << (int)(keycode % 8);
        return index < keymap.Length && (keymap[index] & mask) != 0;
    }
}

internal static class LinuxNative
{
    [DllImport("libX11")]
    public static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11")]
    public static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11")]
    public static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11")]
    public static extern uint XStringToKeysym(string str);

    [DllImport("libX11")]
    public static extern uint XKeysymToKeycode(IntPtr display, uint keysym);

    [DllImport("libX11")]
    public static extern int XGrabKey(IntPtr display, int keycode, int modifiers, IntPtr grabWindow, bool ownerEvents, int pointerMode, int keyboardMode);

    [DllImport("libX11")]
    public static extern int XUngrabKey(IntPtr display, int keycode, int modifiers, IntPtr grabWindow);

    [DllImport("libX11")]
    public static extern int XNextEvent(IntPtr display, out XEvent xEvent);

    [DllImport("libX11")]
    public static extern int XSync(IntPtr display, bool discard);

    [DllImport("libX11")]
    public static extern int XFlush(IntPtr display);

    [DllImport("libX11")]
    public static extern int XQueryKeymap(IntPtr display, byte[] keysReturn);

    [DllImport("libX11")]
    public static extern IntPtr XInternAtom(IntPtr display, string atomName, bool onlyIfExists);

    [DllImport("libX11")]
    public static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y, uint width, uint height, uint borderWidth, ulong border, ulong background);

    [DllImport("libX11")]
    public static extern int XDestroyWindow(IntPtr display, IntPtr window);

    [DllImport("libX11")]
    public static extern int XConvertSelection(IntPtr display, IntPtr selection, IntPtr target, IntPtr property, IntPtr requestor, long time);

    [DllImport("libX11")]
    public static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr longOffset, IntPtr longLength, bool delete, IntPtr reqType, out IntPtr actualType, out int actualFormat, out IntPtr itemCount, out IntPtr bytesAfter, out IntPtr prop);

    [DllImport("libX11")]
    public static extern int XFree(IntPtr data);

    [DllImport("libXtst")]
    public static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, bool isPress, ulong delay);

    [StructLayout(LayoutKind.Explicit)]
    public struct XEvent
    {
        [FieldOffset(0)]
        public int type;

        [FieldOffset(0)]
        public XKeyEvent xkey;

        [FieldOffset(0)]
        public XSelectionEvent xselection;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XKeyEvent
    {
        public int type;
        public ulong serial;
        public int sendEvent;
        public IntPtr display;
        public IntPtr window;
        public IntPtr root;
        public IntPtr subwindow;
        public long time;
        public int x;
        public int y;
        public int xRoot;
        public int yRoot;
        public int state;
        public uint keycode;
        public int sameScreen;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XSelectionEvent
    {
        public int type;
        public ulong serial;
        public int sendEvent;
        public IntPtr display;
        public IntPtr requestor;
        public IntPtr selection;
        public IntPtr target;
        public IntPtr property;
        public long time;
    }
}
