using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CapCompress
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            SetProcessDPIAware();
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
    }

    class MainForm : DarkForm
    {
        readonly Btn browse = new Btn("Browse");
        readonly Label fileLabel = Text0();
        readonly Slider target = Bar(), res = Bar(), fps = Bar();
        readonly RangeSlider trim = new RangeSlider { Dock = DockStyle.Fill, Height = Theme.S(34), Margin = new Padding(Theme.S(10), 0, Theme.S(10), 0) };
        readonly Label targetValue = Text0(), trimValue = Text0(), resValue = Text0(), fpsValue = Text0();
        readonly Toggle autoRes = new Toggle("Auto") { Checked = true };
        readonly Toggle mute = new Toggle("Remove audio");
        readonly Segmented encoder = new Segmented();
        readonly Label bitrate = Text0();
        readonly ProgressLine progress = new ProgressLine { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = Theme.S(6) };
        readonly Label status = Text0();
        readonly Btn compress = new Btn("Compress", true) { Enabled = false };
        readonly Btn cancelButton = new Btn("Cancel") { Enabled = false };
        readonly Btn showFile = new Btn("Show file") { Visible = false };
        readonly Card card = new Card { Dock = DockStyle.Fill, Height = Theme.S(68), ColumnCount = 2, RowCount = 2, Padding = new Padding(Theme.S(14), Theme.S(8), Theme.S(12), Theme.S(8)), Margin = new Padding(0, 0, 0, Theme.S(10)) };
        readonly Label fileInfo = Text0();

        string ffmpeg, ffprobe;
        string lastOutput;
        VideoInfo video;
        Plan plan;
        CancelFlag cancel;
        bool updating;
        bool busy;
        double targetMB = 10;
        int manualShort, manualFps;
        readonly string startupFile;
        readonly float scale;
        readonly Prefs prefs = Prefs.Load();

        static Label Text0() { return new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = "" }; }
        static Slider Bar() { return new Slider { Dock = DockStyle.Fill, Height = Theme.S(34), Margin = new Padding(Theme.S(10), 0, Theme.S(10), 0) }; }

        public MainForm(string file)
        {
            startupFile = file;
            Text = "Cap Compress";
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            ClientSize = new Size(S(660), S(418));
            MinimumSize = new Size(S(580), S(456));
            RestoreWindow();

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(S(18), S(16), S(18), S(16)), AutoScroll = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(150)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(grid);

            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            fileLabel.Text = "Drop a video here";
            fileLabel.AutoEllipsis = true;
            fileLabel.AutoSize = false;
            fileLabel.Dock = DockStyle.Fill;
            fileLabel.UseMnemonic = false;
            fileLabel.TextAlign = ContentAlignment.BottomLeft;
            fileLabel.Font = new Font("Segoe UI Semibold", 10f);
            fileInfo.Text = "or browse for one";
            fileInfo.AutoSize = false;
            fileInfo.Dock = DockStyle.Fill;
            fileInfo.TextAlign = ContentAlignment.TopLeft;
            fileInfo.ForeColor = Theme.Dim;
            card.Controls.Add(fileLabel, 0, 0);
            card.Controls.Add(fileInfo, 0, 1);
            card.Controls.Add(browse, 1, 0);
            card.SetRowSpan(browse, 2);
            browse.Anchor = AnchorStyles.Right;
            grid.Controls.Add(card, 0, 0);
            grid.SetColumnSpan(card, 4);

            AddRow(grid, 1, "Target size", target, targetValue, null);
            AddRow(grid, 2, "Trim", trim, trimValue, null);
            AddRow(grid, 3, "Resolution", res, resValue, autoRes);
            AddRow(grid, 4, "Frame rate", fps, fpsValue, null);

            var encLabel = Text0();
            encLabel.Text = "Encoder";
            encLabel.ForeColor = Theme.Dim;
            encoder.Dock = DockStyle.Fill;
            encoder.Height = S(34);
            encoder.Margin = new Padding(S(10), S(4), S(10), S(4));
            grid.Controls.Add(encLabel, 0, 5);
            grid.Controls.Add(encoder, 1, 5);
            grid.Controls.Add(mute, 2, 5);
            grid.SetColumnSpan(mute, 2);

            var rateLabel = Text0();
            rateLabel.Text = "Bitrate";
            rateLabel.ForeColor = Theme.Dim;
            bitrate.Margin = new Padding(S(10), S(6), 0, S(6));
            status.ForeColor = Theme.Dim;
            grid.Controls.Add(rateLabel, 0, 6);
            grid.Controls.Add(bitrate, 1, 6);
            grid.SetColumnSpan(bitrate, 3);

            progress.Margin = new Padding(0, S(8), 0, S(2));
            grid.Controls.Add(progress, 0, 7);
            grid.SetColumnSpan(progress, 4);
            status.AutoSize = false;
            status.Dock = DockStyle.Fill;
            status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            grid.Controls.Add(status, 0, 8);
            grid.SetColumnSpan(status, 4);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            buttons.Controls.Add(compress);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(showFile);
            grid.Controls.Add(buttons, 0, 9);
            grid.SetColumnSpan(buttons, 4);

            for (int i = 0; i < 10; i++)
                grid.RowStyles.Add(new RowStyle(i == 8 ? SizeType.Percent : SizeType.AutoSize, 100));
            grid.RowCount = 10;

            AcceptButton = compress;

            EventHandler changed = (o, e) => { if (!updating) Recalculate(); };
            // What the user picks is remembered between restarts (see Prefs).
            target.Scroll += (o, e) => { targetMB = target.Value / 10.0; prefs.TargetMB = targetMB; Recalculate(); };
            trim.Scroll += (o, e) => Recalculate();
            res.Scroll += (o, e) => { manualShort = plan.Rungs[res.Value]; prefs.Short = manualShort; Recalculate(); };
            fps.Scroll += (o, e) => { manualFps = fps.Value; prefs.Fps = manualFps; Recalculate(); };
            autoRes.CheckedChanged += (o, e) => { if (plan != null) manualShort = plan.Short; if (!updating) prefs.AutoRes = autoRes.Checked; changed(o, e); };
            mute.CheckedChanged += (o, e) => { if (!updating) prefs.Mute = mute.Checked; changed(o, e); };
            encoder.SelectedIndexChanged += (o, e) => { if (!updating) prefs.Nvenc = encoder.SelectedIndex == 1; changed(o, e); };

            browse.Click += (o, e) => Browse();
            compress.Click += (o, e) => StartCompress();
            cancelButton.Click += (o, e) => { if (cancel != null) { cancel.Cancel(); status.Text = "Cancelling..."; } };
            showFile.Click += (o, e) => { if (lastOutput != null) Process.Start("explorer.exe", "/select,\"" + lastOutput + "\""); };
            AcceptDrops(this);
            Shown += (o, e) => InitTools();
            FormClosing += (o, e) => { if (cancel != null) cancel.Cancel(); SaveWindow(); prefs.Save(); };

            SetInputs();
        }

        int S(int x) { return (int)(x * scale); }

        // Puts the window back where it was, unless that's no longer on any screen.
        void RestoreWindow()
        {
            var b = prefs.Window;
            if (b.Width < MinimumSize.Width || b.Height < MinimumSize.Height) return;
            bool visible = false;
            foreach (var screen in Screen.AllScreens)
            {
                var common = Rectangle.Intersect(screen.WorkingArea, b);
                if (common.Width >= S(100) && common.Height >= S(50)) visible = true;
            }
            if (!visible) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = b;
            if (prefs.Maximized) WindowState = FormWindowState.Maximized;
        }

        void SaveWindow()
        {
            if (WindowState == FormWindowState.Minimized && prefs.Window.Width > 0) return;
            prefs.Window = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            prefs.Maximized = WindowState == FormWindowState.Maximized;
        }

        void AcceptDrops(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (o, e) =>
            {
                if (busy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                e.Effect = DragDropEffects.Copy;
                card.Highlight = true;
            };
            c.DragLeave += (o, e) => card.Highlight = false;
            c.DragDrop += (o, e) =>
            {
                card.Highlight = false;
                var f = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (f != null && f.Length > 0) LoadVideo(f[0]);
            };
            foreach (Control child in c.Controls) AcceptDrops(child);
        }

        void AddRow(TableLayoutPanel grid, int row, string name, Control bar, Label value, CheckBox box)
        {
            var label = Text0();
            label.Text = name;
            label.ForeColor = Theme.Dim;
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(bar, 1, row);
            grid.Controls.Add(value, 2, row);
            if (box != null) grid.Controls.Add(box, 3, row);
            else grid.SetColumnSpan(value, 2);
        }

        // ---- ffmpeg setup ----

        void InitTools()
        {
            ffmpeg = Engine.FindTool("ffmpeg");
            ffprobe = Engine.FindTool("ffprobe");
            if (ffmpeg != null && ffprobe != null) { ToolsReady(); return; }

            bool download = ThemedDialog.Ask(this, "ffmpeg needed", new string[,]
            {
                { null, "This app needs ffmpeg, which wasn't found." },
                { null, "Download it now (about 100 MB) from gyan.dev into a folder next to this app?" }
            }, "Download", "Not now");
            if (!download)
            {
                status.Text = "ffmpeg is required. Put ffmpeg.exe and ffprobe.exe next to this app and restart it.";
                browse.Enabled = false;
                return;
            }

            string dest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
            cancel = new CancelFlag();
            var flag = cancel;
            SetBusy(true);
            status.Text = "Downloading ffmpeg...";
            var worker = new Thread(() =>
            {
                string error = null;
                try { Engine.DownloadFfmpeg(dest, p => Ui(() => { progress.Value = p * 10; status.Text = "Downloading ffmpeg... " + p + "%"; }), flag); }
                catch (CancelledException) { error = "Download cancelled."; }
                catch (Exception ex) { error = "Couldn't download ffmpeg: " + ex.Message; }
                Ui(() =>
                {
                    SetBusy(false);
                    progress.Value = 0;
                    cancel = null;
                    if (error != null) { status.Text = error; browse.Enabled = false; return; }
                    ffmpeg = Path.Combine(dest, "ffmpeg.exe");
                    ffprobe = Path.Combine(dest, "ffprobe.exe");
                    ToolsReady();
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void ToolsReady()
        {
            updating = true;
            encoder.Items.Clear();
            encoder.Items.Add("x264 · best quality");
            if (Engine.HasEncoder(ffmpeg, "h264_nvenc")) encoder.Items.Add("NVENC · fast");
            encoder.SelectedIndex = prefs.Nvenc && encoder.Items.Count > 1 ? 1 : 0;
            updating = false;
            status.Text = "Ready.";
            if (startupFile != null) LoadVideo(startupFile);
        }

        void Ui(Action action)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // ---- input ----

        void Browse()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.ts;*.m4v;*.wmv|All files|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK) LoadVideo(dialog.FileName);
            }
        }

        void LoadVideo(string path)
        {
            if (busy || ffprobe == null) return;
            try
            {
                video = Engine.Probe(ffprobe, path);
            }
            catch (Exception ex)
            {
                video = null;
                plan = null;
                status.Text = ex.Message;
                SetInputs();
                return;
            }

            fileLabel.Text = Path.GetFileName(path);
            fileInfo.Text = string.Format("{0}x{1}  ·  {2:0.##} fps  ·  {3}  ·  {4}",
                video.Width, video.Height, video.Fps, Time(video.Duration), SizeText(video.SizeBytes));
            int tenths = (int)Math.Round(video.Duration * 10);
            updating = true;
            autoRes.Checked = prefs.AutoRes;
            mute.Checked = prefs.Mute;
            trim.Reset(tenths, Math.Min(10, tenths));
            updating = false;
            // Last used settings; the plan fits them to this video.
            manualShort = prefs.Short > 0 ? prefs.Short : video.ShortSide;
            manualFps = Math.Min(prefs.Fps > 0 ? prefs.Fps : 24, video.MaxFps);
            targetMB = prefs.TargetMB;
            showFile.Visible = false;
            progress.Value = 0;
            status.Text = "";
            Recalculate();
        }

        Settings ReadSettings()
        {
            int tenths = (int)Math.Round(video.Duration * 10);
            return new Settings
            {
                TargetMB = targetMB,
                TrimStart = trim.Low / 10.0,
                TrimEnd = trim.High >= tenths ? 0 : trim.High / 10.0,
                AutoRes = autoRes.Checked,
                AutoFps = false,
                Short = manualShort,
                Fps = manualFps,
                Nvenc = encoder.SelectedIndex == 1,
                Mute = mute.Checked
            };
        }

        // Rebuilds the plan and pushes its limits back into every control.
        void Recalculate()
        {
            if (video == null) { SetInputs(); return; }
            plan = Engine.MakePlan(video, ReadSettings());
            manualShort = plan.Short;
            manualFps = plan.Fps;

            updating = true;
            if (plan.CanReduce)
            {
                target.SetRange((int)Math.Round(plan.MinMB * 10), (int)Math.Round(plan.MaxMB * 10));
                target.Value = Math.Max(target.Minimum, Math.Min(target.Maximum, (int)Math.Round(plan.TargetMB * 10)));
                target.LargeChange = Math.Max(1, (target.Maximum - target.Minimum) / 10);
            }
            res.SetRange(0, plan.Rungs.Count - 1);
            res.Value = plan.Rungs.IndexOf(plan.Short);
            fps.SetRange(plan.MinFps, video.MaxFps);
            fps.Value = plan.Fps;
            updating = false;

            targetValue.Text = plan.CanReduce ? string.Format("{0:0.0} MB (max {1:0.0})", plan.TargetMB, plan.MaxMB) : "not possible";
            trimValue.Text = string.Format("{0} – {1}  (keeps {2})", Time(trim.Low / 10.0), Time(trim.High / 10.0), Time(plan.Kept));
            resValue.Text = string.Format("{0}x{1}  (max {2}p)", plan.OutLong, plan.Short, plan.ResCap);
            fpsValue.Text = string.Format("{0} fps  (max {1})", plan.Fps, plan.FpsCap);
            bitrate.Text = plan.AudioKbps > 0
                ? string.Format("Video {0} kbps, audio {1} kbps", plan.VideoKbps, plan.AudioKbps)
                : string.Format("Video {0} kbps, no audio", plan.VideoKbps);

            if (!plan.CanReduce && !busy)
                status.Text = "This can't be made smaller: the selected part is already below the smallest possible size.";
            else if (status.Text.StartsWith("This can't be made smaller"))
                status.Text = "";
            SetInputs();
        }

        void SetInputs()
        {
            bool ready = video != null && !busy;
            bool canReduce = ready && plan != null && plan.CanReduce;
            browse.Enabled = !busy;
            target.Enabled = canReduce;
            trim.Enabled = ready;
            autoRes.Enabled = canReduce;
            res.Enabled = canReduce && !autoRes.Checked && plan.Rungs.Count > 1;
            fps.Enabled = canReduce && video.MaxFps > plan.MinFps;
            mute.Enabled = ready && video.HasAudio;
            encoder.Enabled = ready;
            compress.Enabled = canReduce;
            cancelButton.Enabled = busy;
        }

        void SetBusy(bool value)
        {
            busy = value;
            SetInputs();
        }

        // ---- running ----

        void StartCompress()
        {
            if (video == null || plan == null || !plan.CanReduce || busy) return;
            var settings = ReadSettings();
            prefs.Save();
            var runPlan = plan;
            var source = video;
            string output = Engine.OutputPath(source.Path);


            cancel = new CancelFlag();
            var flag = cancel;
            string tool = ffmpeg;
            showFile.Visible = false;
            progress.Value = 0;
            SetBusy(true);

            var worker = new Thread(() =>
            {
                string message;
                string done = null;
                try
                {
                    long size = Engine.Run(tool, source, settings, runPlan, output, (f, text) => Ui(() =>
                    {
                        progress.Value = (int)(f * 1000);
                        status.Text = string.Format("{0}... {1:0}%", text, f * 100);
                    }), flag);
                    message = string.Format("Done: {0} (was {1}).", SizeText(size), SizeText(source.SizeBytes));
                    done = output;
                }
                catch (CancelledException) { message = "Cancelled. No file was saved."; }
                catch (Exception ex) { message = ex.Message; }

                Ui(() =>
                {
                    SetBusy(false);
                    cancel = null;
                    status.Text = message.Replace('\n', ' ');
                    if (done != null)
                    {
                        progress.Value = 1000;
                        lastOutput = done;
                        showFile.Visible = true;
                    }
                    else
                    {
                        progress.Value = 0;
                    }
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        static string Time(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00.0}", (int)t.TotalHours, t.Minutes, t.Seconds + t.Milliseconds / 1000.0)
                : string.Format("{0}:{1:00.0}", t.Minutes, t.Seconds + t.Milliseconds / 1000.0);
        }

        static string SizeText(long bytes)
        {
            return string.Format("{0:0.0} MB", bytes / 1048576.0);
        }
    }

    // The settings last used, kept in %LOCALAPPDATA%\CapCompress\settings.txt as key=value lines.
    // CAPCOMPRESS_DATA moves the folder (the tests use it). Trim isn't kept: it belongs to one video.
    class Prefs
    {
        public double TargetMB = 10;
        public bool AutoRes = true, Nvenc, Mute;
        public int Short, Fps;          // 0 until the user picks one
        public Rectangle Window;
        public bool Maximized;

        public static string Dir
        {
            get
            {
                return Environment.GetEnvironmentVariable("CAPCOMPRESS_DATA")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapCompress");
            }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.txt"); } }

        static bool Int(string s, out int i) { return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i); }

        public static Prefs Load()
        {
            var p = new Prefs();
            try
            {
                if (!File.Exists(FilePath)) return p;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    double d;
                    int i;
                    switch (key)
                    {
                        case "TargetMB": if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0) p.TargetMB = d; break;
                        case "AutoRes": p.AutoRes = value != "false"; break;
                        case "Short": if (Int(value, out i) && i > 0) p.Short = i; break;
                        case "Fps": if (Int(value, out i) && i > 0) p.Fps = i; break;
                        case "Encoder": p.Nvenc = value == "nvenc"; break;
                        case "Mute": p.Mute = value == "true"; break;
                        case "Maximized": p.Maximized = value == "true"; break;
                        case "Window":
                            var n = value.Split(',');
                            int x, y, w, h;
                            if (n.Length == 4 && Int(n[0], out x) && Int(n[1], out y) && Int(n[2], out w) && Int(n[3], out h))
                                p.Window = new Rectangle(x, y, w, h);
                            break;
                    }
                }
            }
            catch (Exception) { }   // unreadable settings just mean defaults
            return p;
        }

        public void Save()
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var lines = new System.Collections.Generic.List<string>
                {
                    "TargetMB=" + TargetMB.ToString("0.0", inv),
                    "AutoRes=" + (AutoRes ? "true" : "false"),
                    "Short=" + Short.ToString(inv),
                    "Fps=" + Fps.ToString(inv),
                    "Encoder=" + (Nvenc ? "nvenc" : "x264"),
                    "Mute=" + (Mute ? "true" : "false"),
                    "Maximized=" + (Maximized ? "true" : "false")
                };
                if (Window.Width > 0)
                    lines.Add(string.Format(inv, "Window={0},{1},{2},{3}", Window.X, Window.Y, Window.Width, Window.Height));
                Directory.CreateDirectory(Dir);
                string temp = FilePath + ".tmp";
                File.WriteAllLines(temp, lines.ToArray());
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            catch (Exception) { }   // not being able to save settings shouldn't stop anything
        }
    }
}
