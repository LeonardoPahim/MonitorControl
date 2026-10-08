// Monitor Control - small DDC/CI GUI for Windows (brightness, contrast, input, color).
// Uses the built-in Windows monitor API (dxva2.dll); no drivers or runtimes needed.
// Build: build.bat  (uses the C# compiler that ships with .NET Framework 4.x)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Monitor Control")]

namespace MonitorControl
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr type, out uint current, out uint maximum);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint length);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, [Out] byte[] buffer, uint length);
    }

    // One physical monitor. All DDC traffic goes through ddcLock so reads and writes never overlap.
    class Monitor : IDisposable
    {
        public IntPtr Handle;
        public string Name;
        public string Caps = "";
        public Dictionary<int, List<int>> CapsMap = new Dictionary<int, List<int>>();
        public event Action<byte> SetFailed;

        // Shared by all monitors: talking to two displays at once makes some of them drop requests.
        static readonly object ddcLock = new object();
        readonly Dictionary<byte, uint> pending = new Dictionary<byte, uint>();
        bool draining;

        public bool TryGet(byte code, out uint cur, out uint max)
        {
            cur = max = 0;
            lock (ddcLock)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (Handle == IntPtr.Zero) return false;
                    if (Native.GetVCPFeatureAndVCPFeatureReply(Handle, code, IntPtr.Zero, out cur, out max)) return true;
                    Thread.Sleep(40);
                }
            }
            return false;
        }

        bool Set(byte code, uint value)
        {
            lock (ddcLock)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (Handle == IntPtr.Zero) return false;
                    if (Native.SetVCPFeature(Handle, code, value)) return true;
                    Thread.Sleep(40);
                }
            }
            return false;
        }

        // Queues a write; while a slider is being dragged only the latest value per code is sent.
        public void Enqueue(byte code, uint value)
        {
            lock (pending)
            {
                pending[code] = value;
                if (draining) return;
                draining = true;
            }
            ThreadPool.QueueUserWorkItem(delegate { Drain(); });
        }

        void Drain()
        {
            while (true)
            {
                byte code = 0;
                uint value = 0;
                lock (pending)
                {
                    if (pending.Count == 0) { draining = false; return; }
                    foreach (KeyValuePair<byte, uint> e in pending) { code = e.Key; value = e.Value; break; }
                    pending.Remove(code);
                }
                if (!Set(code, value))
                {
                    Action<byte> h = SetFailed;
                    if (h != null) h(code);
                }
            }
        }

        public void LoadCaps()
        {
            lock (ddcLock)
            {
                uint len;
                if (Handle == IntPtr.Zero || !Native.GetCapabilitiesStringLength(Handle, out len) || len == 0) return;
                byte[] buf = new byte[len];
                if (!Native.CapabilitiesRequestAndCapabilitiesReply(Handle, buf, len)) return;
                Caps = Encoding.ASCII.GetString(buf).TrimEnd('\0');
            }
            CapsMap = ParseVcp(Caps);
        }

        // Parses the vcp(...) section of a capabilities string, e.g. "vcp(10 12 14(05 06 08) 60(0F 11 12))".
        static Dictionary<int, List<int>> ParseVcp(string caps)
        {
            Dictionary<int, List<int>> map = new Dictionary<int, List<int>>();
            int i = caps.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return map;
            i += 4;
            int depth = 1, last = -1;
            while (i < caps.Length && depth > 0)
            {
                char c = caps[i];
                if (c == '(') { depth++; i++; continue; }
                if (c == ')') { depth--; i++; continue; }
                if (!Uri.IsHexDigit(c)) { i++; continue; }

                int start = i;
                while (i < caps.Length && Uri.IsHexDigit(caps[i])) i++;
                // Some monitors omit spaces ("vcp(021012...)"), so read the token as 2-digit pairs.
                for (int p = start; p + 1 < i; p += 2)
                {
                    int v = Convert.ToInt32(caps.Substring(p, 2), 16);
                    if (depth == 1)
                    {
                        last = v;
                        if (!map.ContainsKey(v)) map[v] = new List<int>();
                    }
                    else if (depth == 2 && last >= 0 && !map[last].Contains(v))
                    {
                        map[last].Add(v);
                    }
                }
            }
            return map;
        }

        public void Dispose()
        {
            lock (ddcLock)
            {
                if (Handle != IntPtr.Zero) Native.DestroyPhysicalMonitor(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }

    static class MonitorEnum
    {
        static bool warmedUp;

        public static List<Monitor> GetAll()
        {
            // The first physical-monitor handles a process opens can be dead for some displays
            // (seen on the Gigabyte M34WQ); opening and closing one set first avoids that.
            if (!warmedUp)
            {
                warmedUp = true;
                foreach (Monitor m in Enumerate()) m.Dispose();
            }
            return Enumerate();
        }

        static List<Monitor> Enumerate()
        {
            List<Monitor> list = new List<Monitor>();
            Native.MonitorEnumProc proc = delegate(IntPtr h, IntPtr dc, ref Native.RECT r, IntPtr d)
            {
                AddPhysical(h, list);
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);

            // Two identical models get numbered so they can be told apart.
            Dictionary<string, int> seen = new Dictionary<string, int>();
            foreach (Monitor m in list) seen[m.Name] = seen.ContainsKey(m.Name) ? seen[m.Name] + 1 : 1;
            Dictionary<string, int> n = new Dictionary<string, int>();
            foreach (Monitor m in list)
            {
                if (seen[m.Name] < 2) continue;
                string baseName = m.Name;
                n[baseName] = n.ContainsKey(baseName) ? n[baseName] + 1 : 1;
                m.Name = baseName + " (" + n[baseName] + ")";
            }
            return list;
        }

        static void AddPhysical(IntPtr hMonitor, List<Monitor> list)
        {
            Native.MONITORINFOEX info = new Native.MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
            Native.GetMonitorInfo(hMonitor, ref info);

            uint count;
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count) || count == 0) return;
            Native.PHYSICAL_MONITOR[] arr = new Native.PHYSICAL_MONITOR[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, arr)) return;

            List<string> names = DeviceNames(info.szDevice);
            for (int i = 0; i < count; i++)
            {
                string name = i < names.Count ? names[i] : null;
                if (string.IsNullOrEmpty(name)) name = arr[i].szPhysicalMonitorDescription;
                if (string.IsNullOrEmpty(name)) name = "Monitor";
                Monitor m = new Monitor();
                m.Handle = arr[i].hPhysicalMonitor;
                m.Name = name;
                list.Add(m);
            }
        }

        // Friendly model names (e.g. "VG279QM") for the active monitors on a display adapter output.
        static List<string> DeviceNames(string adapter)
        {
            List<string> names = new List<string>();
            Native.DISPLAY_DEVICE dd = new Native.DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(dd);
            for (uint i = 0; Native.EnumDisplayDevices(adapter, i, ref dd, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */); i++)
            {
                if ((dd.StateFlags & 1) != 0) names.Add(EdidName(dd.DeviceID) ?? dd.DeviceString);
                dd = new Native.DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(dd);
            }
            return names;
        }

        // Reads the monitor name descriptor (0xFC) from the EDID Windows stores in the registry.
        static string EdidName(string deviceId)
        {
            try
            {
                // \\?\DISPLAY#GSM5B7F#5&abc&0&UID4353#{guid} -> Enum\DISPLAY\GSM5B7F\5&abc&0&UID4353
                string[] p = (deviceId ?? "").Split('#');
                if (p.Length < 3) return null;
                string path = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + p[1] + @"\" + p[2] + @"\Device Parameters";
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) return null;
                    byte[] edid = k.GetValue("EDID") as byte[];
                    if (edid == null || edid.Length < 128) return null;
                    for (int o = 54; o <= 108; o += 18)
                    {
                        if (edid[o] != 0 || edid[o + 1] != 0 || edid[o + 3] != 0xFC) continue;
                        string s = Encoding.ASCII.GetString(edid, o + 5, 13);
                        int nl = s.IndexOf('\n');
                        if (nl >= 0) s = s.Substring(0, nl);
                        s = s.Trim();
                        if (s.Length > 0) return s;
                    }
                }
            }
            catch { }
            return null;
        }
    }

    class Item
    {
        public int Value;
        public string Text;
        public string Glyph;          // optional icon drawn before the text
        public Color Swatch;          // optional color dot drawn before the text
        public Item(int value, string text) { Value = value; Text = text; }
        public override string ToString() { return Text; }
    }

    static class Vcp
    {
        public const byte Brightness = 0x10, Contrast = 0x12, ColorPreset = 0x14,
            RedGain = 0x16, GreenGain = 0x18, BlueGain = 0x1A, Input = 0x60, PictureMode = 0xDC;

        public static readonly byte[] All = { Brightness, Contrast, Input, ColorPreset, RedGain, GreenGain, BlueGain, PictureMode };

        public static readonly Dictionary<int, string> Inputs = new Dictionary<int, string>
        {
            { 0x01, "VGA 1" }, { 0x02, "VGA 2" }, { 0x03, "DVI 1" }, { 0x04, "DVI 2" },
            { 0x05, "Composite 1" }, { 0x06, "Composite 2" }, { 0x07, "S-Video 1" }, { 0x08, "S-Video 2" },
            { 0x0C, "Component 1" }, { 0x0D, "Component 2" }, { 0x0E, "Component 3" },
            { 0x0F, "DisplayPort 1" }, { 0x10, "DisplayPort 2" }, { 0x11, "HDMI 1" }, { 0x12, "HDMI 2" },
            { 0x1B, "USB-C" },
        };
        public static readonly int[] FallbackInputs = { 0x0F, 0x10, 0x11, 0x12 };

        public static readonly Dictionary<int, string> Presets = new Dictionary<int, string>
        {
            { 0x01, "sRGB" }, { 0x02, "Native" }, { 0x03, "4000K" }, { 0x04, "5000K" }, { 0x05, "6500K" },
            { 0x06, "7500K" }, { 0x07, "8200K" }, { 0x08, "9300K" }, { 0x09, "10000K" }, { 0x0A, "11500K" },
            { 0x0B, "User 1" }, { 0x0C, "User 2" }, { 0x0D, "User 3" },
        };

        // Approximate white points for the color-temperature presets, shown as a dot on each chip.
        public static readonly Dictionary<int, int> PresetSwatches = new Dictionary<int, int>
        {
            { 0x03, 0xFFC98A }, { 0x04, 0xFFE0BD }, { 0x05, 0xFFF6EC }, { 0x06, 0xEEF2FF },
            { 0x07, 0xE0E9FF }, { 0x08, 0xCFDDFF }, { 0x09, 0xC4D5FF }, { 0x0A, 0xB8CCFF },
        };

        public static readonly Dictionary<int, string> Modes = new Dictionary<int, string>
        {
            { 0x00, "Standard" }, { 0x01, "Productivity" }, { 0x02, "Mixed" }, { 0x03, "Movie" },
            { 0x04, "User" }, { 0x05, "Games" }, { 0x06, "Sports" }, { 0x07, "Professional" },
            { 0x08, "Standard (mid power)" }, { 0x09, "Standard (low power)" }, { 0x0A, "Demo" },
            { 0xF0, "Dynamic contrast" },
        };

        public static string Label(byte code)
        {
            switch (code)
            {
                case Brightness: return "Brightness";
                case Contrast: return "Contrast";
                case ColorPreset: return "Color preset";
                case RedGain: return "Red";
                case GreenGain: return "Green";
                case BlueGain: return "Blue";
                case Input: return "Input source";
                case PictureMode: return "Picture mode";
            }
            return "0x" + code.ToString("X2");
        }
    }

    // ---------------------------------------------------------------- look & feel

    static class Ui
    {
        public static float Scale = 1f;
        public static int S(float px) { return (int)Math.Round(px * Scale); }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float radius, Color c)
        {
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(c)) g.DrawPath(pen, p);
        }

        public static void Circle(Graphics g, PointF center, float radius, Color c)
        {
            using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, center.X - radius, center.Y - radius, radius * 2, radius * 2);
        }
    }

    // Follows the Windows light/dark setting and accent color.
    static class Theme
    {
        public static bool Dark;
        public static Color Back, Card, Border, Text, SubText, Track, Accent, AccentSoft, OnAccent, Chip, ChipHover, Thumb, Red, Green, Blue;
        public static Font Body, Small, Strong, Heading, Title, Icons, SmallIcons;

        public const string GlyphBrightness = "\uE706", GlyphInput = "\uE7F4", GlyphColor = "\uE790", GlyphInfo = "\uE946",
            GlyphRefresh = "\uE72C", GlyphMonitor = "\uE7F4", GlyphCheck = "\uE73E";

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static void Load()
        {
            Dark = RegDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
            if (Dark)
            {
                Back = Hex(0x202020); Card = Hex(0x2B2B2B); Border = Hex(0x3A3A3A);
                Text = Hex(0xFFFFFF); SubText = Hex(0xA8A8A8); Track = Hex(0x505050);
                Chip = Hex(0x343434); ChipHover = Hex(0x3F3F3F); Thumb = Hex(0x454545); OnAccent = Hex(0x000000);
                Red = Hex(0xFF6B6B); Green = Hex(0x4CC38A); Blue = Hex(0x5EA2FF);
            }
            else
            {
                Back = Hex(0xF3F3F3); Card = Hex(0xFFFFFF); Border = Hex(0xE5E5E5);
                Text = Hex(0x1B1B1B); SubText = Hex(0x5F5F5F); Track = Hex(0xD4D4D4);
                Chip = Hex(0xF7F7F7); ChipHover = Hex(0xEDEDED); Thumb = Hex(0xFFFFFF); OnAccent = Hex(0xFFFFFF);
                Red = Hex(0xE5484D); Green = Hex(0x2F9E64); Blue = Hex(0x2F7FE0);
            }
            // Windows uses the lighter accent shade on dark backgrounds and the darker one on light backgrounds.
            Color fallback = Dark ? Hex(0x60CDFF) : Hex(0x005FB8);
            Accent = SystemAccent(Dark ? 1 : 4, fallback);
            // A gray/black system accent makes everything look disabled; use Windows' default blue instead.
            if (Accent.GetSaturation() < 0.25f || Math.Abs(Accent.GetBrightness() - 0.5f) > 0.4f) Accent = fallback;
            AccentSoft = Mix(Card, Accent, Dark ? 0.22f : 0.12f);

            Body = MakeFont("Segoe UI Variable Text", 10f, FontStyle.Regular);
            Small = MakeFont("Segoe UI Variable Small", 8.5f, FontStyle.Regular);
            Strong = MakeFont("Segoe UI Semibold", 10f, FontStyle.Bold);
            Heading = MakeFont("Segoe UI Semibold", 11f, FontStyle.Bold);
            Title = MakeFont("Segoe UI Variable Display Semib", 17f, FontStyle.Bold);
            if (Title.Name != "Segoe UI Variable Display Semib") { Title.Dispose(); Title = MakeFont("Segoe UI Semibold", 17f, FontStyle.Bold); }
            string icons = new Font("Segoe Fluent Icons", 12f).Name == "Segoe Fluent Icons" ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            Icons = new Font(icons, 12f);
            SmallIcons = new Font(icons, 10f);
        }

        static Font MakeFont(string family, float size, FontStyle fallbackStyle)
        {
            Font f = new Font(family, size);
            if (f.Name == family) return f;
            f.Dispose();
            return new Font("Segoe UI", size, fallbackStyle);
        }

        static Color Hex(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        static int RegDword(string path, string name, int fallback)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v : fallback;
                }
            }
            catch { return fallback; }
        }

        static Color SystemAccent(int index, Color fallback)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    byte[] p = k == null ? null : k.GetValue("AccentPalette") as byte[];
                    if (p != null && p.Length >= 32) return Color.FromArgb(255, p[index * 4], p[index * 4 + 1], p[index * 4 + 2]);
                }
            }
            catch { }
            return fallback;
        }
    }

    // A self-drawn control that knows how tall it wants to be at a given width.
    abstract class Row : Control
    {
        protected Row()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        public abstract int HeightFor(int width);

        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(BackColor); }
    }

    class SliderRow : Row
    {
        readonly string label;
        readonly Color fill;
        int value, max;
        bool hover, dragging;
        public event Action<int> Changed;

        public SliderRow(string label, int value, int max, Color fill)
        {
            this.label = label;
            this.fill = fill;
            this.max = Math.Max(1, max);
            this.value = Math.Max(0, Math.Min(value, this.max));
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        // Single line: label | track | value pill.
        public override int HeightFor(int width) { return Ui.S(30); }

        int LabelWidth { get { return Ui.S(78); } }
        int PillWidth { get { return Ui.S(38); } }

        // Updates from a re-read of the monitor; doesn't send anything back.
        public void SetSilently(int v, int newMax)
        {
            if (dragging) return;
            max = Math.Max(1, newMax);
            value = Math.Max(0, Math.Min(v, max));
            Invalidate();
        }

        float ThumbRadius { get { return Ui.S(9); } }
        RectangleF Track
        {
            get
            {
                float r = ThumbRadius, th = Ui.S(5);
                float left = LabelWidth + r, right = Width - PillWidth - Ui.S(10) - r;
                return new RectangleF(left, (Height - th) / 2f, Math.Max(1, right - left), th);
            }
        }
        float XFor(int v) { RectangleF t = Track; return t.Left + t.Width * v / max; }

        void SetValue(int v)
        {
            v = Math.Max(0, Math.Min(v, max));
            if (v == value) return;
            value = v;
            Invalidate();
            Action<int> h = Changed;
            if (h != null) h(v);
        }

        void SetFromX(int x)
        {
            RectangleF t = Track;
            SetValue((int)Math.Round((x - t.Left) / t.Width * max));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            TextRenderer.DrawText(g, label, Theme.Body, new Rectangle(0, 0, LabelWidth, Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Value in a small pill tinted with the slider's color.
            string vt = value.ToString();
            int ph = Ui.S(22);
            Rectangle pill = new Rectangle(Width - PillWidth, (Height - ph) / 2, PillWidth, ph);
            Ui.Fill(g, new RectangleF(pill.X, pill.Y, pill.Width, pill.Height), pill.Height / 2f, Theme.Mix(Theme.Card, fill, Theme.Dark ? 0.22f : 0.12f));
            TextRenderer.DrawText(g, vt, Theme.Strong, pill, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            RectangleF t = Track;
            float x = XFor(value);
            Ui.Fill(g, t, t.Height / 2, Theme.Track);
            Ui.Fill(g, new RectangleF(t.Left, t.Top, x - t.Left, t.Height), t.Height / 2, fill);

            PointF c = new PointF(x, t.Top + t.Height / 2);
            Ui.Circle(g, new PointF(c.X, c.Y + 1), ThumbRadius + 1, Color.FromArgb(Theme.Dark ? 90 : 28, 0, 0, 0));
            Ui.Circle(g, c, ThumbRadius, Theme.Border);
            Ui.Circle(g, c, ThumbRadius - 1, Theme.Thumb);
            float inner = dragging ? Ui.S(4) : hover || Focused ? Ui.S(6) : Ui.S(5);
            Ui.Circle(g, c, inner, fill);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || e.X < LabelWidth) return;
            Focus();
            dragging = true;
            SetFromX(e.X);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            SetValue(value + Math.Sign(e.Delta) * Math.Max(1, max / 50));
        }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int big = Math.Max(1, max / 10);
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: SetValue(value - 1); break;
                case Keys.Right: case Keys.Up: SetValue(value + 1); break;
                case Keys.PageDown: SetValue(value - big); break;
                case Keys.PageUp: SetValue(value + big); break;
                case Keys.Home: SetValue(0); break;
                case Keys.End: SetValue(max); break;
            }
        }
    }

    // A wrapping row of pill buttons with one selected.
    class ChipGroup : Row
    {
        public readonly List<Item> Items = new List<Item>();
        public string Label;
        public int Selected = -1;
        public event Action<Item> Picked;
        int hover = -1;

        int Gap { get { return Ui.S(5); } }
        int ChipHeight { get { return Ui.S(28); } }
        int SidePad { get { return Ui.S(11); } }
        // With a label, chips start in a column lined up with the slider tracks.
        int Indent { get { return Label != null ? Ui.S(78) : 0; } }
        int ChipWidth(Item it) { return TextRenderer.MeasureText(it.Text, Theme.Body).Width + 2 * SidePad + LeadWidth(it); }
        int LeadWidth(Item it) { return it.Glyph != null ? Ui.S(22) : !it.Swatch.IsEmpty ? Ui.S(16) : 0; }

        List<Rectangle> Arrange(int width, out int height)
        {
            List<Rectangle> rects = new List<Rectangle>();
            int x = Indent, y = 0;
            foreach (Item it in Items)
            {
                int w = ChipWidth(it);
                if (x > Indent && x + w > width) { x = Indent; y += ChipHeight + Gap; }
                rects.Add(new Rectangle(x, y, w, ChipHeight));
                x += w + Gap;
            }
            height = y + ChipHeight;
            return rects;
        }

        public override int HeightFor(int width) { int h; Arrange(width, out h); return h; }

        public int SingleLineWidth()
        {
            int w = 0;
            foreach (Item it in Items) w += ChipWidth(it) + Gap;
            return Math.Max(0, w - Gap);
        }

        public void SetSilently(int selected) { Selected = selected; Invalidate(); }

        int HitTest(Point p)
        {
            int h;
            List<Rectangle> rects = Arrange(Width, out h);
            for (int i = 0; i < rects.Count; i++) if (rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Label != null)
                TextRenderer.DrawText(g, Label, Theme.Body, new Rectangle(0, 0, Indent, ChipHeight), Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int h;
            List<Rectangle> rects = Arrange(Width, out h);
            for (int i = 0; i < rects.Count; i++)
            {
                Rectangle r = rects[i];
                bool sel = Items[i].Value == Selected;
                RectangleF rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
                float radius = rf.Height / 2;
                if (sel)
                {
                    Ui.Fill(g, rf, radius, i == hover ? Theme.Mix(Theme.Accent, Theme.Card, 0.12f) : Theme.Accent);
                }
                else
                {
                    Ui.Fill(g, rf, radius, i == hover ? Theme.ChipHover : Theme.Chip);
                    Ui.Stroke(g, rf, radius, Theme.Border);
                }
                Item it = Items[i];
                Color fg = sel ? Theme.OnAccent : Theme.Text;
                int lead = LeadWidth(it);
                int x0 = r.X + SidePad;
                if (it.Glyph != null)
                {
                    TextRenderer.DrawText(g, it.Glyph, Theme.SmallIcons, new Rectangle(x0, r.Y, Ui.S(18), r.Height), fg,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                else if (!it.Swatch.IsEmpty)
                {
                    PointF c = new PointF(x0 + Ui.S(5), r.Y + r.Height / 2f);
                    Ui.Circle(g, c, Ui.S(5), sel ? Theme.OnAccent : Theme.Border);
                    Ui.Circle(g, c, Ui.S(5) - 1.5f, it.Swatch);
                }
                TextRenderer.DrawText(g, it.Text, Theme.Body, new Rectangle(x0 + lead, r.Y, r.Width - lead - 2 * SidePad, r.Height), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = HitTest(e.Location);
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
            if (i != hover) { hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1) { hover = -1; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int i = HitTest(e.Location);
            if (i < 0) return;
            if (Items[i].Value == Selected) return;
            Selected = Items[i].Value;
            Invalidate();
            Action<Item> h = Picked;
            if (h != null) h(Items[i]);
        }
    }

    class MessageRow : Row
    {
        readonly string text;
        const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;

        public MessageRow(string text) { this.text = text; }

        public override int HeightFor(int width)
        {
            return TextRenderer.MeasureText(text, Theme.Body, new Size(Math.Max(1, width), 0), Flags).Height + Ui.S(4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, text, Theme.Body, ClientRectangle, Theme.SubText, Flags);
        }
    }

    // A rounded card with an icon + title, stacking its Rows vertically.
    class Card : Control
    {
        readonly string title, glyph;
        int Pad { get { return Ui.S(13); } }
        int TitleHeight { get { return Ui.S(32); } }
        int RowGap { get { return Ui.S(6); } }

        public Card(string title, string glyph)
        {
            this.title = title;
            this.glyph = glyph;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public bool HasRows { get { return Controls.Count > 0; } }

        public int HeightFor(int width)
        {
            int y = Pad + TitleHeight;
            foreach (Row r in Controls) y += r.HeightFor(width - 2 * Pad) + RowGap;
            return y - RowGap + Pad + Ui.S(7);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int w = Width - 2 * Pad;
            int y = Pad + TitleHeight;
            foreach (Row r in Controls)
            {
                int h = r.HeightFor(w);
                r.SetBounds(Pad, y, w, h);
                y += h + RowGap;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Theme.Back); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float radius = Ui.S(10);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - Ui.S(3) - 1.5f);
            // Soft drop shadow: a few offset, faint copies of the card shape.
            for (int i = 3; i >= 1; i--)
                Ui.Fill(g, new RectangleF(r.X, r.Y + i, r.Width, r.Height), radius, Color.FromArgb(Theme.Dark ? 40 : 7 * (4 - i), 0, 0, 0));
            Ui.Fill(g, r, radius, Theme.Card);
            Ui.Stroke(g, r, radius, Theme.Border);

            // Icon in a tinted rounded square, then the title.
            int box = Ui.S(24);
            Rectangle ib = new Rectangle(Pad, Pad - Ui.S(2), box, box);
            Ui.Fill(g, new RectangleF(ib.X, ib.Y, ib.Width, ib.Height), Ui.S(6), Theme.AccentSoft);
            TextRenderer.DrawText(g, glyph, Theme.SmallIcons, ib, Theme.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, title, Theme.Heading, new Rectangle(Pad + box + Ui.S(10), ib.Y, Width, box), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ---------------------------------------------------------------- pages & window

    class MonitorPage : Panel
    {
        public readonly Monitor Monitor;
        readonly MainForm owner;
        readonly Dictionary<byte, SliderRow> sliders = new Dictionary<byte, SliderRow>();
        readonly Dictionary<byte, ChipGroup> chips = new Dictionary<byte, ChipGroup>();
        bool built;

        int Margin_ { get { return Ui.S(12); } }
        int CardGap { get { return Ui.S(8); } }

        public MonitorPage(Monitor monitor, MainForm owner)
        {
            Monitor = monitor;
            this.owner = owner;
            BackColor = Theme.Back;
            AutoScroll = true;
            DoubleBuffered = true;
            ShowMessage("Reading monitor", "Talking to " + monitor.Name + " over DDC/CI...");
        }

        public int ContentHeight(int width)
        {
            int y = Ui.S(4);
            foreach (Card c in Controls) y += c.HeightFor(width - 2 * Margin_) + CardGap;
            return y - CardGap + Margin_;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            // Leave room for the vertical scrollbar up front so a horizontal one never appears.
            int w = Width - 2 * Margin_;
            if (ContentHeight(Width) > Height) w -= SystemInformation.VerticalScrollBarWidth;
            if (w <= 0) return;
            int y = Ui.S(4);
            foreach (Card c in Controls)
            {
                int h = c.HeightFor(w);
                c.SetBounds(Margin_, y + AutoScrollPosition.Y, w, h);
                y += h + CardGap;
            }
            Size min = new Size(0, y - CardGap + Margin_);
            if (AutoScrollMinSize != min) AutoScrollMinSize = min;
        }

        void ClearCards()
        {
            while (Controls.Count > 0) Controls[0].Dispose();
            sliders.Clear();
            chips.Clear();
            built = false;
        }

        public void ShowMessage(string title, string text)
        {
            ClearCards();
            Card c = new Card(title, Theme.GlyphInfo);
            c.Controls.Add(new MessageRow(text));
            Controls.Add(c);
            PerformLayout();
        }

        public void Populate(Dictionary<byte, uint[]> vals, bool update)
        {
            if (update && built) { UpdateValues(vals); return; }

            ClearCards();
            Card display = new Card("Display", Theme.GlyphBrightness);
            AddSlider(display, vals, Vcp.Brightness, Theme.Accent);
            AddSlider(display, vals, Vcp.Contrast, Theme.Accent);

            Card input = new Card("Input source", Theme.GlyphInput);
            AddChips(input, vals, Vcp.Input, null, Vcp.Inputs, "Input", Vcp.FallbackInputs, false);

            Card color = new Card("Color", Theme.GlyphColor);
            AddChips(color, vals, Vcp.ColorPreset, "Preset", Vcp.Presets, "Preset", null, true);
            AddChips(color, vals, Vcp.PictureMode, "Mode", Vcp.Modes, "Mode", null, true);
            AddSlider(color, vals, Vcp.RedGain, Theme.Red);
            AddSlider(color, vals, Vcp.GreenGain, Theme.Green);
            AddSlider(color, vals, Vcp.BlueGain, Theme.Blue);

            foreach (Card c in new Card[] { display, input, color })
            {
                if (c.HasRows) Controls.Add(c);
                else c.Dispose();
            }

            if (Controls.Count == 0)
            {
                ShowMessage("No response",
                    "This monitor didn't answer over DDC/CI. Make sure DDC/CI is enabled in its on-screen menu, then press Refresh.");
            }
            else
            {
                built = true;
                PerformLayout();
            }
            owner.PageReady(this);
        }

        void UpdateValues(Dictionary<byte, uint[]> vals)
        {
            uint[] v;
            foreach (KeyValuePair<byte, SliderRow> s in sliders)
                if (vals.TryGetValue(s.Key, out v)) s.Value.SetSilently((int)v[0], (int)Math.Max(v[0], v[1]));
            foreach (KeyValuePair<byte, ChipGroup> c in chips)
                if (vals.TryGetValue(c.Key, out v)) c.Value.SetSilently((int)(v[0] & 0xFF));
        }

        void AddSlider(Card card, Dictionary<byte, uint[]> vals, byte code, Color fill)
        {
            uint[] v;
            if (!vals.TryGetValue(code, out v) || v[1] == 0) return;
            // Some monitors (e.g. Gigabyte gains) report a current value above their stated maximum.
            int max = (int)Math.Min(Math.Max(v[0], v[1]), 10000);
            SliderRow s = new SliderRow(Vcp.Label(code), (int)v[0], max, fill);
            s.Changed += delegate(int value) { Monitor.Enqueue(code, (uint)value); };
            card.Controls.Add(s);
            sliders[code] = s;
        }

        // True when every picture-mode value is from the MCCS standard list; otherwise the vendor uses its own codes.
        static bool IsStandardModeList(List<int> options)
        {
            foreach (int o in options) if (o > 0x0A && o != 0xF0) return false;
            return true;
        }

        void AddChips(Card card, Dictionary<byte, uint[]> vals, byte code, string label,
                      Dictionary<int, string> names, string unknownPrefix, int[] fallback, bool reloadAfter)
        {
            uint[] v;
            bool readable = vals.TryGetValue(code, out v);
            List<int> listed;
            bool inCaps = Monitor.CapsMap.TryGetValue(code, out listed) && listed.Count > 0;
            if (!readable && !inCaps) return;

            List<int> options = inCaps ? new List<int>(listed) : new List<int>(fallback ?? new int[0]);
            int cur = readable ? (int)(v[0] & 0xFF) : -1;
            if (cur >= 0 && !options.Contains(cur)) options.Insert(0, cur);
            if (options.Count == 0) return;

            ChipGroup g = new ChipGroup();
            g.Label = label;
            g.Selected = cur;
            int unknown = 0;
            foreach (int o in options)
            {
                string n;
                // Vendor-specific picture modes get numbered (Mode 1, Mode 2...) rather than shown as hex codes.
                if (!names.TryGetValue(o, out n) || (code == Vcp.PictureMode && options.Count > 1 && !IsStandardModeList(options)))
                    n = code == Vcp.PictureMode ? unknownPrefix + " " + (++unknown) : unknownPrefix + " " + o.ToString("X2");
                Item item = new Item(o, n);
                int sw;
                if (code == Vcp.ColorPreset && Vcp.PresetSwatches.TryGetValue(o, out sw))
                    item.Swatch = Color.FromArgb(255, (sw >> 16) & 255, (sw >> 8) & 255, sw & 255);
                g.Items.Add(item);
            }
            g.Picked += delegate(Item it)
            {
                Monitor.Enqueue(code, (uint)it.Value);
                // Presets/modes change the gains and brightness, so re-read them afterwards.
                if (reloadAfter) owner.ScheduleReload(this);
            };
            card.Controls.Add(g);
            chips[code] = g;
        }
    }

    // Round, borderless icon button (used for Refresh in the header).
    class IconButton : Control
    {
        readonly string glyph;
        bool hover, down;

        public IconButton(string glyph)
        {
            this.glyph = glyph;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Size = new Size(Ui.S(28), Ui.S(28));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hover || down)
            {
                Color c = down ? Theme.Mix(Theme.ChipHover, Theme.Text, 0.08f) : Theme.ChipHover;
                Ui.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Width / 2f, c);
            }
            TextRenderer.DrawText(g, glyph, Theme.Icons, ClientRectangle, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
    }

    class MainForm : Form
    {
        readonly ChipGroup selector;
        readonly IconButton refresh;
        readonly Label status;
        readonly ToolTip tips = new ToolTip();
        readonly List<MonitorPage> pages = new List<MonitorPage>();
        List<Monitor> monitors = new List<Monitor>();
        bool autoRetried;
        public static int InitialPage;

        int Pad { get { return Ui.S(12); } }
        int FooterHeight { get { return Ui.S(status != null && status.Text.Length > 0 ? 22 : 4); } }

        public MainForm()
        {
            Text = "Monitor Control";
            Font = Theme.Body;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            ClientSize = new Size(Ui.S(420), Ui.S(480));
            MinimumSize = new Size(Ui.S(360), Ui.S(220));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            selector = new ChipGroup();
            selector.BackColor = Theme.Back;
            selector.Picked += delegate(Item it) { ShowPage(it.Value); };

            refresh = new IconButton(Theme.GlyphRefresh);
            refresh.Click += delegate { ReloadAll(); };
            tips.SetToolTip(refresh, "Refresh monitors");

            status = new Label();
            status.BackColor = Theme.Back;
            status.ForeColor = Theme.SubText;
            status.Font = Theme.Small;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;

            Controls.Add(selector);
            Controls.Add(refresh);
            Controls.Add(status);

            Shown += delegate { ReloadAll(); };
            FormClosed += delegate { ReleaseAll(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Match the title bar to the window: dark mode flag, then caption + text colors (Windows 11).
            int dark = Theme.Dark ? 1 : 0;
            int caption = ColorTranslator.ToWin32(Theme.Back);
            int text = ColorTranslator.ToWin32(Theme.Text);
            try
            {
                Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                Native.DwmSetWindowAttribute(Handle, 35, ref caption, 4);
                Native.DwmSetWindowAttribute(Handle, 36, ref text, 4);
            }
            catch { }
        }

        // Monitor switcher and refresh button share the top row.
        int SelectorTop { get { return Ui.S(10); } }
        int SelectorWidth { get { return ClientSize.Width - 2 * Pad - refresh.Width - Ui.S(6); } }
        int PagesTop { get { return SelectorTop + selector.HeightFor(SelectorWidth) + Ui.S(10); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (selector == null) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            refresh.Location = new Point(w - Pad - refresh.Width, SelectorTop);
            selector.SetBounds(Pad, SelectorTop, SelectorWidth, selector.HeightFor(SelectorWidth));

            int top = PagesTop;
            foreach (MonitorPage p in pages) p.SetBounds(0, top, w, Math.Max(0, h - FooterHeight - top));
            status.SetBounds(Pad + Ui.S(2), h - FooterHeight, Math.Max(0, w - 2 * Pad), FooterHeight - Ui.S(4));
        }

        void ReleaseAll()
        {
            foreach (Monitor m in monitors) m.Dispose();
            monitors.Clear();
        }

        void ReloadAll()
        {
            // Stay on the same monitor across refreshes (InitialPage carries --page on first load).
            int keep = pages.Count > 0 ? Math.Max(0, selector.Selected) : InitialPage;
            InitialPage = keep;
            ReleaseAll();
            foreach (MonitorPage p in pages) p.Dispose();
            pages.Clear();
            selector.Items.Clear();
            status.Text = "";

            monitors = MonitorEnum.GetAll();
            if (monitors.Count == 0) status.Text = "No monitors found.";

            for (int i = 0; i < monitors.Count; i++)
            {
                Monitor mon = monitors[i];
                mon.SetFailed += delegate(byte code) { SetStatus(mon.Name + " didn't accept the " + Vcp.Label(code).ToLower() + " change."); };
                MonitorPage page = new MonitorPage(mon, this);
                page.Visible = false;
                pages.Add(page);
                Controls.Add(page);
                Item tab = new Item(i, mon.Name);
                tab.Glyph = Theme.GlyphMonitor;
                selector.Items.Add(tab);
                LoadAsync(page, true);
            }
            ShowPage(pages.Count > InitialPage ? InitialPage : 0);
            InitialPage = keep;
            PerformLayout();
        }

        void ShowPage(int index)
        {
            if (index < 0 || index >= pages.Count) return;
            selector.SetSilently(index);
            for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == index;
            FitHeight(pages[index]);
        }

        public void PageReady(MonitorPage page)
        {
            if (page.Visible) FitHeight(page);
        }

        // Sizes the window to the page so there's no scrolling or empty space (capped to the screen).
        void FitHeight(MonitorPage page)
        {
            if (WindowState != FormWindowState.Normal) return;
            int want = Math.Max(PagesTop + page.ContentHeight(ClientSize.Width) + FooterHeight, Ui.S(200));
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int cap = (int)(wa.Height * 0.9) - (Height - ClientSize.Height);
            ClientSize = new Size(ClientSize.Width, Math.Min(want, cap));
            if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
        }

        void SetStatus(string text)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    status.Text = text;
                    foreach (MonitorPage p in pages) if (p.Visible) FitHeight(p);
                    PerformLayout();
                });
            }
            catch (InvalidOperationException) { }
        }

        void LoadAsync(MonitorPage page, bool readCaps)
        {
            Monitor m = page.Monitor;
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (readCaps) m.LoadCaps();
                Dictionary<byte, uint[]> vals = new Dictionary<byte, uint[]>();
                foreach (byte code in Vcp.All)
                {
                    // Picture mode is vendor-specific; only query it when the monitor advertises it.
                    if (code == Vcp.PictureMode && !m.CapsMap.ContainsKey(code)) continue;
                    uint cur, max;
                    if (m.TryGet(code, out cur, out max)) vals[code] = new uint[] { cur, max };
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (page.IsDisposed) return;
                        // A monitor that doesn't answer the first time usually does after reopening its handle.
                        if (readCaps && vals.Count == 0 && !autoRetried)
                        {
                            autoRetried = true;
                            page.ShowMessage("Reading monitor", "No answer yet from " + m.Name + ", retrying...");
                            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                            t.Interval = 1000;
                            t.Tick += delegate { t.Stop(); t.Dispose(); ReloadAll(); };
                            t.Start();
                            return;
                        }
                        page.Populate(vals, !readCaps);
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        public void ScheduleReload(MonitorPage page)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 800;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                if (!page.IsDisposed) LoadAsync(page, false);
            };
            t.Start();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--dump")
            {
                Dump();
                return;
            }
            // --page N opens on the Nth monitor (0-based).
            if (args.Length > 1 && args[0] == "--page") int.TryParse(args[1], out MainForm.InitialPage);
            try { Native.SetProcessDPIAware(); } catch { }
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Scale = g.DpiX / 96f;
            Theme.Load();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        // Prints what each monitor reports; handy for troubleshooting (MonitorControl.exe --dump | more).
        static void Dump()
        {
            foreach (Monitor m in MonitorEnum.GetAll())
            {
                m.LoadCaps();
                Console.WriteLine(m.Name);
                Console.WriteLine("  caps: " + (m.Caps.Length > 0 ? m.Caps : "(none)"));
                foreach (byte code in Vcp.All)
                {
                    uint cur, max;
                    Console.WriteLine("  " + Vcp.Label(code) + ": " + (m.TryGet(code, out cur, out max) ? cur + " / " + max : "n/a"));
                }
                m.Dispose();
            }
        }
    }
}
