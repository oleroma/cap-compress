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

            Move(target, int.MaxValue);
            State("target max");
            var video = (VideoInfo)typeof(MainForm).GetField("video", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
            Check(target.Value / 10.0 < video.SizeBytes / 1048576.0, "target slider stops below source size");

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

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            form.Close();
        }

        [STAThread]
        static int Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Application.EnableVisualStyles();
            shots = args[1];
            form = new MainForm(args[0]);
            form.Shown += async (o, e) =>
            {
                try { await Script(); }
                catch (Exception ex) { Console.WriteLine("CRASH " + ex); failed++; form.Close(); }
            };
            Application.Run(form);
            return failed;
        }
    }
}
