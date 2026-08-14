using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TokenOptimizer.Core.RateLimit;

/// <summary>
/// P/Invoke surface for reading a target console's visible screen buffer and
/// injecting keystrokes into it. The original PowerShell launcher ran INSIDE
/// the same console its child (claude.exe/codex.exe) inherited, so a plain
/// GetStdHandle/ReadConsoleOutputCharacter against its own console handle
/// was enough. This app is a GUI process with no console of its own - when
/// it launches a console CLI, Windows gives that child a brand-new console
/// window, so this class instead AttachConsole()s to the CHILD's console
/// process id for the duration of a read/write, then detaches. Keystrokes
/// are injected via WriteConsoleInput directly into that console's input
/// buffer (targets the exact console regardless of window focus), rather
/// than global SendInput.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConsoleWatcherInterop
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_INPUT_HANDLE = -10;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT { public short Left; public short Top; public short Right; public short Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleOutputCharacterW(
        IntPtr hConsoleOutput, [Out] char[] lpCharacter, uint nLength, COORD dwReadCoord, out uint lpNumberOfCharsRead);

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD_UNION { [FieldOffset(0)] public KEY_EVENT_RECORD KeyEvent; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public bool bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_RECORD { public ushort EventType; public INPUT_RECORD_UNION Event; }

    private const ushort KEY_EVENT = 0x0001;
    private const ushort VK_RETURN = 0x0D;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInput(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

    /// <summary>Attaches to the given console process, reads its currently visible screen text, then detaches. Returns null on any failure.</summary>
    public static string? ReadVisibleScreen(int consoleOwnerProcessId)
    {
        if (!AttachConsole((uint)consoleOwnerProcessId)) return null;
        try
        {
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (handle == IntPtr.Zero || !GetConsoleScreenBufferInfo(handle, out var info)) return null;

            var width = info.dwSize.X;
            var visibleHeight = (short)(info.srWindow.Bottom - info.srWindow.Top + 1);
            if (width <= 0 || visibleHeight <= 0) return null;

            var buffer = new char[width * visibleHeight];
            var readCoord = new COORD { X = 0, Y = info.srWindow.Top };
            if (!ReadConsoleOutputCharacterW(handle, buffer, (uint)buffer.Length, readCoord, out var charsRead)) return null;

            return new string(buffer, 0, (int)charsRead);
        }
        finally
        {
            FreeConsole();
        }
    }

    /// <summary>Attaches to the given console process and injects an Enter keypress into its input buffer.</summary>
    public static void SendEnter(int consoleOwnerProcessId) => SendKey(consoleOwnerProcessId, VK_RETURN, '\r');

    /// <summary>Attaches to the given console process and types the given string followed by Enter.</summary>
    public static void SendStringWithEnter(int consoleOwnerProcessId, string text)
    {
        if (!AttachConsole((uint)consoleOwnerProcessId)) return;
        try
        {
            var handle = GetStdHandle(STD_INPUT_HANDLE);
            if (handle == IntPtr.Zero) return;

            foreach (var ch in text)
            {
                WriteChar(handle, ch);
            }
            WriteChar(handle, '\r');
        }
        finally
        {
            FreeConsole();
        }
    }

    private static void SendKey(int consoleOwnerProcessId, ushort virtualKeyCode, char ch)
    {
        if (!AttachConsole((uint)consoleOwnerProcessId)) return;
        try
        {
            var handle = GetStdHandle(STD_INPUT_HANDLE);
            if (handle == IntPtr.Zero) return;
            WriteKeyRecords(handle, virtualKeyCode, ch);
        }
        finally
        {
            FreeConsole();
        }
    }

    private static void WriteChar(IntPtr inputHandle, char ch) => WriteKeyRecords(inputHandle, 0, ch);

    private static void WriteKeyRecords(IntPtr inputHandle, ushort virtualKeyCode, char ch)
    {
        var down = new INPUT_RECORD
        {
            EventType = KEY_EVENT,
            Event = new INPUT_RECORD_UNION
            {
                KeyEvent = new KEY_EVENT_RECORD { bKeyDown = true, wRepeatCount = 1, wVirtualKeyCode = virtualKeyCode, UnicodeChar = ch },
            },
        };
        var up = down;
        up.Event.KeyEvent.bKeyDown = false;

        WriteConsoleInput(inputHandle, [down, up], 2, out _);
    }
}
