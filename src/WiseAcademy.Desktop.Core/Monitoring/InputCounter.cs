namespace WiseAcademy.Desktop.Core.Monitoring;

/// <summary>Contagens de teclado e rato num intervalo. NUNCA guarda as teclas — só quantas.</summary>
public sealed class InputCounts
{
    public int Keystrokes, Letters, Digits, Symbols, Backspaces, LeftClicks, RightClicks, MiddleClicks, Scrolls;
    public long MousePx;
}

/// <summary>
/// Hooks globais de baixo nível (WH_KEYBOARD_LL / WH_MOUSE_LL) numa thread própria com
/// ciclo de mensagens. Porte dos serviços Keyboard/Mouse do TimeZoon, sem o registo de palavras.
/// </summary>
public sealed class InputCounter : IDisposable
{
    private readonly object _lock = new();
    private InputCounts _cur = new();
    private Thread? _thread;
    private uint _threadId;
    private Win32.HookProc? _kbProc, _msProc; // referências vivas (o GC não pode recolher os delegates)
    private IntPtr _kb, _ms;
    private int _lastX = int.MinValue, _lastY;

    public void Start()
    {
        if (_thread is not null) return;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Win32.GetCurrentThreadId();
            _kbProc = KeyboardHook; _msProc = MouseHook;
            var mod = Win32.GetModuleHandle(null);
            _kb = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _kbProc, mod, 0);
            _ms = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _msProc, mod, 0);
            ready.Set();
            while (Win32.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            if (_kb != IntPtr.Zero) Win32.UnhookWindowsHookEx(_kb);
            if (_ms != IntPtr.Zero) Win32.UnhookWindowsHookEx(_ms);
        }) { IsBackground = true, Name = "wa-input-hooks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(3000);
    }

    /// <summary>Devolve as contagens desde a última chamada e recomeça.</summary>
    public InputCounts Take()
    {
        lock (_lock) { var c = _cur; _cur = new InputCounts(); return c; }
    }

    private IntPtr KeyboardHook(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && (w == 0x0100 || w == 0x0104)) // WM_KEYDOWN | WM_SYSKEYDOWN
        {
            var vk = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(l).vkCode;
            var modifier = vk is 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);
            if (!modifier)
                lock (_lock)
                {
                    _cur.Keystrokes++;
                    if (vk is >= 0x41 and <= 0x5A) _cur.Letters++;
                    else if (vk is (>= 0x30 and <= 0x39) or (>= 0x60 and <= 0x69)) _cur.Digits++;
                    else if (vk == 0x08) _cur.Backspaces++;
                    else if (vk is (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF) or (>= 0x6A and <= 0x6F)) _cur.Symbols++;
                }
        }
        return Win32.CallNextHookEx(_kb, code, w, l);
    }

    private IntPtr MouseHook(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            var m = (uint)w;
            lock (_lock)
            {
                switch (m)
                {
                    case 0x0201: _cur.LeftClicks++; break;
                    case 0x0204: _cur.RightClicks++; break;
                    case 0x0207: _cur.MiddleClicks++; break;
                    case 0x020A: case 0x020E: _cur.Scrolls++; break;
                    case 0x0200:
                        var p = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(l).pt;
                        if (_lastX != int.MinValue) _cur.MousePx += Math.Abs(p.X - _lastX) + Math.Abs(p.Y - _lastY);
                        _lastX = p.X; _lastY = p.Y;
                        break;
                }
            }
        }
        return Win32.CallNextHookEx(_ms, code, w, l);
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread = null;
    }
}
