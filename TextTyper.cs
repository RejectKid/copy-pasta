using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CopyPasta;

internal static class TextTyper
{
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyAlt = 0x12;
    private const int VirtualKeyV = 0x56;
    private const ushort VirtualKeyEnter = 0x0D;

    public static Task WaitForPasteHotKeyReleaseAsync(CancellationToken cancellationToken) =>
        WaitForKeysReleasedAsync(cancellationToken, VirtualKeyControl, VirtualKeyAlt, VirtualKeyV);

    private static async Task WaitForKeysReleasedAsync(CancellationToken cancellationToken, params int[] virtualKeys)
    {
        while (virtualKeys.Any(IsKeyDown))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellationToken);
        }
    }

    public static async Task TypeTextAsync(string text, int delayMs, CancellationToken cancellationToken, IProgress<int>? progress = null)
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
