using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace CopyPasta;

internal sealed class MacOSGlobalHotkeyService : IGlobalHotkeyService
{
    private const int KeyCodeC = 8;
    private const int KeyCodeV = 9;
    private const int KeyCodeX = 7;
    private const ulong EventMaskKeyDown = 1UL << 10;
    private const ulong EventFlagMaskControl = 0x0004_0000;
    private const ulong EventFlagMaskAlternate = 0x0008_0000;

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
        var hasModifiers = (flags & EventFlagMaskControl) != 0 && (flags & EventFlagMaskAlternate) != 0;

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

internal sealed class MacOSContextMenuIntegrationService : IContextMenuIntegrationService
{
    private const string ServiceName = "Add to Copy Pasta";

    private static string WorkflowDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "Services",
        $"{ServiceName}.workflow");

    private static string ContentsDirectory => Path.Combine(WorkflowDirectory, "Contents");

    public ContextMenuIntegrationStatus GetStatus()
    {
        var isInstalled = File.Exists(Path.Combine(ContentsDirectory, "Info.plist")) &&
            File.Exists(Path.Combine(ContentsDirectory, "document.wflow"));

        return new ContextMenuIntegrationStatus(
            true,
            isInstalled,
            "macOS Finder",
            isInstalled
                ? "Finder Quick Action is installed for the current user."
                : "Finder Quick Action is not installed for the current user.");
    }

    public ContextMenuIntegrationResult Install()
    {
        try
        {
            Directory.CreateDirectory(ContentsDirectory);
            File.WriteAllText(Path.Combine(ContentsDirectory, "Info.plist"), CreateInfoPlist());
            File.WriteAllText(Path.Combine(ContentsDirectory, "document.wflow"), CreateWorkflowDocument());
            RefreshServicesDatabase();

            return new ContextMenuIntegrationResult(true, "Installed Finder Quick Action for the current user.");
        }
        catch (Exception ex)
        {
            return new ContextMenuIntegrationResult(false, $"Could not install Finder Quick Action: {ex.Message}");
        }
    }

    public ContextMenuIntegrationResult Uninstall()
    {
        try
        {
            if (Directory.Exists(WorkflowDirectory))
            {
                Directory.Delete(WorkflowDirectory, true);
            }

            RefreshServicesDatabase();
            return new ContextMenuIntegrationResult(true, "Removed Finder Quick Action for the current user.");
        }
        catch (Exception ex)
        {
            return new ContextMenuIntegrationResult(false, $"Could not remove Finder Quick Action: {ex.Message}");
        }
    }

    private static string CreateInfoPlist()
    {
        return $$"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>{{XmlEscape(ServiceName)}}</string>
    <key>NSServices</key>
    <array>
        <dict>
            <key>NSMenuItem</key>
            <dict>
                <key>default</key>
                <string>{{XmlEscape(ServiceName)}}</string>
            </dict>
            <key>NSMessage</key>
            <string>runWorkflowAsService</string>
            <key>NSSendFileTypes</key>
            <array>
                <string>public.item</string>
                <string>public.folder</string>
            </array>
        </dict>
    </array>
</dict>
</plist>
""";
    }

    private static string CreateWorkflowDocument()
    {
        var command = $"for item in \"$@\"; do\n  {ShellQuote(AppCommand.ResolveExecutablePath())} {AppCommand.AddToHistoryOption} \"$item\"\ndone";
        var inputUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var outputUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var actionUuid = Guid.NewGuid().ToString().ToUpperInvariant();

        return $$"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>AMApplicationBuild</key>
    <string>523</string>
    <key>AMApplicationVersion</key>
    <string>2.10</string>
    <key>AMDocumentVersion</key>
    <string>2</string>
    <key>actions</key>
    <array>
        <dict>
            <key>action</key>
            <dict>
                <key>AMAccepts</key>
                <dict>
                    <key>Container</key>
                    <string>List</string>
                    <key>Optional</key>
                    <true/>
                    <key>Types</key>
                    <array>
                        <string>com.apple.cocoa.path</string>
                    </array>
                </dict>
                <key>AMActionVersion</key>
                <string>2.0.3</string>
                <key>AMApplication</key>
                <array>
                    <string>Automator</string>
                </array>
                <key>AMProvides</key>
                <dict>
                    <key>Container</key>
                    <string>List</string>
                    <key>Types</key>
                    <array>
                        <string>com.apple.cocoa.path</string>
                    </array>
                </dict>
                <key>ActionBundlePath</key>
                <string>/System/Library/Automator/Run Shell Script.action</string>
                <key>ActionName</key>
                <string>Run Shell Script</string>
                <key>ActionParameters</key>
                <dict>
                    <key>COMMAND_STRING</key>
                    <string>{{XmlEscape(command)}}</string>
                    <key>CheckedForUserDefaultShell</key>
                    <true/>
                    <key>inputMethod</key>
                    <integer>1</integer>
                    <key>shell</key>
                    <string>/bin/bash</string>
                    <key>source</key>
                    <string></string>
                </dict>
                <key>BundleIdentifier</key>
                <string>com.apple.RunShellScript</string>
                <key>CFBundleVersion</key>
                <string>2.0.3</string>
                <key>CanShowSelectedItemsWhenRun</key>
                <false/>
                <key>CanShowWhenRun</key>
                <true/>
                <key>Category</key>
                <string>AMCategoryUtilities</string>
                <key>Class Name</key>
                <string>RunShellScriptAction</string>
                <key>InputUUID</key>
                <string>{{inputUuid}}</string>
                <key>Keywords</key>
                <array>
                    <string>Shell</string>
                    <string>Script</string>
                </array>
                <key>OutputUUID</key>
                <string>{{outputUuid}}</string>
                <key>UUID</key>
                <string>{{actionUuid}}</string>
                <key>UnlocalizedApplications</key>
                <array>
                    <string>Automator</string>
                </array>
            </dict>
            <key>isViewVisible</key>
            <false/>
        </dict>
    </array>
    <key>connectors</key>
    <dict/>
    <key>workflowMetaData</key>
    <dict>
        <key>serviceApplicationBundleID</key>
        <string>com.apple.finder</string>
        <key>serviceInputTypeIdentifier</key>
        <string>com.apple.Automator.fileSystemObject</string>
        <key>workflowTypeIdentifier</key>
        <string>com.apple.Automator.servicesMenu</string>
    </dict>
</dict>
</plist>
""";
    }

    private static string ShellQuote(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private static string XmlEscape(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static void RefreshServicesDatabase()
    {
        const string pbsPath = "/System/Library/CoreServices/pbs";
        if (!File.Exists(pbsPath))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = pbsPath,
                ArgumentList = { "-update" },
                CreateNoWindow = true
            });
            process?.WaitForExit(1500);
        }
        catch
        {
        }
    }
}

internal static class MacNative
{
    private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public static readonly IntPtr kCFRunLoopCommonModes = GetCoreFoundationStringConstant("kCFRunLoopCommonModes");

    public delegate IntPtr CGEventTapCallBack(IntPtr proxy, int type, IntPtr eventRef, IntPtr userInfo);

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
