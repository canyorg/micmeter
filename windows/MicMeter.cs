// MicMeter - Mikrofon seviye göstergesi (Windows)
// Sağ altta, her zaman en üstte duran küçük pencere. Ses arttıkça parlaklık artar.
// Ek bağımlılık yok: Windows'un kendi winmm.dll (waveIn) API'si kullanılır.
// Derleme: build.bat (Windows'ta hazır gelen .NET Framework csc.exe ile)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MicMeter
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new System.Threading.Mutex(true, "MicMeter_SingleInstance", out created))
            {
                if (!created) return; // zaten açık
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MeterForm());
            }
        }
    }

    // ---------------- Mikrofon okuma (winmm waveIn, polling) ----------------
    class WaveIn
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag; public ushort nChannels; public uint nSamplesPerSec;
            public uint nAvgBytesPerSec; public ushort nBlockAlign; public ushort wBitsPerSample; public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData; public uint dwBufferLength; public uint dwBytesRecorded; public IntPtr dwUser;
            public uint dwFlags; public uint dwLoops; public IntPtr lpNext; public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEINCAPS
        {
            public ushort wMid; public ushort wPid; public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats; public ushort wChannels; public ushort wReserved1;
        }

        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, int deviceId, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW", CharSet = CharSet.Unicode)]
        static extern int waveInGetDevCaps(IntPtr id, ref WAVEINCAPS caps, int size);

        const int WHDR_DONE = 1;
        const int SampleRate = 16000;
        const int BufSamples = 640;   // 40 ms
        const int NumBufs = 4;

        IntPtr handle = IntPtr.Zero;
        readonly List<IntPtr> headers = new List<IntPtr>();
        readonly int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        readonly int offBytes = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwBytesRecorded");
        readonly int offFlags = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
        readonly short[] tmp = new short[BufSamples];

        public bool IsOpen { get { return handle != IntPtr.Zero; } }

        public static List<string> ListDevices()
        {
            var list = new List<string>();
            int n = waveInGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var caps = new WAVEINCAPS();
                if (waveInGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(typeof(WAVEINCAPS))) == 0)
                    list.Add(caps.szPname);
                else
                    list.Add("Mikrofon " + (i + 1));
            }
            return list;
        }

        // deviceId = -1 => Windows varsayılan mikrofonu
        public bool Open(int deviceId)
        {
            Close();
            var f = new WAVEFORMATEX();
            f.wFormatTag = 1; f.nChannels = 1; f.nSamplesPerSec = SampleRate; f.wBitsPerSample = 16;
            f.nBlockAlign = 2; f.nAvgBytesPerSec = SampleRate * 2; f.cbSize = 0;

            if (waveInOpen(out handle, deviceId, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0)
            {
                handle = IntPtr.Zero;
                return false;
            }
            for (int i = 0; i < NumBufs; i++)
            {
                IntPtr data = Marshal.AllocHGlobal(BufSamples * 2);
                IntPtr hdr = Marshal.AllocHGlobal(hdrSize);
                var w = new WAVEHDR();
                w.lpData = data; w.dwBufferLength = (uint)(BufSamples * 2);
                Marshal.StructureToPtr(w, hdr, false);
                waveInPrepareHeader(handle, hdr, hdrSize);
                waveInAddBuffer(handle, hdr, hdrSize);
                headers.Add(hdr);
            }
            waveInStart(handle);
            return true;
        }

        // Biten tamponların en yüksek RMS değeri (0..1). Yeni veri yoksa -1.
        public float Poll()
        {
            if (handle == IntPtr.Zero) return -1;
            float best = -1;
            foreach (IntPtr hdr in headers)
            {
                int flags = Marshal.ReadInt32(hdr, offFlags);
                if ((flags & WHDR_DONE) == 0) continue;

                int n = Math.Min(Marshal.ReadInt32(hdr, offBytes) / 2, BufSamples);
                if (n > 0)
                {
                    Marshal.Copy(Marshal.ReadIntPtr(hdr, 0), tmp, 0, n);
                    double sum = 0;
                    for (int i = 0; i < n; i++) { double s = tmp[i] / 32768.0; sum += s * s; }
                    float rms = (float)Math.Sqrt(sum / n);
                    if (rms > best) best = rms;
                }
                else if (best < 0) best = 0;

                Marshal.WriteInt32(hdr, offFlags, flags & ~WHDR_DONE);
                Marshal.WriteInt32(hdr, offBytes, 0);
                waveInAddBuffer(handle, hdr, hdrSize);
            }
            return best;
        }

        public void Close()
        {
            if (handle != IntPtr.Zero)
            {
                waveInReset(handle);
                foreach (IntPtr hdr in headers) waveInUnprepareHeader(handle, hdr, hdrSize);
                waveInClose(handle);
                handle = IntPtr.Zero;
            }
            foreach (IntPtr hdr in headers)
            {
                Marshal.FreeHGlobal(Marshal.ReadIntPtr(hdr, 0));
                Marshal.FreeHGlobal(hdr);
            }
            headers.Clear();
        }
    }

    // ---------------- Sistem mikrofonu sessize alma (Core Audio) ----------------
    static class SysMic
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                       [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr p);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
            [PreserveSig] int GetChannelCount(out uint c);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float l, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float l);
            [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float l, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float l);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        const int eCapture = 1, eConsole = 0, eCommunications = 2, CLSCTX_ALL = 23;

        static IAudioEndpointVolume Get(int role)
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(eCapture, role, out dev) != 0 || dev == null) return null;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0) return null;
                return o as IAudioEndpointVolume;
            }
            catch { return null; }
        }

        // null = okunamadı
        public static bool? IsMuted()
        {
            IAudioEndpointVolume v = Get(eConsole);
            if (v == null) return null;
            bool m;
            return v.GetMute(out m) == 0 ? (bool?)m : null;
        }

        // Varsayılan ve iletişim (Meet/Teams) mikrofonunu birlikte ayarlar
        public static bool SetMuted(bool mute)
        {
            bool ok = false;
            Guid ctx = Guid.Empty;
            foreach (int role in new[] { eConsole, eCommunications })
            {
                IAudioEndpointVolume v = Get(role);
                if (v != null && v.SetMute(mute, ref ctx) == 0) ok = true;
            }
            return ok;
        }
    }

    // ---------------- Gösterge penceresi ----------------
    class MeterForm : Form
    {
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

        const float MinDb = -60f;   // bu seviyenin altı "sessiz" (gösterge boş)
        const int Segs = 22;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly WaveIn wave = new WaveIn();
        readonly Timer timer = new Timer();
        readonly Timer topTimer = new Timer();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem devicesItem, startupItem;

        float scale = 1f;
        float level = 0f, peak = 0f, shownDb = -90f;
        int peakHold = 0, noDataTicks = 0;
        int deviceId = -1;
        bool muted = false, pressOnIcon = false;
        int muteCheckTicks = 0;
        ToolStripMenuItem muteItem;
        int sizeMode = 0; // 0 = Mini, 1 = Küçük, 2 = Normal
        static readonly string[] SizeNames = { "Mini", "Küçük", "Normal" };
        static readonly int[,] SizeDims = { { 124, 14 }, { 176, 22 }, { 270, 44 } };
        const float NumGain = 2000f; // 1-1000 ölçeği: RMS 0.0005 (çok hafif ses) = 1, RMS 0.5 = 1000
        int num = 0, numTicks = 0;
        float numPeak = 0f;
        bool hasHome = false;
        Point home;
        float Rad { get { return sizeMode == 0 ? S(7) : sizeMode == 1 ? S(9) : S(12); } }
        bool dragging, userMoved;
        Point dragOffset;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x8 | 0x08000000; // TOOLWINDOW | TOPMOST | NOACTIVATE
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public MeterForm()
        {
            Text = "MicMeter";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(16, 16, 20);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            sizeMode = LoadSizeMode();
            ApplySize();

            BuildMenu();
            ContextMenuStrip = menu;
            tray.Icon = SystemIcons.Application;
            tray.Text = "MicMeter - mikrofon seviyesi";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ResetPosition(); };

            LoadHome();
            PlaceHome();
            wave.Open(deviceId);

            timer.Interval = 25;
            timer.Tick += OnTick;
            timer.Start();

            // Görev çubuğu vb. öne geçerse tekrar en üste al
            topTimer.Interval = 1500;
            topTimer.Tick += delegate { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); };
            topTimer.Start();

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        void OnDisplayChanged(object s, EventArgs e) { if (!userMoved) PlaceHome(); }

        void PlaceBottomRight()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea; // görev çubuğunun hemen üstü
            Location = new Point(wa.Right - Width - S(12), wa.Bottom - Height - S(12));
        }

        void ResetPosition() { userMoved = false; PlaceHome(); }

        // Kayıtlı konum varsa oraya, yoksa sağ alta
        void PlaceHome()
        {
            if (hasHome)
            {
                foreach (Screen sc in Screen.AllScreens)
                    if (sc.Bounds.Contains(home)) { Location = home; return; }
            }
            PlaceBottomRight();
        }

        void SaveHome()
        {
            home = Location; hasHome = true; userMoved = false;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\MicMeter"))
                {
                    k.SetValue("HomeX", home.X, RegistryValueKind.DWord);
                    k.SetValue("HomeY", home.Y, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        void LoadHome()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\MicMeter"))
                {
                    if (k == null) return;
                    object x = k.GetValue("HomeX"), y = k.GetValue("HomeY");
                    if (x is int && y is int) { home = new Point((int)x, (int)y); hasHome = true; }
                }
            }
            catch { }
        }

        void ApplyRoundRegion()
        {
            using (GraphicsPath p = RoundRect(new RectangleF(0, 0, Width, Height), Rad))
                Region = new Region(p);
        }

        void ApplySize()
        {
            Size = new Size(S(SizeDims[sizeMode, 0]), S(SizeDims[sizeMode, 1]));
            ApplyRoundRegion();
            if (!userMoved) PlaceHome();
            Invalidate();
        }

        int LoadSizeMode()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\MicMeter"))
                {
                    object v = k == null ? null : k.GetValue("Size");
                    int m = v is int ? (int)v : 0;
                    return m >= 0 && m <= 2 ? m : 0;
                }
            }
            catch { return 0; }
        }

        void SaveSizeMode()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\MicMeter"))
                    k.SetValue("Size", sizeMode, RegistryValueKind.DWord);
            }
            catch { }
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            float d = rad * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------- Menü ----------
        void BuildMenu()
        {
            devicesItem = new ToolStripMenuItem("Mikrofon seç");
            devicesItem.DropDownItems.Add("..."); // açılınca doldurulur
            devicesItem.DropDownOpening += delegate { FillDevices(); };

            startupItem = new ToolStripMenuItem("Windows ile başlat");
            startupItem.Checked = IsStartupEnabled();
            startupItem.Click += delegate { SetStartup(!startupItem.Checked); startupItem.Checked = IsStartupEnabled(); };

            var saveHome = new ToolStripMenuItem("Bu konumu kaydet (çift tık buraya döner)");
            saveHome.Click += delegate { SaveHome(); };
            menu.Items.Add(saveHome);
            var reset = new ToolStripMenuItem("Sağ alta taşı");
            reset.Click += delegate { userMoved = true; PlaceBottomRight(); };

            var exit = new ToolStripMenuItem("Çıkış");
            exit.Click += delegate { Close(); };

            menu.Items.Add(devicesItem);
            var sizeItem = new ToolStripMenuItem("Boyut");
            for (int i = 0; i < 3; i++)
            {
                int m = i;
                var it = new ToolStripMenuItem(SizeNames[i]);
                it.Click += delegate { sizeMode = m; SaveSizeMode(); ApplySize(); };
                sizeItem.DropDownItems.Add(it);
            }
            sizeItem.DropDownOpening += delegate
            {
                for (int i = 0; i < 3; i++) ((ToolStripMenuItem)sizeItem.DropDownItems[i]).Checked = i == sizeMode;
            };
            muteItem = new ToolStripMenuItem("Mikrofonu kapat");
            muteItem.Click += delegate { ToggleMute(); };
            menu.Items.Add(muteItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(sizeItem);
            menu.Items.Add(reset);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);
        }

        void FillDevices()
        {
            devicesItem.DropDownItems.Clear();
            var def = new ToolStripMenuItem("Windows varsayılanı");
            def.Checked = deviceId == -1;
            def.Click += delegate { SwitchDevice(-1); };
            devicesItem.DropDownItems.Add(def);

            List<string> names = WaveIn.ListDevices();
            for (int i = 0; i < names.Count; i++)
            {
                int id = i;
                var it = new ToolStripMenuItem(names[i]);
                it.Checked = deviceId == id;
                it.Click += delegate { SwitchDevice(id); };
                devicesItem.DropDownItems.Add(it);
            }
        }

        void SwitchDevice(int id)
        {
            deviceId = id;
            level = 0; peak = 0; shownDb = -90;
            wave.Open(deviceId);
        }

        bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("MicMeter") != null;
            }
            catch { return false; }
        }

        void SetStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("MicMeter", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("MicMeter", false);
                }
            }
            catch { }
        }

        // ---------- Mikrofon simgesi / sessize alma ----------
        float IconR() { return sizeMode == 0 ? S(3.5f) : sizeMode == 1 ? S(6.5f) : S(13); }
        float IconCx() { return sizeMode == 0 ? S(8) : sizeMode == 1 ? S(12) : S(22); }

        bool IconHit(Point p)
        {
            float dx = p.X - IconCx(), dy = p.Y - Height / 2f;
            float hit = IconR() + S(sizeMode == 0 ? 6 : 4); // Mini'de tıklama alanı biraz geniş
            return dx * dx + dy * dy <= hit * hit;
        }

        void ToggleMute()
        {
            bool target = !muted;
            if (SysMic.SetMuted(target)) muted = target;
            else
            {
                bool? m = SysMic.IsMuted();
                if (m.HasValue) muted = m.Value;
            }
            UpdateMuteMenu();
            Invalidate();
        }

        void UpdateMuteMenu()
        {
            if (muteItem != null) muteItem.Text = muted ? "Mikrofonu aç" : "Mikrofonu kapat";
        }

        // ---------- Sürükleme ----------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (IconHit(e.Location)) pressOnIcon = true;
                else { dragging = true; dragOffset = e.Location; }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                Point c = Cursor.Position;
                Location = new Point(c.X - dragOffset.X, c.Y - dragOffset.Y);
                userMoved = true;
            }
            else Cursor = IconHit(e.Location) ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (pressOnIcon && e.Button == MouseButtons.Left && IconHit(e.Location)) ToggleMute();
            pressOnIcon = false;
            dragging = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (!IconHit(e.Location)) ResetPosition();
            base.OnMouseDoubleClick(e);
        }

        // ---------- Ölçüm ----------
        void OnTick(object sender, EventArgs e)
        {
            float rms = wave.Poll();
            if (rms >= 0)
            {
                noDataTicks = 0;
                float db = rms > 0 ? (float)(20 * Math.Log10(rms)) : -90f;
                if (db < -90) db = -90;
                float target = Clamp01((db - MinDb) / (0 - MinDb));

                level += (target - level) * (target > level ? 0.65f : 0.12f); // hızlı yükselir, yavaş düşer
                shownDb += (db - shownDb) * 0.25f;
                if (rms > numPeak) numPeak = rms;
            }
            else
            {
                noDataTicks++;
                if (noDataTicks > 20) { level *= 0.85f; shownDb = -90; }
                if (noDataTicks == 120 && !wave.IsOpen) wave.Open(deviceId); // mikrofon sonradan takıldıysa
                if (noDataTicks > 120) noDataTicks = 21;
            }

            if (++numTicks >= 4) // sayı 10 kez/sn güncellenir (okunabilsin diye)
            {
                numTicks = 0;
                num = muted ? 0 : (int)Math.Min(1000, Math.Round(numPeak * NumGain));
                numPeak = 0f;
            }

            if (++muteCheckTicks >= 20) // ~0.5 sn
            {
                muteCheckTicks = 0;
                bool? m = SysMic.IsMuted();
                if (m.HasValue && m.Value != muted) { muted = m.Value; UpdateMuteMenu(); }
            }

            if (level >= peak) { peak = level; peakHold = 35; }
            else if (peakHold > 0) peakHold--;
            else peak = Math.Max(level, peak - 0.012f);

            Invalidate();
        }

        static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        static Color Mix(Color a, Color b, float t)
        {
            t = Clamp01(t);
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        static Color SegColor(float pos)
        {
            if (pos < 0.65f) return Color.FromArgb(40, 230, 120);   // yeşil
            if (pos < 0.85f) return Color.FromArgb(255, 200, 40);   // sarı
            return Color.FromArgb(255, 70, 60);                     // kırmızı (çok yüksek)
        }

        // ---------- Çizim ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color bg = Color.FromArgb(16, 16, 20);
            Color accent = Color.FromArgb(60, 235, 140);
            bool noMic = !wave.IsOpen;

            // Arka plan: ses arttıkça hafifçe aydınlanır
            using (var b = new SolidBrush(Mix(bg, Color.FromArgb(22, 60, 40), level)))
                g.FillRectangle(b, ClientRectangle);

            // Kenar çizgisi: ses arttıkça parlar
            using (GraphicsPath border = RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Rad))
            using (var pen = new Pen(muted ? Color.FromArgb(200, 230, 60, 60) : Color.FromArgb((int)(50 + 205 * level), accent), Math.Max(1f, (sizeMode == 0 ? 1f : 1.5f) * scale)))
                g.DrawPath(pen, border);

            // Sol: parlayan daire + mikrofon simgesi
            bool showStatus = sizeMode == 2, showGlyph = sizeMode >= 1;
            float cy = Height / 2f;
            float r = IconR();
            float cx = IconCx();
            float glowR = r * (1f + 0.45f * level); float pad = r * 0.3f;
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(cx - glowR - pad, cy - glowR - pad, (glowR + pad) * 2, (glowR + pad) * 2);
                using (var pgb = new PathGradientBrush(gp))
                {
                    pgb.CenterColor = Color.FromArgb((int)(170 * level), accent);
                    pgb.SurroundColors = new[] { Color.FromArgb(0, accent) };
                    g.FillPath(pgb, gp);
                }
            }
            Color dot = muted ? Color.FromArgb(215, 50, 50)
                      : noMic ? Color.FromArgb(90, 40, 40)
                      : Mix(Color.FromArgb(35, 55, 45), accent, 0.15f + 0.85f * level);
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            if (showGlyph) DrawMicGlyph(g, cx, cy, r, muted ? Color.White : level > 0.5f ? Color.FromArgb(10, 30, 18) : Color.FromArgb(220, 240, 230));
            if (muted && showGlyph) // üstü çizili mikrofon
            {
                using (var pen = new Pen(Color.White, Math.Max(1.5f, r * 0.16f)))
                {
                    float k = r * 0.62f;
                    g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
                }
            }

            // Orta: segmentli seviye çubuğu
            float x0 = cx + r + S(sizeMode == 2 ? 9 : 4);
            float x1 = Width - (sizeMode == 0 ? S(32) : sizeMode == 1 ? S(40) : S(74));
            float barH = sizeMode == 0 ? S(6) : sizeMode == 1 ? S(9) : S(14), y0 = cy - barH / 2;
            float gap = Math.Max(1f, (sizeMode == 2 ? 2f : 1f) * scale);
            float segW = ((x1 - x0) - gap * (Segs - 1)) / Segs;
            float bright = 0.45f + 0.55f * level; // genel parlaklık ses arttıkça artar

            for (int i = 0; i < Segs; i++)
            {
                float pos = (i + 1) / (float)Segs;
                float lit = Clamp01((level - i / (float)Segs) * Segs); // kısmi doluluk
                Color on = Mix(bg, SegColor(pos), bright);
                Color off = Mix(bg, SegColor(pos), 0.13f);
                using (var b = new SolidBrush(Mix(off, on, lit)))
                    g.FillRectangle(b, x0 + i * (segW + gap), y0, segW, barH);
            }

            // Tepe noktası çizgisi
            if (peak > 0.02f)
            {
                float px = x0 + (x1 - x0) * peak;
                using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1f, (sizeMode == 0 ? 1f : 2f) * scale)))
                    g.DrawLine(pen, px, y0 - S(sizeMode == 0 ? 1 : 3), px, y0 + barH + S(sizeMode == 0 ? 1 : 3));
            }

            // Sağ: dB değeri ve durum
            // Sondaki 1-1000 sayacı (+ Normal'de dB / durum)
            string numText, status;
            Color statusColor;
            if (muted) { numText = "0"; status = "Kapalı"; statusColor = Color.FromArgb(255, 90, 80); }
            else if (noMic) { numText = "--"; status = "Mik. yok"; statusColor = Color.FromArgb(255, 110, 100); }
            else
            {
                numText = num.ToString();
                if (shownDb < -50) { status = "Sessiz"; statusColor = Color.FromArgb(130, 130, 140); }
                else if (shownDb < -35) { status = "Düşük"; statusColor = Color.FromArgb(255, 200, 40); }
                else if (shownDb < -10) { status = "İyi"; statusColor = Color.FromArgb(60, 235, 140); }
                else { status = "Yüksek"; statusColor = Color.FromArgb(255, 80, 70); }
            }

            var rect = new RectangleF(x1 + S(2), 0, Width - x1 - S(4), Height);
            float numSize = sizeMode == 0 ? 6.5f : sizeMode == 1 ? 8.5f : 11f;
            using (var f1 = new Font("Segoe UI Semibold", numSize))
            using (var f2 = new Font("Segoe UI", 6.5f))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var b1 = new SolidBrush(noMic || muted ? statusColor : Mix(Color.FromArgb(150, 150, 160), Color.White, level)))
            using (var b2 = new SolidBrush(statusColor))
            {
                if (showStatus)
                {
                    string dbText = shownDb <= -89 ? "-∞ dB" : ((int)Math.Round(shownDb)).ToString() + " dB";
                    string line2 = (noMic || muted) ? status : dbText + " · " + status;
                    g.DrawString(numText, f1, b1, new RectangleF(rect.X, rect.Y + S(1), rect.Width, rect.Height / 2 + S(2)), sf);
                    g.DrawString(line2, f2, b2, new RectangleF(rect.X, rect.Height / 2, rect.Width, rect.Height / 2), sf);
                }
                else g.DrawString(numText, f1, b1, rect, sf);
            }
        }

        void DrawMicGlyph(Graphics g, float cx, float cy, float r, Color c)
        {
            float w = r * 0.55f, h = r * 0.95f;
            using (var b = new SolidBrush(c))
            using (GraphicsPath cap = RoundRect(new RectangleF(cx - w / 2, cy - h * 0.75f, w, h), w / 2))
                g.FillPath(b, cap);
            using (var pen = new Pen(c, Math.Max(1.2f, r * 0.13f)))
            {
                float aw = r * 0.95f;
                g.DrawArc(pen, cx - aw / 2, cy - h * 0.55f, aw, h * 0.95f, 0, 180);
                g.DrawLine(pen, cx, cy + h * 0.4f, cx, cy + h * 0.62f);
                g.DrawLine(pen, cx - w * 0.6f, cy + h * 0.62f, cx + w * 0.6f, cy + h * 0.62f);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            topTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            wave.Close();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }
    }
}
