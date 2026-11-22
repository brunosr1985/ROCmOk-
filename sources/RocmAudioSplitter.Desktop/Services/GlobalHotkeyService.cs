using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RocmAudioSplitter.Desktop.Services;

public enum HotkeyAction
{
    ToggleVocalSuppression = 1,
    ToggleMicMute = 2,
    ToggleLiveEngine = 3,
    LoadProfile1 = 4,
    LoadProfile2 = 5,
    LoadProfile3 = 6
}

/// <summary>
/// Registra atalhos globais no Windows via RegisterHotKey em uma message-only window dedicada,
/// permitindo acionar controles durante jogos em tela cheia ou transmissões no OBS/Meld Studio.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_QUIT = 0x0012;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly Thread _messageLoopThread;
    private readonly ManualResetEventSlim _readyEvent = new(false);
    private nint _hwnd;
    private uint _nativeThreadId;

    public event Action<HotkeyAction>? HotkeyPressed;
    public string RegistrationSummary { get; private set; } = "Hotkeys inactive";

    public GlobalHotkeyService()
    {
        _messageLoopThread = new Thread(MessageLoop)
        {
            Name = "Win32-GlobalHotkeyLoop",
            IsBackground = true
        };
        _messageLoopThread.SetApartmentState(ApartmentState.STA);
        _messageLoopThread.Start();
        _readyEvent.Wait(TimeSpan.FromSeconds(2));
    }

    private void MessageLoop()
    {
        _nativeThreadId = GetCurrentThreadId();
        // HWND_MESSAGE = -3 (janela exclusiva para mensagens Win32)
        _hwnd = CreateWindowExW(0, "STATIC", "RocmHotkeySink", 0, 0, 0, 0, 0, new nint(-3), nint.Zero, nint.Zero, nint.Zero);

        var registered = new List<string>();
        uint mods = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;

        if (RegisterHotKey(_hwnd, (int)HotkeyAction.ToggleVocalSuppression, mods, (uint)'V'))
            registered.Add("Ctrl+Alt+V (Vocal)");
        if (RegisterHotKey(_hwnd, (int)HotkeyAction.ToggleMicMute, mods, (uint)'M'))
            registered.Add("Ctrl+Alt+M (Mic Mute)");
        if (RegisterHotKey(_hwnd, (int)HotkeyAction.ToggleLiveEngine, mods, (uint)'S'))
            registered.Add("Ctrl+Alt+S (Start/Stop)");
        if (RegisterHotKey(_hwnd, (int)HotkeyAction.LoadProfile1, mods, (uint)'1'))
            registered.Add("Ctrl+Alt+1..3 (Presets)");

        RegisterHotKey(_hwnd, (int)HotkeyAction.LoadProfile2, mods, (uint)'2');
        RegisterHotKey(_hwnd, (int)HotkeyAction.LoadProfile3, mods, (uint)'3');

        RegistrationSummary = registered.Count > 0
            ? string.Join("  •  ", registered)
            : "Hotkeys unavailable (in use by another app)";

        _readyEvent.Set();

        while (GetMessageW(out MSG msg, nint.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                var action = (HotkeyAction)msg.wParam.ToInt32();
                HotkeyPressed?.Invoke(action);
            }
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        foreach (HotkeyAction act in Enum.GetValues<HotkeyAction>())
        {
            UnregisterHotKey(_hwnd, (int)act);
        }

        if (_hwnd != nint.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }

    public void Dispose()
    {
        if (_nativeThreadId != 0)
        {
            PostThreadMessageW(_nativeThreadId, WM_QUIT, nint.Zero, nint.Zero);
            _messageLoopThread.Join(500);
        }
        _readyEvent.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}