using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VoxType.Services;

/// <summary>
/// Injects text into the active (foreground) window using the Clipboard +
/// Win32 SendInput (Ctrl+V) approach. Raw Unicode keystrokes corrupt Bangla
/// conjunct characters (যুক্তবর্ণ) in Chrome/Electron apps, so we paste instead.
/// All clipboard operations run on the WPF STA Dispatcher.
/// </summary>
public class TextInjectionService
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
        [FieldOffset(0)] public HARDWAREINPUT Hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint UMsg;
        public ushort WParamL;
        public ushort WParamH;
    }

    /// <summary>
    /// 1. Save current clipboard text (if any)  2. Set transcribed text + trailing space
    /// 3. Wait 20ms, send Ctrl+V                4. Wait 80ms, restore clipboard
    /// </summary>
    public async Task<bool> InjectTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return false;

        string? previousText = null;
        bool hadText = false;

        // 1) Save current clipboard text (STA, retry on COMException)
        await dispatcher.InvokeAsync(() =>
        {
            TryWithRetry(() =>
            {
                hadText = Clipboard.ContainsText();
                if (hadText) previousText = Clipboard.GetText();
            });
        });

        // 2) Put transcribed text + trailing space on the clipboard (STA)
        await dispatcher.InvokeAsync(() =>
            TryWithRetry(() => Clipboard.SetText(text + " ")));

        // 3) Give the clipboard time to settle, then simulate Ctrl+V
        await Task.Delay(20);
        bool sent = SendCtrlV();

        // 4) Let the paste land, then restore the previous clipboard (STA)
        await Task.Delay(80);
        await dispatcher.InvokeAsync(() =>
        {
            try
            {
                TryWithRetry(() =>
                {
                    if (hadText && previousText != null) Clipboard.SetText(previousText);
                    else Clipboard.Clear();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoxType] Failed to restore clipboard: {ex.Message}");
            }
        });

        return sent;
    }

    private static bool SendCtrlV()
    {
        bool winStuck = GetAsyncKeyState(VK_LWIN) < 0 || GetAsyncKeyState(VK_RWIN) < 0;

        var inputs = new INPUT[]
        {
            KeyInput(VK_CONTROL, down: true),
            KeyInput(VK_V, down: true),
            KeyInput(VK_V, down: false),
            KeyInput(VK_CONTROL, down: false),
        };

        try
        {
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            return sent == inputs.Length;
        }
        finally
        {
            // Ensure no keys are left pressed (Ctrl, V). This guard prevents
            // stuck modifier bugs even if SendInput fails.
            var release = new[]
            {
                KeyInput(VK_CONTROL, down: false),
                KeyInput(VK_V, down: false),
            };
            SendInput((uint)release.Length, release, Marshal.SizeOf<INPUT>());
        }
    }

    private static INPUT KeyInput(ushort vk, bool down) => new()
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            Ki = new KEYBDINPUT
            {
                WVk = vk,
                DwFlags = down ? 0 : KEYEVENTF_KEYUP,
                DwExtraInfo = IntPtr.Zero,
            },
        },
    };

    /// <summary>
    /// Runs an action, retrying when the clipboard is locked by another process.
    /// </summary>
    private static void TryWithRetry(Action action, int attempts = 4, int delayMs = 40)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (COMException) when (i < attempts - 1)
            {
                Thread.Sleep(delayMs);
            }
        }
    }
}