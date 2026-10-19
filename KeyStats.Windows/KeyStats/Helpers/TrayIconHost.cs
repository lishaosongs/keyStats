using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace KeyStats.Helpers;

/// <summary>
/// Owns one notification icon and its callback window for the application's lifetime.
/// All members must be called on the UI thread.
/// </summary>
internal sealed class TrayIconHost : Forms.NativeWindow, IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = 0x8001; // WM_APP + 1
    private const int LeftButtonUp = 0x0202;
    private const int RightButtonUp = 0x0205;
    private const uint Add = 0;
    private const uint Modify = 1;
    private const uint Delete = 2;
    private const uint IconFlags = 0x01 | 0x02 | 0x04; // NIF_MESSAGE | NIF_ICON | NIF_TIP
    private readonly uint _taskbarCreatedMessage;
    private bool _disposed;

    public event Forms.MouseEventHandler? MouseClick;
    public event EventHandler? RecoveryRequested;

    public TrayIconHost()
    {
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        // A hidden top-level window receives TaskbarCreated broadcasts; a message-only window does not.
        CreateHandle(new Forms.CreateParams { Caption = "KeyStatsTrayIcon" });
    }

    /// <summary>
    /// Refreshes the image, tooltip and callback together, or restores a missing icon with the same identity.
    /// </summary>
    public bool TryRefresh(Icon? icon, string tooltip)
    {
        if (_disposed || Handle == IntPtr.Zero || icon == null)
        {
            return false;
        }

        var data = CreateData();
        data.Flags = IconFlags;
        data.CallbackMessage = CallbackMessage;
        data.Icon = icon.Handle;
        data.Tip = tooltip.Length > 127 ? tooltip.Substring(0, 127) : tooltip;

        // Never delete/recreate a healthy icon during resume. Both operations use the same HWND/uID.
        return Shell_NotifyIcon(Modify, ref data) || Shell_NotifyIcon(Add, ref data);
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if (!_disposed)
        {
            if (_taskbarCreatedMessage != 0 && unchecked((uint)message.Msg) == _taskbarCreatedMessage)
            {
                RecoveryRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (message.Msg == CallbackMessage && message.WParam == new IntPtr(IconId))
            {
                var mouseMessage = message.LParam.ToInt64();
                if (mouseMessage == LeftButtonUp || mouseMessage == RightButtonUp)
                {
                    var cursor = Forms.Control.MousePosition;
                    var button = mouseMessage == LeftButtonUp ? Forms.MouseButtons.Left : Forms.MouseButtons.Right;
                    MouseClick?.Invoke(this, new Forms.MouseEventArgs(button, 1, cursor.X, cursor.Y, 0));
                }
            }
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Handle != IntPtr.Zero)
        {
            var data = CreateData();
            Shell_NotifyIcon(Delete, ref data);
            DestroyHandle();
        }

        MouseClick = null;
        RecoveryRequested = null;
    }

    private NotifyIconData CreateData() => new NotifyIconData
    {
        Size = (uint)Marshal.SizeOf(typeof(NotifyIconData)),
        Window = Handle,
        Id = IconId,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }
}
