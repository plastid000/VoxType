using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace VoxType.Services;

/// <summary>
/// Push-to-Talk (Hold-to-Talk) state machine via a Win32 Low-Level Keyboard
/// Hook (WH_KEYBOARD_LL), because RegisterHotKey cannot distinguish Right
/// Alt / Right Ctrl:
///   Win (L or R) + Right Alt   -> PttStarted(BN); keep recording while held
///   Win (L or R) + Right Ctrl  -> PttStarted(EN); keep recording while held
///
/// Once the PTT key is down, the user may release Win - recording continues
/// until the PTT key ITSELF is released (PttStopped). The first key-down of
/// the PTT key and every OS key-repeat while it is held are consumed (so
/// AltGr / the Start menu / app menus stay quiet), but key-up events are
/// NEVER consumed: they always pass through CallNextHookEx so Windows always
/// releases the modifiers (swallowing a Win key-up would corrupt shortcuts).
/// A harmless 0xE8 (unassigned virtual key - the standard AutoHotkey
/// "menu mask" key) down+up is injected via SendInput while Win is still
/// physically down to suppress the Start Menu and Alt-menu focus. Windows 11
/// ignores VK_NONAME (0xFF) for Start Menu suppression, so 0xE8 is used.
/// </summary>
public class HotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104; // Alt keys arrive as SYSKEYDOWN, not KEYDOWN
    private const int WM_SYSKEYUP = 0x0105;
    private const int LLKHF_INJECTED = 0x10;  // set on events we inject via SendInput

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_RMENU = 0xA5;     // Right Alt (AltGr alone has no Win -> never triggers)
    private const int VK_RCONTROL = 0xA3;  // Right Ctrl
    private const int VK_MENU_MASK = 0xE8; // unassigned vk injected to suppress the Start Menu
                                           // (0xFF / VK_NONAME is ignored by Windows 11; 0xE8 is
                                           // the standard AutoHotkey menu-mask key)

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

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

    private readonly Dispatcher _dispatcher;
    private readonly LowLevelKeyboardProc _hookProc; // kept alive (GC) for the hook's lifetime
    private IntPtr _hookId = IntPtr.Zero;
    private bool _registered;
    private bool _disposed;

    // Push-to-Talk state: 0 = idle, VK_RMENU = BN active, VK_RCONTROL = EN
    // active. Only one PTT key can be active at a time.
    private int _activePttKey;

    /// <summary>Raised (on the Dispatcher) when a PTT key starts recording.</summary>
    public event Action<VoxLanguage>? PttStarted;

    /// <summary>Raised (on the Dispatcher) when the active PTT key is released.</summary>
    public event Action<VoxLanguage>? PttStopped;

    public HotkeyService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hookProc = HookCallback;
    }

    /// <summary>
    /// Installs the global low-level keyboard hook. Must be called on the WPF
    /// (STA, message-pumping) thread so the hook is serviced by the dispatcher.
    /// </summary>
    public void Register()
    {
        if (_registered) return;

        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hookId == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Failed to install keyboard hook (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        _registered = true;
        System.Diagnostics.Debug.WriteLine("[VoxType] Low-level keyboard hook installed.");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)data.vkCode;

            // Ignore events WE injected (Ctrl+V paste, 0xE8 menu-mask) so
            // they can never interfere with modifier tracking or re-trigger
            // shortcuts.
            if ((data.flags & LLKHF_INJECTED) != 0)
            {
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // Alt sends WM_SYSKEYDOWN (0x0104), not WM_KEYDOWN (0x0100), so
            // BOTH must be checked for the Right-Alt trigger.
            bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (isKeyDown)
            {
                // GetAsyncKeyState is the reliable source of truth for the
                // Win key state (tracked booleans can drift).
                bool winDown = GetAsyncKeyState(VK_LWIN) < 0 || GetAsyncKeyState(VK_RWIN) < 0;

                if (_activePttKey == 0 && winDown)
                {
                    if (vk == VK_RMENU)
                    {
                        // Win + Right Alt -> Bangla PTT.
                        _activePttKey = VK_RMENU;
                        InjectMenuMaskKey(); // suppresses Start Menu + Alt-menu focus
                        RaiseSafe(PttStarted, VoxLanguage.BN);
                        return (IntPtr)1; // consume this key-down only
                    }

                    if (vk == VK_RCONTROL)
                    {
                        // Win + Right Ctrl -> English PTT.
                        _activePttKey = VK_RCONTROL;
                        InjectMenuMaskKey();
                        RaiseSafe(PttStarted, VoxLanguage.EN);
                        return (IntPtr)1; // consume this key-down only
                    }
                }

                // OS key-repeat while the active PTT key is held: consume it
                // so it cannot spam events or trigger application menus.
                if (vk == _activePttKey)
                {
                    return (IntPtr)1;
                }
            }
            else if (isKeyUp)
            {
                // NEVER consume a key-up - it must always reach
                // CallNextHookEx so Windows releases the modifier. Releasing
                // Win is irrelevant here (Win vk never equals a PTT vk), so
                // recording only ever stops when the PTT key itself goes up.
                if (vk == _activePttKey)
                {
                    VoxLanguage lang = vk == VK_RMENU ? VoxLanguage.BN : VoxLanguage.EN;
                    _activePttKey = 0;
                    RaiseSafe(PttStopped, lang);
                }
            }
        }

        // ALWAYS pass every event (especially all key-ups) to the system.
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void RaiseSafe<T>(Action<T>? handler, T arg)
    {
        // Fire-and-forget so the LL hook returns promptly (Windows unloads hooks
        // that exceed LowLevelHooksTimeout). Marshals to the WPF Dispatcher.
        if (handler == null) return;
        try
        {
            _dispatcher.BeginInvoke(() => handler(arg));
        }
        catch
        {
            // Dispatcher shutting down; ignore.
        }
    }

    /// <summary>
    /// Injects a 0xE8 (unassigned vk) key-down + key-up via SendInput while
    /// Win is still down, which is enough to keep the Start Menu from opening
    /// after we consumed the trigger key-down. Windows 11 ignores VK_NONAME
    /// (0xFF) for Start Menu suppression; 0xE8 is the standard AutoHotkey
    /// menu-mask key. Key-down uses wScan = 0 / dwFlags = 0, key-up uses
    /// KEYEVENTF_KEYUP.
    /// </summary>
    private static void InjectMenuMaskKey()
    {
        var inputs = new[]
        {
            KeyInput(VK_MENU_MASK, up: false), // wScan = 0, dwFlags = 0
            KeyInput(VK_MENU_MASK, up: true),  // wScan = 0, dwFlags = KEYEVENTF_KEYUP
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(int vk, bool up) => new()
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            Ki = new KEYBDINPUT
            {
                WVk = (ushort)vk,
                DwFlags = up ? KEYEVENTF_KEYUP : 0,
                DwExtraInfo = IntPtr.Zero,
            },
        },
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registered && _hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _registered = false;
        }
    }
}