using System;
using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Window-less global clipboard (CF_UNICODETEXT only) via raw Win32 P/Invoke.
/// No STA (raw user32 clipboard is apartment-agnostic) — runs on a plain Task. SetText follows
/// the strict ownership protocol: the OS owns the HGLOBAL only after a successful SetClipboardData;
/// every other exit path GlobalFrees it.</summary>
public static class ClipboardAccess
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int OpenRetries = 5;
    private const int RetryDelayMs = 50;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);

    private static bool TryOpen()
    {
        for (int i = 0; i < OpenRetries; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            System.Threading.Thread.Sleep(RetryDelayMs);
        }
        return false;
    }

    public static Task<string> GetTextAsync() => Task.Run(() =>
    {
        Console.Error.WriteLine("[audit] desktop_clipboard_get: reading the global clipboard.");
        if (!TryOpen())
            throw new ToolException(ToolErrorCode.ClipboardUnavailable, "Could not open the clipboard (locked by another process).", "retry in a moment");
        try
        {
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return string.Empty; // empty OR non-text formats
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return string.Empty;
            try { return Marshal.PtrToStringUni(p) ?? string.Empty; }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    });

    public static Task SetTextAsync(string text) => Task.Run(() =>
    {
        if (string.IsNullOrEmpty(text))
        {
            if (!TryOpen())
                throw new ToolException(ToolErrorCode.ClipboardUnavailable, "Could not open the clipboard (locked by another process).", "retry in a moment");
            try { EmptyClipboard(); } finally { CloseClipboard(); }
            return;
        }

        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(((uint)text.Length + 1) * 2));
        if (h == IntPtr.Zero)
            throw new ToolException(ToolErrorCode.ClipboardUnavailable, "Could not allocate clipboard memory.", "retry in a moment");
        bool osOwnsHandle = false;
        bool opened = false;
        try
        {
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero)
                throw new ToolException(ToolErrorCode.ClipboardUnavailable, "Could not lock clipboard memory.", "retry in a moment");
            try
            {
                // Copy the UTF-16 chars + a single wide-null terminator.
                Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                Marshal.WriteInt16(p, text.Length * 2, 0);
            }
            finally { GlobalUnlock(h); }

            if (!TryOpen())
                throw new ToolException(ToolErrorCode.ClipboardUnavailable, "Could not open the clipboard (locked by another process).", "retry in a moment");
            opened = true;
            EmptyClipboard();
            if (SetClipboardData(CF_UNICODETEXT, h) != IntPtr.Zero)
                osOwnsHandle = true; // OS now owns h — must NOT free it
            else
                throw new ToolException(ToolErrorCode.ClipboardUnavailable, "SetClipboardData failed.", "retry in a moment");
        }
        finally
        {
            if (!osOwnsHandle && h != IntPtr.Zero) GlobalFree(h);
            if (opened) CloseClipboard();
        }
    });
}
