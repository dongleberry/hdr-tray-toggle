using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HdrTray;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "HdrTray_SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "HdrTray";

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Icon _iconOn;
    private readonly Icon _iconOff;

    public TrayContext()
    {
        _iconOn = MakeIcon(true);
        _iconOff = MakeIcon(false);

        _toggleItem = new ToolStripMenuItem("Toggle HDR", null, (_, _) => Toggle());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            CheckOnClick = false
        };
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { _toggleItem, new ToolStripSeparator(), _startupItem, exitItem });
        menu.Opening += (_, _) => { RefreshState(); _startupItem.Checked = IsStartupEnabled(); };

        _tray = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Icon = _iconOff,
            Text = "HDR"
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Toggle();
        };

        RefreshState();
    }

    private void Toggle()
    {
        bool? current = Hdr.IsAnyEnabled();
        if (current == null)
        {
            _tray.ShowBalloonTip(2000, "HDR", "No HDR-capable display found.", ToolTipIcon.Warning);
            return;
        }
        Hdr.SetAll(!current.Value);
        Thread.Sleep(300); // give Windows a moment to apply the change
        RefreshState();
    }

    private void RefreshState()
    {
        bool? state = Hdr.IsAnyEnabled();
        if (state == null)
        {
            _tray.Icon = _iconOff;
            _tray.Text = "HDR: not supported";
            _toggleItem.Text = "Toggle HDR (unsupported)";
            _toggleItem.Enabled = false;
            return;
        }
        _toggleItem.Enabled = true;
        _tray.Icon = state.Value ? _iconOn : _iconOff;
        _tray.Text = state.Value ? "HDR: On" : "HDR: Off";
        _toggleItem.Text = state.Value ? "Turn HDR Off" : "Turn HDR On";
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) != null;
    }

    private void ToggleStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (key.GetValue(AppName) != null)
            key.DeleteValue(AppName, false);
        else
            key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
    }

    private static Icon MakeIcon(bool on)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(on ? Color.FromArgb(255, 255, 176, 0) : Color.FromArgb(255, 90, 90, 90));
            using var path = RoundedRect(new Rectangle(1, 5, 30, 22), 5);
            g.FillPath(bg, path);

            using var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(on ? Color.Black : Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("HDR", font, fg, new RectangleF(1, 5, 30, 23), sf);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>
/// HDR ("Advanced Color") control through the Win32 DisplayConfig API.
/// This is the same mechanism the Settings app uses.
/// </summary>
static class Hdr
{
    // ---- Win32 interop ----
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int GET_ADVANCED_COLOR_INFO = 9;
    private const int SET_ADVANCED_COLOR_STATE = 10;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GET_COLOR_INFO
    {
        public HEADER header;
        public uint value;            // bit0 supported, bit1 enabled
        public int colorEncoding;
        public uint bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SET_COLOR_STATE
    {
        public HEADER header;
        public uint enableAdvancedColor; // bit0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public int outputTechnology;
        public int rotation;
        public int scaling;
        public uint refreshNumerator;
        public uint refreshDenominator;
        public int scanLineOrdering;
        public uint targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO
    {
        public SOURCE_INFO sourceInfo;
        public TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths,
        ref uint numModes, [Out] MODE_INFO[] modes, IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetInfo(ref GET_COLOR_INFO info);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    private static extern int SetInfo(ref SET_COLOR_STATE info);

    // ---- Helpers ----
    private static List<(LUID adapter, uint id)> GetActiveTargets()
    {
        var result = new List<(LUID, uint)>();
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0)
            return result;

        var paths = new PATH_INFO[np];
        var modes = new MODE_INFO[nm];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0)
            return result;

        for (int i = 0; i < np; i++)
            result.Add((paths[i].targetInfo.adapterId, paths[i].targetInfo.id));
        return result;
    }

    private static bool TryGetState(LUID adapter, uint id, out bool supported, out bool enabled)
    {
        var info = new GET_COLOR_INFO
        {
            header = new HEADER
            {
                type = GET_ADVANCED_COLOR_INFO,
                size = (uint)Marshal.SizeOf<GET_COLOR_INFO>(),
                adapterId = adapter,
                id = id
            }
        };
        supported = enabled = false;
        if (GetInfo(ref info) != 0) return false;
        supported = (info.value & 0x1) != 0;
        enabled = (info.value & 0x2) != 0;
        return true;
    }

    /// <summary>Returns null if no display supports HDR; otherwise whether any HDR display currently has it on.</summary>
    public static bool? IsAnyEnabled()
    {
        bool anySupported = false, anyEnabled = false;
        foreach (var (adapter, id) in GetActiveTargets())
        {
            if (TryGetState(adapter, id, out bool supported, out bool enabled) && supported)
            {
                anySupported = true;
                anyEnabled |= enabled;
            }
        }
        return anySupported ? anyEnabled : null;
    }

    /// <summary>Turns HDR on or off for every HDR-capable active display.</summary>
    public static void SetAll(bool enable)
    {
        foreach (var (adapter, id) in GetActiveTargets())
        {
            if (!TryGetState(adapter, id, out bool supported, out _) || !supported) continue;

            var set = new SET_COLOR_STATE
            {
                header = new HEADER
                {
                    type = SET_ADVANCED_COLOR_STATE,
                    size = (uint)Marshal.SizeOf<SET_COLOR_STATE>(),
                    adapterId = adapter,
                    id = id
                },
                enableAdvancedColor = enable ? 1u : 0u
            };
            SetInfo(ref set);
        }
    }
}
