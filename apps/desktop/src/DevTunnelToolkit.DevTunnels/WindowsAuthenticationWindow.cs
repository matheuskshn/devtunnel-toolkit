using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevTunnelToolkit.DevTunnels;

// The official CLI searches its process ancestors for a visible WAM parent.
// A hidden console alone cannot supply that parent when the agent is headless.
// This window belongs to the agent, contains no credentials and exists only
// during an explicitly requested Microsoft browser login.
[SupportedOSPlatform("windows")]
internal sealed class WindowsAuthenticationWindow : IDisposable
{
    private readonly TaskCompletionSource<nint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action cancel;
    private readonly Thread thread;
    private readonly WindowProcedure procedure;
    private nint window;
    private int disposed;
    private const uint CloseNormally = 0x8001;

    private WindowsAuthenticationWindow(Action cancel)
    {
        this.cancel = cancel;
        procedure = HandleMessage;
        thread = new Thread(Run) { IsBackground = true, Name = "Toolkit authentication window" };
        thread.SetApartmentState(ApartmentState.STA);
    }

    internal static WindowsAuthenticationWindow Show(Action cancel)
    {
        var host = new WindowsAuthenticationWindow(cancel);
        host.thread.Start();
        try { host.ready.Task.GetAwaiter().GetResult(); return host; }
        catch { host.Dispose(); throw; }
    }

    internal nint Handle => window;

    private void Run()
    {
        var name = "DevTunnelToolkit.Authentication." + Guid.NewGuid().ToString("N");
        var instance = GetModuleHandle(null);
        nint icon = 0, smallIcon = 0, created = 0;
        ushort registered = 0;
        try
        {
            if (Environment.ProcessPath is { } executable) ExtractIconEx(executable, 0, out icon, out smallIcon, 1);
            var type = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = procedure,
                Instance = instance,
                Icon = icon,
                SmallIcon = smallIcon,
                Cursor = LoadCursor(0, 32512),
                Background = 6, // COLOR_WINDOW + 1.
                ClassName = name
            };
            registered = RegisterClassEx(ref type);
            if (registered == 0) throw new Win32Exception();
            created = CreateWindowEx(0x00040000, name, "DevTunnel Toolkit - Autenticação", 0x00C80000,
                Math.Max(0, (GetSystemMetrics(0) - 500) / 2), Math.Max(0, (GetSystemMetrics(1) - 185) / 2),
                500, 185, 0, 0, instance, 0);
            if (created == 0) throw new Win32Exception();
            window = created;
            var label = CreateWindowEx(0, "STATIC", "Conclua o login na janela Microsoft.\r\nEsta janela será fechada ao concluir ou cancelar.",
                0x50000000, 20, 22, 450, 55, created, 0, instance, 0);
            var button = CreateWindowEx(0, "BUTTON", "Cancelar login", 0x50010000,
                335, 92, 140, 30, created, 1, instance, 0);
            if (label == 0 || button == 0) throw new Win32Exception();
            var font = GetStockObject(17); // DEFAULT_GUI_FONT, shared handle.
            SendMessage(label, 0x0030, font, 1); SendMessage(button, 0x0030, font, 1);
            ShowWindow(created, 5);
            SetForegroundWindow(created);
            ready.TrySetResult(created);
            int result;
            while ((result = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                if (IsDialogMessage(created, ref message)) continue;
                TranslateMessage(ref message); DispatchMessage(ref message);
            }
            if (result < 0) RequestCancellation();
        }
        catch (Exception error)
        {
            if (!ready.TrySetException(error)) RequestCancellation();
        }
        finally
        {
            if (created != 0) DestroyWindow(created);
            if (registered != 0) UnregisterClass(name, instance);
            if (icon != 0) DestroyIcon(icon);
            if (smallIcon != 0) DestroyIcon(smallIcon);
        }
    }

    private nint HandleMessage(nint handle, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0010 || message == 0x0111 && (wParam & 0xffff) == 1) // WM_CLOSE / Cancel button.
        {
            RequestCancellation(); DestroyWindow(handle); return 0;
        }
        if (message == CloseNormally) { DestroyWindow(handle); return 0; }
        if (message == 0x0002) { Interlocked.Exchange(ref window, 0); PostQuitMessage(0); return 0; }
        return DefWindowProc(handle, message, wParam, lParam);
    }

    private void RequestCancellation()
    {
        // Exceptions must never escape a native window callback.
        try { cancel(); } catch (Exception) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (window != 0) PostMessage(window, CloseNormally, 0, 0);
        if (thread.IsAlive && Thread.CurrentThread != thread) thread.Join(TimeSpan.FromSeconds(5));
        GC.KeepAlive(procedure);
    }

    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass type);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string text, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out Message message, nint window, uint first, uint last);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "IsDialogMessageW")]
    private static extern bool IsDialogMessage(nint window, ref Message message);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern nint LoadCursor(nint instance, nint name);
    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int index);
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string path, int index, out nint large, out nint small, uint count);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
}
