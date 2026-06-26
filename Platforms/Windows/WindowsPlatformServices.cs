using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Win32;

namespace CopyPasta;

internal static class PlatformServicesFactory
{
    public static IGlobalHotkeyService CreateHotkeyService() => new WindowsGlobalHotkeyService();

    public static ISelectionCaptureService CreateSelectionCaptureService() => new WindowsSelectionCaptureService();

    public static ITextOutputService CreateTextOutputService() => new WindowsTextOutputService();

    public static IContextMenuIntegrationService CreateContextMenuIntegrationService() => new WindowsContextMenuIntegrationService();
}

internal sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int CaptureHotkeyId = 100;
    private const int TypeHotkeyId = 101;
    private const int StopHotkeyId = 102;

    private const uint VirtualKeyC = 0x43;
    private const uint VirtualKeyV = 0x56;
    private const uint VirtualKeyX = 0x58;

    private IntPtr _handle;
    private IntPtr _previousWndProc;
    private bool _registered;
    private readonly NativeMethods.WndProcDelegate _wndProc;

    public WindowsGlobalHotkeyService()
    {
        _wndProc = WndProc;
    }

    public event EventHandler<CopyPastaHotkey>? HotkeyPressed;

    public bool Register(Window window, out string? error)
    {
        error = null;

        if (_registered)
        {
            return true;
        }

        if (window.TryGetPlatformHandle() is not { } platformHandle ||
            platformHandle.Handle == IntPtr.Zero ||
            platformHandle.HandleDescriptor != "HWND")
        {
            error = "Could not resolve the native Windows window handle.";
            return false;
        }

        _handle = platformHandle.Handle;
        _previousWndProc = NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlpWndProc, _wndProc);
        if (_previousWndProc == IntPtr.Zero)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        var modifiers = NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat;
        var captureRegistered = NativeMethods.RegisterHotKey(_handle, CaptureHotkeyId, modifiers, VirtualKeyC);
        var typeRegistered = NativeMethods.RegisterHotKey(_handle, TypeHotkeyId, modifiers, VirtualKeyV);
        var stopRegistered = NativeMethods.RegisterHotKey(_handle, StopHotkeyId, modifiers, VirtualKeyX);

        _registered = captureRegistered || typeRegistered || stopRegistered;

        if (!captureRegistered || !typeRegistered || !stopRegistered)
        {
            error = "One or more global hotkeys are already in use by another app.";
        }

        return _registered;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_handle, CaptureHotkeyId);
            NativeMethods.UnregisterHotKey(_handle, TypeHotkeyId);
            NativeMethods.UnregisterHotKey(_handle, StopHotkeyId);

            if (_previousWndProc != IntPtr.Zero)
            {
                NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlpWndProc, _previousWndProc);
            }
        }

        _registered = false;
        _handle = IntPtr.Zero;
        _previousWndProc = IntPtr.Zero;
        HotkeyPressed = null;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WmHotKey)
        {
            switch (wParam.ToInt32())
            {
                case CaptureHotkeyId:
                    HotkeyPressed?.Invoke(this, CopyPastaHotkey.Capture);
                    return IntPtr.Zero;
                case TypeHotkeyId:
                    HotkeyPressed?.Invoke(this, CopyPastaHotkey.Type);
                    return IntPtr.Zero;
                case StopHotkeyId:
                    HotkeyPressed?.Invoke(this, CopyPastaHotkey.Stop);
                    return IntPtr.Zero;
            }
        }

        return NativeMethods.CallWindowProc(_previousWndProc, hWnd, msg, wParam, lParam);
    }
}

internal sealed class WindowsSelectionCaptureService : ISelectionCaptureService
{
    private const uint EmGetSel = 0x00B0;
    private const uint EmGetPasswordChar = 0x00D2;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;

    private const int MaxSelectionChars = 200_000;
    private static readonly int CurrentProcessId = Process.GetCurrentProcess().Id;

    public SelectionReadResult? TryCapture(out string? error)
    {
        error = null;

        try
        {
            foreach (var element in EnumerateStartingElements())
            {
                var result = TryReadFromElementAndParents(element);
                if (result is not null)
                {
                    return result;
                }
            }

            return TryReadFocusedNativeSelection();
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (COMException ex)
        {
            error = ex.Message;
            return null;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static IEnumerable<AutomationElement> EnumerateStartingElements()
    {
        var focusedElement = AutomationElement.FocusedElement;
        if (focusedElement is not null)
        {
            yield return focusedElement;
        }

        if (!NativeMethods.GetCursorPos(out var cursorPosition))
        {
            yield break;
        }

        AutomationElement? elementFromPoint = null;
        try
        {
            elementFromPoint = AutomationElement.FromPoint(new System.Windows.Point(cursorPosition.X, cursorPosition.Y));
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (COMException)
        {
        }

        if (elementFromPoint is not null)
        {
            yield return elementFromPoint;
        }
    }

    private static SelectionReadResult? TryReadFromElementAndParents(AutomationElement element)
    {
        foreach (var candidate in EnumerateElementAndParents(element))
        {
            if (IsCurrentProcessElement(candidate))
            {
                continue;
            }

            var text = TryReadTextPatternSelection(candidate);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var style = TryReadTextStyle(candidate);
                return new SelectionReadResult(text, Describe(candidate), style);
            }
        }

        return null;
    }

    private static IEnumerable<AutomationElement> EnumerateElementAndParents(AutomationElement element)
    {
        var current = element;
        for (var i = 0; i < 8 && current is not null; i++)
        {
            yield return current;

            try
            {
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
            catch (ElementNotAvailableException)
            {
                yield break;
            }
        }
    }

    private static string? TryReadTextPatternSelection(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
            patternObject is not TextPattern textPattern)
        {
            return null;
        }

        var ranges = textPattern.GetSelection();
        if (ranges.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var range in ranges)
        {
            var remaining = MaxSelectionChars - builder.Length;
            if (remaining <= 0)
            {
                break;
            }

            var text = range.GetText(remaining);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(text);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static TextStyleSnapshot? TryReadTextStyle(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
            patternObject is not TextPattern textPattern)
        {
            return null;
        }

        var ranges = textPattern.GetSelection();
        if (ranges.Length == 0)
        {
            return null;
        }

        var range = ranges[0];
        var style = new TextStyleSnapshot
        {
            FontName = AttributeValue<string>(range, TextPattern.FontNameAttribute) ?? string.Empty,
            FontSize = AttributeValue<double>(range, TextPattern.FontSizeAttribute),
            FontWeight = AttributeValue<int>(range, TextPattern.FontWeightAttribute),
            IsItalic = AttributeValue<bool>(range, TextPattern.IsItalicAttribute),
            ForegroundColor = AttributeValue<int>(range, TextPattern.ForegroundColorAttribute),
            BackgroundColor = AttributeValue<int>(range, TextPattern.BackgroundColorAttribute)
        };

        return style.HasAny ? style : null;
    }

    private static T? AttributeValue<T>(dynamic range, AutomationTextAttribute attribute)
    {
        try
        {
            var value = range.GetAttributeValue(attribute);
            if (value is T typedValue)
            {
                return typedValue;
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return default;
    }

    private static SelectionReadResult? TryReadFocusedNativeSelection()
    {
        var guiThreadInfo = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        if (!NativeMethods.GetGUIThreadInfo(0, ref guiThreadInfo) || guiThreadInfo.hwndFocus == IntPtr.Zero)
        {
            return null;
        }

        var hwnd = guiThreadInfo.hwndFocus;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == CurrentProcessId)
        {
            return null;
        }

        if (NativeMethods.SendMessage(hwnd, EmGetPasswordChar, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero)
        {
            return null;
        }

        var start = 0;
        var end = 0;
        NativeMethods.SendMessageGetSelection(hwnd, EmGetSel, ref start, ref end);
        if (start == end || start < 0 || end < 0)
        {
            return null;
        }

        if (end < start)
        {
            (start, end) = (end, start);
        }

        var length = NativeMethods.SendMessage(hwnd, WmGetTextLength, IntPtr.Zero, IntPtr.Zero).ToInt32();
        if (length <= 0 || start >= length)
        {
            return null;
        }

        var capacity = Math.Min(length, MaxSelectionChars) + 1;
        var buffer = new StringBuilder(capacity);
        NativeMethods.SendMessageText(hwnd, WmGetText, (IntPtr)capacity, buffer);

        var text = buffer.ToString();
        if (start >= text.Length)
        {
            return null;
        }

        var safeEnd = Math.Min(end, text.Length);
        var selectedText = text[start..safeEnd];
        return string.IsNullOrWhiteSpace(selectedText)
            ? null
            : new SelectionReadResult(selectedText, "native edit control");
    }

    private static bool IsCurrentProcessElement(AutomationElement element)
    {
        try
        {
            return element.Current.ProcessId == CurrentProcessId;
        }
        catch (ElementNotAvailableException)
        {
            return true;
        }
    }

    private static string Describe(AutomationElement element)
    {
        try
        {
            var name = element.Current.Name;
            var controlType = element.Current.ControlType?.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal) ?? "control";

            return string.IsNullOrWhiteSpace(name) ? controlType : $"{controlType}: {name}";
        }
        catch (ElementNotAvailableException)
        {
            return "selection";
        }
    }
}

internal sealed class WindowsTextOutputService : ITextOutputService
{
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyAlt = 0x12;
    private const int VirtualKeyV = 0x56;
    private const ushort VirtualKeyEnter = 0x0D;

    public Task WaitForTypeHotkeyReleaseAsync(CancellationToken cancellationToken)
    {
        return WaitForKeysReleasedAsync(cancellationToken, VirtualKeyControl, VirtualKeyAlt, VirtualKeyV);
    }

    public async Task TypeTextAsync(string text, int delayMs, CancellationToken cancellationToken, IProgress<int>? progress = null)
    {
        for (var i = 0; i < text.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var character = text[i];
            if (character == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                SendVirtualKey(VirtualKeyEnter);
            }
            else if (character == '\n')
            {
                SendVirtualKey(VirtualKeyEnter);
            }
            else
            {
                SendUnicodeCharacter(character);
            }

            progress?.Report(i + 1);

            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }
        }
    }

    private static async Task WaitForKeysReleasedAsync(CancellationToken cancellationToken, params int[] virtualKeys)
    {
        while (virtualKeys.Any(IsKeyDown))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellationToken);
        }
    }

    private static void SendUnicodeCharacter(char character)
    {
        SendKeyboardInputs(
            new NativeMethods.KEYBDINPUT
            {
                wScan = character,
                dwFlags = NativeMethods.KeyEventFUnicode
            },
            new NativeMethods.KEYBDINPUT
            {
                wScan = character,
                dwFlags = NativeMethods.KeyEventFUnicode | NativeMethods.KeyEventFKeyUp
            });
    }

    private static void SendVirtualKey(ushort virtualKey)
    {
        SendKeyboardInputs(
            new NativeMethods.KEYBDINPUT
            {
                wVk = virtualKey
            },
            new NativeMethods.KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = NativeMethods.KeyEventFKeyUp
            });
    }

    private static void SendKeyboardInputs(params NativeMethods.KEYBDINPUT[] keyboardInputs)
    {
        var inputs = keyboardInputs.Select(input => new NativeMethods.INPUT
        {
            type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion { ki = input }
        }).ToArray();

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }
}

internal sealed class WindowsContextMenuIntegrationService : IContextMenuIntegrationService
{
    private static readonly ContextMenuEntry[] Entries =
    [
        new(
            @"Software\Classes\*\shell\CopyPasta",
            "Add file path to Copy Pasta",
            "%1"),
        new(
            @"Software\Classes\Directory\shell\CopyPasta",
            "Add folder path to Copy Pasta",
            "%V"),
        new(
            @"Software\Classes\Directory\Background\shell\CopyPasta",
            "Add current folder to Copy Pasta",
            "%V")
    ];

    public ContextMenuIntegrationStatus GetStatus()
    {
        var isInstalled = Entries.All(entry =>
            string.Equals(ReadDefaultValue($@"{entry.SubKey}\command"), CommandValue(entry.Argument), StringComparison.OrdinalIgnoreCase));

        return new ContextMenuIntegrationStatus(
            true,
            isInstalled,
            "Windows Explorer",
            isInstalled
                ? "Explorer context menus are installed for the current user."
                : "Explorer context menus are not installed for the current user.");
    }

    public ContextMenuIntegrationResult Install()
    {
        try
        {
            var appPath = AppCommand.ResolveExecutablePath();
            var iconValue = $"\"{appPath}\",0";

            foreach (var entry in Entries)
            {
                SetString(entry.SubKey, string.Empty, entry.Label);
                SetString(entry.SubKey, "Icon", iconValue);
                SetString(entry.SubKey, "NoWorkingDirectory", string.Empty);
                SetString($@"{entry.SubKey}\command", string.Empty, CommandValue(entry.Argument));
            }

            return new ContextMenuIntegrationResult(true, "Installed Explorer context menus for the current user.");
        }
        catch (Exception ex)
        {
            return new ContextMenuIntegrationResult(false, $"Could not install Explorer context menus: {ex.Message}");
        }
    }

    public ContextMenuIntegrationResult Uninstall()
    {
        try
        {
            foreach (var entry in Entries)
            {
                var exists = false;
                using (var existingKey = Registry.CurrentUser.OpenSubKey(entry.SubKey))
                {
                    exists = existingKey is not null;
                }

                if (!exists)
                {
                    continue;
                }

                Registry.CurrentUser.DeleteSubKeyTree(entry.SubKey);
            }

            return new ContextMenuIntegrationResult(true, "Removed Explorer context menus for the current user.");
        }
        catch (Exception ex)
        {
            return new ContextMenuIntegrationResult(false, $"Could not remove Explorer context menus: {ex.Message}");
        }
    }

    private static string CommandValue(string argument)
    {
        return $"\"{AppCommand.ResolveExecutablePath()}\" {AppCommand.AddToHistoryOption} \"{argument}\"";
    }

    private static string? ReadDefaultValue(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(string.Empty) as string;
    }

    private static void SetString(string subKey, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        if (key is null)
        {
            throw new InvalidOperationException($"Could not open HKCU\\{subKey}.");
        }

        key.SetValue(name, value, RegistryValueKind.String);
    }

    private sealed record ContextMenuEntry(string SubKey, string Label, string Argument);
}
