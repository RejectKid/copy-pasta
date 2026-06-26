using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;

namespace CopyPasta;

internal sealed class LinuxGlobalHotkeyService : IGlobalHotkeyService
{
    private const int KeyPress = 2;
    private const int ControlMask = 1 << 2;
    private const int Mod1Mask = 1 << 3;

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
                LinuxNative.XUngrabKey(_display, (int)keycode, ControlMask | Mod1Mask, _rootWindow);
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
        LinuxNative.XGrabKey(_display, (int)keycode, ControlMask | Mod1Mask, _rootWindow, true, 1, 1);
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
    private const int ControlMask = 1 << 2;
    private const int Mod1Mask = 1 << 3;
    private const uint ShiftKeysym = 0xffe1;

    public Task WaitForTypeHotkeyReleaseAsync(CancellationToken cancellationToken)
    {
        return Task.Delay(120, cancellationToken);
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
}

internal sealed class LinuxContextMenuIntegrationService : IContextMenuIntegrationService
{
    private const string MenuName = "Add to Copy Pasta";
    private const string DesktopFileName = "copy-pasta-add-to-history.desktop";

    private static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
            ? home
            : Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

    private static string WrapperPath => Path.Combine(HomeDirectory, ".local", "share", "copy-pasta", "add-to-history");

    private static string[] IntegrationFiles =>
    [
        WrapperPath,
        Path.Combine(HomeDirectory, ".local", "share", "nautilus", "scripts", MenuName),
        Path.Combine(HomeDirectory, ".local", "share", "nemo", "scripts", MenuName),
        Path.Combine(HomeDirectory, ".local", "share", "nemo", "actions", "copy-pasta.nemo_action"),
        Path.Combine(HomeDirectory, ".config", "caja", "scripts", MenuName),
        Path.Combine(HomeDirectory, ".local", "share", "kio", "servicemenus", DesktopFileName),
        Path.Combine(HomeDirectory, ".local", "share", "kservices5", "ServiceMenus", DesktopFileName)
    ];

    public ContextMenuIntegrationStatus GetStatus()
    {
        var isInstalled = IntegrationFiles.All(File.Exists);

        return new ContextMenuIntegrationStatus(
            true,
            isInstalled,
            "Linux file managers",
            isInstalled
                ? "File manager context menu entries are installed for the current user."
                : "File manager context menu entries are not installed for the current user.");
    }

    public ContextMenuIntegrationResult Install()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(HomeDirectory))
            {
                return new ContextMenuIntegrationResult(false, "Could not resolve the current user's home directory.");
            }

            WriteExecutableFile(WrapperPath, CreateWrapperScript());
            WriteExecutableFile(Path.Combine(HomeDirectory, ".local", "share", "nautilus", "scripts", MenuName), CreateScriptMenuLauncher());
            WriteExecutableFile(Path.Combine(HomeDirectory, ".local", "share", "nemo", "scripts", MenuName), CreateScriptMenuLauncher());
            WriteTextFile(Path.Combine(HomeDirectory, ".local", "share", "nemo", "actions", "copy-pasta.nemo_action"), CreateNemoAction());
            WriteExecutableFile(Path.Combine(HomeDirectory, ".config", "caja", "scripts", MenuName), CreateScriptMenuLauncher());
            WriteExecutableFile(Path.Combine(HomeDirectory, ".local", "share", "kio", "servicemenus", DesktopFileName), CreateDolphinServiceMenu());
            WriteExecutableFile(Path.Combine(HomeDirectory, ".local", "share", "kservices5", "ServiceMenus", DesktopFileName), CreateDolphinServiceMenu());

            return new ContextMenuIntegrationResult(true, "Installed Linux file manager context menus for the current user.");
        }
        catch (Exception ex)
        {
            return new ContextMenuIntegrationResult(false, $"Could not install Linux context menus: {ex.Message}");
        }
    }

    public ContextMenuIntegrationResult Uninstall()
    {
        try
        {
            foreach (var file in IntegrationFiles)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }

            var wrapperDirectory = Path.GetDirectoryName(WrapperPath);
            if (!string.IsNullOrEmpty(wrapperDirectory) &&
                Directory.Exists(wrapperDirectory) &&
                !Directory.EnumerateFileSystemEntries(wrapperDirectory).Any())
            {
                Directory.Delete(wrapperDirectory);
            }

            return new ContextMenuIntegrationResult(true, "Removed Linux file manager context menus for the current user.");
        }
        catch (Exception ex)
        {
            return new ContextMenuIntegrationResult(false, $"Could not remove Linux context menus: {ex.Message}");
        }
    }

    private static string CreateWrapperScript()
    {
        var appPath = ShellQuote(AppCommand.ResolveExecutablePath());
        return $$"""
#!/bin/sh
APP={{appPath}}

run_env_paths() {
    if [ -n "$1" ]; then
        printf '%s\n' "$1" | while IFS= read -r path; do
            [ -n "$path" ] && "$APP" {{AppCommand.AddToHistoryOption}} "$path"
        done
        exit 0
    fi
}

if [ "$#" -gt 0 ]; then
    exec "$APP" {{AppCommand.AddToHistoryOption}} "$@"
fi

run_env_paths "$NAUTILUS_SCRIPT_SELECTED_FILE_PATHS"
run_env_paths "$NEMO_SCRIPT_SELECTED_FILE_PATHS"
run_env_paths "$CAJA_SCRIPT_SELECTED_FILE_PATHS"

if [ -n "$PWD" ]; then
    exec "$APP" {{AppCommand.AddToHistoryOption}} "$PWD"
fi
""";
    }

    private static string CreateScriptMenuLauncher()
    {
        return $$"""
#!/bin/sh
exec {{ShellQuote(WrapperPath)}} "$@"
""";
    }

    private static string CreateNemoAction()
    {
        return $$"""
[Nemo Action]
Name={{MenuName}}
Comment=Add selected path to Copy Pasta history
Exec={{DesktopQuote(WrapperPath)}} %F
Selection=any
Extensions=any;
Icon-Name=edit-paste
""";
    }

    private static string CreateDolphinServiceMenu()
    {
        return $$"""
[Desktop Entry]
Type=Service
MimeType=all/all;inode/directory;
Actions=addToCopyPasta;
ServiceTypes=KonqPopupMenu/Plugin
X-KDE-ServiceTypes=KonqPopupMenu/Plugin
X-KDE-Priority=TopLevel

[Desktop Action addToCopyPasta]
Name={{MenuName}}
Icon=edit-paste
Exec={{DesktopQuote(WrapperPath)}} %F
""";
    }

    private static void WriteTextFile(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void WriteExecutableFile(string path, string content)
    {
        WriteTextFile(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
        }
    }

    private static string ShellQuote(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private static string DesktopQuote(string value)
    {
        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
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
