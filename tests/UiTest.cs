// Drives the real form in-process: moves sliders through their handlers, then runs a compress straight from the Compress button.
// Args: <video> <screenshot dir>
using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CapCompress
{
    static class UiTest
    {


        static MainForm form;
        static string shots;
        static int failed;

        static T Get<T>(string name) { return (T)typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form); }
        static string Txt(string name) { return Get<Label>(name).Text; }

        static void Move(Slider bar, int value) { bar.UserSet(value); }

        static void Check(bool ok, string name) { Console.WriteLine((ok ? "ok   " : "FAIL ") + name); if (!ok) failed++; }

        static void State(string step)
        {
            Console.WriteLine(string.Format("[{0}] target={1} | trim={2} | res={3} | fps={4} | {5} | compress={6} | status={7}",
                step, Txt("targetValue"), Txt("trimValue"), Txt("resValue"), Txt("fpsValue"), Txt("bitrate"), Get<Btn>("compress").Enabled, Txt("status")));
        }

        static void Shot(string name)
        {
            using (var b = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                b.Save(shots + "\\" + name + ".png");
            }
        }

        static async Task Script()
        {
            await Task.Delay(2500);
            var target = Get<Slider>("target");
            var res = Get<Slider>("res");
            var fps = Get<Slider>("fps");
            var trim = Get<RangeSlider>("trim");
            State("loaded");
            Shot("1-loaded");
            Check(fps.Value == 24 && fps.Enabled, "frame rate starts at 24 and is adjustable");

            // Holding: the slider's own timer reads key state, faked here since a test can't hold real keys.
            var held = Get<RangeSlider>("trim");
            var key0 = typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            bool rightDown = false, shiftDown = false;
            held.KeyIsDown = k => k == Keys.Right && rightDown;
            held.ShiftIsDown = () => shiftDown;
            held.Focus();
            int[] at = new int[4];
            rightDown = true;
            key0.Invoke(held, new object[] { new KeyEventArgs(Keys.Right) });
            await Task.Delay(1000); at[0] = held.Low;
            shiftDown = true;                    // Windows sends no more arrow repeats from here
            await Task.Delay(400); at[1] = held.Low;
            shiftDown = false;
            await Task.Delay(700); at[2] = held.Low;
            shiftDown = true;
            await Task.Delay(200); at[3] = held.Low;
            rightDown = false;
            await Task.Delay(100);
            int after = held.Low;
            await Task.Delay(300);
            Console.WriteLine(string.Format("held key positions: {0}, {1}, {2}, {3}, after release {4}", at[0], at[1], at[2], at[3], held.Low));
            int slow1 = at[0], fast = at[1] - at[0], slow2 = at[2] - at[1], fast2 = at[3] - at[2];
            Check(held.Focused, "trim slider had focus during hold");
            Check(slow1 >= 5 && slow1 <= 40, "holding Right scrubs slowly: " + slow1);
            Check(fast >= 50, "pressing Shift mid-hold speeds up: " + fast);
            Check(slow2 >= 5 && slow2 < fast, "releasing Shift slows down but keeps moving: " + slow2);
            Check(fast2 >= 20, "pressing Shift again speeds up again: " + fast2);
            Check(held.Low == after, "stops when Right is released");
            held.UserSet(false, 0);

            var video = (VideoInfo)typeof(MainForm).GetField("video", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
            Move(target, int.MaxValue);
            Move(fps, fps.Maximum);
            State("target no limit");
            Check(Txt("targetValue") == "No limit", "the last stop of Target size is No limit");
            Check(fps.Value == video.MaxFps && Txt("resValue").StartsWith(string.Format("{0}x{1}", video.LongSide / 2 * 2, video.ShortSide / 2 * 2)),
                  "No limit allows the source resolution and fps: " + Txt("resValue") + ", " + fps.Value);
            var rate = Get<Slider>("rate");
            var autoRate = Get<Toggle>("autoRate");
            Check(autoRate.Enabled && !rate.Enabled && Txt("rateValue").StartsWith("Auto"), "No limit offers Auto bitrate, constant quality by default");
            autoRate.Checked = false;
            Check(rate.Enabled, "unticking Auto bitrate at No limit enables the slider");
            Move(rate, 0);
            State("no limit, 250 kbps");
            Check(Txt("rateValue") == "250 kbps" && Txt("bitrate").Contains("about"), "a hand-picked bitrate shows the size it gives: " + Txt("bitrate"));
            Check(!Txt("resValue").StartsWith(string.Format("{0}x", video.LongSide / 2 * 2)) || video.ShortSide <= 360, "a low bitrate caps the resolution: " + Txt("resValue"));
            autoRate.Checked = true;
            Move(target, target.Maximum - 1);
            State("target max");
            Check(target.Value / 10.0 < video.SizeBytes / 1048576.0, "largest size stops below source size");
            Check(!autoRate.Enabled && !rate.Enabled, "with a size set, the size decides the bitrate");

            Move(target, int.MinValue);
            State("target min");
            Get<Toggle>("autoRes").Checked = false;
            Check(res.Enabled && fps.Enabled, "unticking Auto enables sliders");

            Move(target, 20);   // 2 MB: too small for max resolution at max fps
            Move(fps, fps.Maximum);
            Move(res, res.Maximum);
            State("2 MB, both pushed to max");
            Shot("2-capped");
            int shortAtHighFps = res.Value;
            Check(res.Value < res.Maximum || fps.Value < fps.Maximum, "impossible combination is refused");

            Move(fps, fps.Minimum);
            Move(res, res.Maximum);
            State("2 MB, fps at minimum, res pushed to max");
            Check(res.Value >= shortAtHighFps, "lowering fps raises the resolution limit");
            Check(fps.Value >= 24, "fps stays at 24 or above");

            Get<Toggle>("autoFps").Checked = true;
            State("auto fps");
            Check(!fps.Enabled, "Auto frame rate disables the frame rate slider");
            Get<Toggle>("autoFps").Checked = false;
            Check(fps.Enabled, "unticking Auto frame rate enables the slider again");

            Get<Toggle>("autoRes").Checked = true;
            Move(target, 100);
            trim.UserSet(true, 60);
            Check(trim.High == 60 && trim.Low == 0, "high handle moves alone");
            State("10 MB, first 6 s");
            trim.UserSet(false, 110);
            Check(trim.Low == 50 && trim.High == 60, "low handle stops 1 s before high: " + trim.Low);
            State("handles close together");
            trim.UserSet(true, 0);
            Check(trim.High == 60, "high handle can't cross low");
            trim.UserSet(false, 0);
            Check(Txt("trimValue").Contains("keeps 0:06.0"), "kept time follows the handles");
            var input = typeof(RangeSlider).GetMethod("IsInputKey", BindingFlags.NonPublic | BindingFlags.Instance);
            Check((bool)input.Invoke(trim, new object[] { Keys.Right | Keys.Shift }) && (bool)input.Invoke(trim, new object[] { Keys.Left | Keys.Shift }), "shift+arrow reaches the trim slider");
            var key = typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            key.Invoke(trim, new object[] { new KeyEventArgs(Keys.Right) });
            Check(trim.Low == 1, "arrow moves trim 0.1 s: " + trim.Low);
            key.Invoke(trim, new object[] { new KeyEventArgs(Keys.Right | Keys.Shift) });
            Check(trim.Low == 11, "shift+arrow moves trim 1 s: " + trim.Low);
            key.Invoke(trim, new object[] { new KeyEventArgs(Keys.Left | Keys.Shift) });
            key.Invoke(trim, new object[] { new KeyEventArgs(Keys.Left) });
            Check(trim.Low == 0, "shift+arrow back to start");

            Get<Toggle>("mute").Checked = true;
            State("muted");
            Check(Txt("bitrate").Contains("no audio"), "mute shown in bitrate line");
            Get<Toggle>("mute").Checked = false;


            Get<Btn>("compress").PerformClick();
            Check(Get<Btn>("cancelButton").Enabled, "compress starts with no confirm dialog");
            for (int i = 0; i < 600 && Get<Btn>("cancelButton").Enabled; i++) await Task.Delay(200);
            State("finished");
            Shot("3-done");
            Check(Txt("status").StartsWith("Done"), "compress finished from the window");
            Check(Get<Btn>("showFile").Visible, "show file button appears");

            // Settings to come back after a restart (checked by Remembered).
            Get<Toggle>("mute").Checked = true;
            var enc = Get<Segmented>("encoder");
            if (enc.Items.Count > 1) enc.SelectedIndex = 1;
            Get<Toggle>("autoRes").Checked = false;
            Move(target, 50);
            Move(fps, 30);
            Move(res, 1);
            Get<Toggle>("autoFps").Checked = true;
            rememberedTarget = Lead(Txt("targetValue"));
            rememberedFps = fps.Value;
            rememberedRes = Lead(Txt("resValue"));
            rememberedNvenc = enc.SelectedIndex == 1;
            State("before restart");

            form.Close();
        }

        // The value without its "(max ...)", which depends on the trim.
        static string Lead(string text) { int i = text.IndexOf('('); return (i < 0 ? text : text.Substring(0, i)).Trim(); }

        static int rememberedFps;
        static string rememberedRes, rememberedTarget;
        static bool rememberedNvenc;

        // A fresh window on the same video picks up the last settings.
        static async Task Remembered()
        {
            await Task.Delay(2500);
            State("after restart");
            Check(!Get<Toggle>("autoRes").Checked, "restart keeps Auto resolution off");
            Check(Lead(Txt("targetValue")) == rememberedTarget, "restart keeps the target size: " + Txt("targetValue"));
            Check(Get<Slider>("fps").Value == rememberedFps, "restart keeps the frame rate: " + Get<Slider>("fps").Value);
            Check(Lead(Txt("resValue")) == rememberedRes, "restart keeps the resolution: " + Txt("resValue"));
            Check(Get<Toggle>("mute").Checked, "restart keeps Remove audio");
            Check(Get<Toggle>("autoFps").Checked, "restart keeps Auto frame rate");
            Check((Get<Segmented>("encoder").SelectedIndex == 1) == rememberedNvenc, "restart keeps the encoder");
            Check(Txt("trimValue").StartsWith("0:00.0"), "trim starts over for each video");
            form.Close();
        }

        // Off-screen and never activated, so the tests leave the desktop alone.
        class QuietForm : MainForm
        {
            public QuietForm(string file) : base(file) { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ShowInTaskbar = false; }
            protected override bool ShowWithoutActivation { get { return true; } }
        }

        static void Run(string video, Func<Task> script)
        {
            form = new QuietForm(video);
            form.Shown += async (o, e) =>
            {
                try { await script(); }
                catch (Exception ex) { Console.WriteLine("CRASH " + ex); failed++; form.Close(); }
            };
            Application.Run(form);
        }

        static void WindowPlace(string data)
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            var want = new Rectangle(area.X + 40, area.Y + 30, 900, 700);
            System.IO.File.WriteAllText(System.IO.Path.Combine(data, "settings.txt"),
                string.Format("Window={0},{1},{2},{3}\r\nMaximized=false\r\n", want.X, want.Y, want.Width, want.Height));
            using (var f = new MainForm(null)) Check(f.Bounds == want, "window comes back where it was: " + f.Bounds);
            System.IO.File.WriteAllText(System.IO.Path.Combine(data, "settings.txt"), "Window=-30000,-30000,900,700\r\n");
            using (var f = new MainForm(null)) Check(f.Left > -30000, "a window no longer on any screen isn't restored off-screen");
            System.IO.File.WriteAllText(System.IO.Path.Combine(data, "settings.txt"), "TargetMB=nonsense\r\nFps=\r\nWindow=1,2\r\n");
            using (var f = new MainForm(null)) Check(f.Left > -30000, "broken settings fall back to defaults");
            System.IO.File.Delete(System.IO.Path.Combine(data, "settings.txt"));
        }

        [STAThread]
        static int Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Application.EnableVisualStyles();
            shots = args[1];
            // Settings go to a folder of the test's own, starting empty, so the user's are never touched.
            string data = System.IO.Path.Combine(shots, "data");
            if (System.IO.Directory.Exists(data)) System.IO.Directory.Delete(data, true);
            System.IO.Directory.CreateDirectory(data);
            Environment.SetEnvironmentVariable("CAPCOMPRESS_DATA", data);
            WindowPlace(data);
            Run(args[0], Script);
            Check(System.IO.File.Exists(System.IO.Path.Combine(data, "settings.txt")), "closing saves the settings");
            Run(args[0], Remembered);
            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            return failed;
        }
    }
}
