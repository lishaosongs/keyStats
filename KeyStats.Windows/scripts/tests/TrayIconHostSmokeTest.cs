using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using KeyStats.Helpers;
using Forms = System.Windows.Forms;
// Standalone integration test, compiled together with TrayIconHost by Test-TrayIconRecovery.ps1.
internal static class TrayIconHostSmokeTest
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string text);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref Identifier id, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] struct Identifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    static Identifier GetId(IntPtr window) => new Identifier { Size=(uint)Marshal.SizeOf(typeof(Identifier)), Window=window, Id=1 };
    static void Check(bool condition, string description) { if (!condition) throw new Exception(description); Console.WriteLine("PASS: " + description); }
    [STAThread] static int Main()
    {
        using (var host = new TrayIconHost())
        using (var icon = (Icon)SystemIcons.Application.Clone())
        {
            var hwnd = host.Handle;
            var id = GetId(hwnd);
            int left = 0, right = 0, recovery = 0;
            host.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) left++; if (e.Button == Forms.MouseButtons.Right) right++; };
            host.RecoveryRequested += (_, e) => recovery++;
            Check(!host.TryRefresh(null, "missing image"), "missing image fails cleanly");
            Check(host.TryRefresh(icon, "KeyStats isolated tray test"), "register icon in real Explorer tray");
            Rect rect;
            Check(Shell_NotifyIconGetRect(ref id, out rect) == 0, "Shell confirms registration");
            for (int attempt = 0; attempt < 25; attempt++)
            {
                if (!host.TryRefresh(icon, new string('x', 150)))
                {
                    throw new Exception("Refresh failed on attempt " + attempt);
                }
            }
            Check(host.Handle == hwnd, "25 refreshes with long tooltips retain callback HWND");
            SendMessage(hwnd, 0x8001, new IntPtr(1), new IntPtr(0x0202));
            SendMessage(hwnd, 0x8001, new IntPtr(1), new IntPtr(0x0205));
            SendMessage(hwnd, 0x8001, new IntPtr(99), new IntPtr(0x0205));
            Check(left == 1 && right == 1, "left/right callbacks route once; unrelated IDs ignored");
            SendMessage(hwnd, unchecked((int)RegisterWindowMessage("TaskbarCreated")), IntPtr.Zero, IntPtr.Zero);
            Check(recovery == 1 && host.Handle == hwnd, "TaskbarCreated requests recovery without replacing callback window");
            // Remove only this test icon to simulate registration lost during Explorer restart.
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var data = typeof(TrayIconHost).GetMethod("CreateData", flags).Invoke(host, null);
            var native = typeof(TrayIconHost).GetMethod("Shell_NotifyIcon", BindingFlags.Static | BindingFlags.NonPublic);
            Check((bool)native.Invoke(null, new object[] { (uint)2, data }), "simulate lost Shell registration for isolated test icon");
            Check(Shell_NotifyIconGetRect(ref id, out rect) != 0, "Shell confirms test registration missing");
            Check(host.TryRefresh(icon, "KeyStats restored test"), "missing registration restored via ADD fallback");
            Check(Shell_NotifyIconGetRect(ref id, out rect) == 0 && host.Handle == hwnd, "restored icon has same HWND and ID");
            SendMessage(hwnd, 0x8001, new IntPtr(1), new IntPtr(0x0205));
            Check(right == 2, "right click remains wired after restoration");
            host.Dispose();
            Check(host.Handle == IntPtr.Zero && !host.TryRefresh(icon, "after exit"), "disposed host cannot recreate icon");
            Check(Shell_NotifyIconGetRect(ref id, out rect) != 0, "dispose removes Shell registration");
            host.Dispose();
        }
        return 0;
    }
}
