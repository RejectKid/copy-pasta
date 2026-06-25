using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace CopyPasta;

internal sealed record SelectionReadResult(string Text, string Source, TextStyleSnapshot? Style = null);

internal sealed class TextStyleSnapshot
{
    public string FontName { get; set; } = string.Empty;
    public double? FontSize { get; set; }
    public int? FontWeight { get; set; }
    public bool? IsItalic { get; set; }
    public int? ForegroundColor { get; set; }
    public int? BackgroundColor { get; set; }

    public bool HasAny =>
        !string.IsNullOrEmpty(FontName) ||
        FontSize.HasValue ||
        FontWeight.HasValue ||
        IsItalic.HasValue ||
        ForegroundColor.HasValue ||
        BackgroundColor.HasValue;
}

internal static class SelectionReader
{
    private const uint EmGetSel = 0x00B0;
    private const uint EmGetPasswordChar = 0x00D2;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;

    private const int MaxSelectionChars = 200_000;
    private static readonly int CurrentProcessId = Process.GetCurrentProcess().Id;

    public static SelectionReadResult? TryReadSelectedText(out string? error)
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

            var nativeResult = TryReadFocusedNativeSelection();
            if (nativeResult is not null)
            {
                return nativeResult;
            }

            return null;
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

            if (string.IsNullOrWhiteSpace(name))
            {
                return controlType;
            }

            return $"{controlType}: {name}";
        }
        catch (ElementNotAvailableException)
        {
            return "selection";
        }
    }
}
