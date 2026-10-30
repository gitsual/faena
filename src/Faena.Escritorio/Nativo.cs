using System.Runtime.InteropServices;

namespace Faena.Escritorio;

/// <summary>Las llamadas a Win32 que no tiene .NET: escritorios, procesos en un escritorio, mensajes de teclado y capturas.</summary>
internal static partial class Nativo
{
    public const uint DesktopTodo = 0x10000000; // GENERIC_ALL
    public const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmChar = 0x0102;
    public const uint PwRenderFullContent = 2;
    public const uint WmCommand = 0x0111;
    public const int BnClicked = 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder nombre, int max);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int GetDlgCtrlID(IntPtr hwnd);

    /// <summary>La clase de ventana de un control («Button», «WindowsForms10.BUTTON.app…»).</summary>
    public static string Clase(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateDesktopW")]
    public static extern IntPtr CreateDesktop(string nombre, IntPtr dispositivo, IntPtr modo, uint flags, uint acceso, IntPtr seguridad);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadDesktop(IntPtr escritorio);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(IntPtr escritorio);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcess(string? aplicacion, System.Text.StringBuilder lineaDeComandos, IntPtr atributosProceso, IntPtr atributosHilo,
        [MarshalAs(UnmanagedType.Bool)] bool heredar, uint flags, IntPtr entorno, string? carpeta, ref StartupInfo si, out ProcessInformation pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    /// <summary>Las teclas que se pueden mandar por mensaje: código virtual y, si escribe algo, su carácter.</summary>
    public static (int Vk, char? Caracter)? Tecla(string tecla) => tecla switch
    {
        "Enter" => (0x0D, '\r'),
        "Tab" => (0x09, '\t'),
        "Escape" => (0x1B, (char)0x1B),
        "Backspace" => (0x08, '\b'),
        "Space" => (0x20, ' '),
        "ArrowDown" => (0x28, null),
        "ArrowUp" => (0x26, null),
        "ArrowLeft" => (0x25, null),
        "ArrowRight" => (0x27, null),
        "PageDown" => (0x22, null),
        "PageUp" => (0x21, null),
        "Home" => (0x24, null),
        "End" => (0x23, null),
        "Delete" => (0x2E, null),
        _ => null,
    };
}
