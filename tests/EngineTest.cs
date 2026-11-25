// Compile with src\Engine.cs. Checks size planning; args <video> <out dir> add real encodes, args download <dir> x tests the ffmpeg download.
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using CapCompress;

static class EngineTest
{
    static int failed;

    static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) failed++;
    }

    static int Main(string[] args)
    {
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");   // decimal comma must not leak into ffmpeg args

        var v = new VideoInfo { Path = "x", SizeBytes = 452L << 20, Duration = 47.3, Width = 1920, Height = 1080, Fps = 60, AudioTracks = 1 };
        var auto = Engine.MakePlan(v, new Settings());
        Check(auto.Short == 540 && auto.Fps == 30, "auto picks 540p30 at 10 MB: " + auto.Short + "p" + auto.Fps);
        Check(auto.AudioKbps == 128, "audio follows budget");

        var over = Engine.MakePlan(v, new Settings { TargetMB = 9999 });
        Check(over.TargetMB < 452 && over.TargetMB == over.MaxMB, "target capped below source: " + over.TargetMB);

        var trimmed = Engine.MakePlan(v, new Settings { TargetMB = 9999, TrimStart = 10, TrimEnd = 20 });
        Check(trimmed.MaxMB < 452 * 10 / 47.3, "cap scales with trim: " + trimmed.MaxMB);
        Check(Engine.MakePlan(v, new Settings { TargetMB = 10, TrimStart = 37 }).VideoKbps > auto.VideoKbps * 4, "trim raises bitrate");

        var muted = Engine.MakePlan(v, new Settings { Mute = true });
        Check(muted.AudioKbps == 0 && muted.VideoKbps > auto.VideoKbps, "mute gives audio share to video");

        var fps24 = Engine.MakePlan(v, new Settings { AutoFps = false, Fps = 24, AutoRes = false, Short = 1080 });
        var fps60 = Engine.MakePlan(v, new Settings { AutoFps = false, Fps = 60, AutoRes = false, Short = 1080 });
        Check(fps24.ResCap >= fps60.ResCap, "lower fps never lowers res cap: " + fps24.ResCap + " vs " + fps60.ResCap);
        Check(fps60.Short <= fps60.ResCap && fps60.Fps <= fps60.FpsCap, "manual values stay inside caps");

        var tight = new Settings { TargetMB = 2, AutoFps = false, Fps = 60, AutoRes = false, Short = 1080 };
        var tightPlan = Engine.MakePlan(v, tight);
        Check(tightPlan.Fps >= 24 && tightPlan.Short * (long)tightPlan.OutLong * tightPlan.Fps * 0.01 <= tightPlan.VideoKbps * 1000.0,
              "over-budget manual combo is clamped: " + tightPlan.Short + "p" + tightPlan.Fps);

        var longVid = new VideoInfo { Path = "x", SizeBytes = 200L << 20, Duration = 7200, Width = 1920, Height = 1080, Fps = 30, AudioTracks = 1 };
        Check(Engine.MakePlan(longVid, new Settings()).TargetMB >= Engine.MakePlan(longVid, new Settings()).MinMB, "target raised to minimum for long video");
        var hopeless = new VideoInfo { Path = "x", SizeBytes = 1L << 20, Duration = 7200, Width = 640, Height = 360, Fps = 30, AudioTracks = 1 };
        Check(!Engine.MakePlan(hopeless, new Settings()).CanReduce, "refuses when source is below the minimum");
        var slow = new VideoInfo { Path = "x", SizeBytes = 400L << 20, Duration = 30, Width = 1280, Height = 720, Fps = 15, AudioTracks = 0 };
        var slowPlan = Engine.MakePlan(slow, new Settings());
        Check(slowPlan.Fps == 15 && slowPlan.MinFps == 15, "low-fps source is never raised");
        var phone = new VideoInfo { Path = "x", SizeBytes = 400L << 20, Duration = 30, Width = 1080, Height = 1920, Fps = 60, AudioTracks = 1 };
        Check(Engine.MakePlan(phone, new Settings()).Rungs.Contains(1080), "portrait uses short side");

        var free = Engine.MakePlan(v, new Settings { NoLimit = true, TargetMB = 1 });
        Check(free.NoLimit && free.Short == 1080 && free.Fps == 60 && free.ResCap == 1080 && free.FpsCap == 60, "No limit keeps the source resolution and fps: " + free.Short + "p" + free.Fps);
        var freeManual = Engine.MakePlan(v, new Settings { NoLimit = true, AutoRes = false, Short = 720, AutoFps = false, Fps = 30 });
        Check(freeManual.Short == 720 && freeManual.Fps == 30, "No limit still takes a manual pick: " + freeManual.Short + "p" + freeManual.Fps);
        Check(Engine.QualityArgs(new Settings()).Contains("-crf") && Engine.QualityArgs(new Settings { Nvenc = true }).Contains("-cq"), "No limit encodes at constant quality");

        // ffmpeg arguments: HDR tone mapping and mixing several audio tracks.
        var sdrArgs = Engine.BaseArgs(v, new Settings(), auto, false);
        Check(!sdrArgs.Contains("tonemap"), "SDR isn't tone mapped");
        var hdr = new VideoInfo { Path = "x", SizeBytes = 400L << 20, Duration = 30, Width = 1080, Height = 1920, Fps = 30, AudioTracks = 1, Hdr = true };
        var hdrArgs = Engine.BaseArgs(hdr, new Settings(), Engine.MakePlan(hdr, new Settings()), true);
        Check(hdrArgs.Contains("tonemap=") && hdrArgs.Contains("-colorspace bt709") && hdrArgs.IndexOf("scale=") < hdrArgs.IndexOf("zscale="), "HDR is tone mapped to BT.709 after scaling");
        Check(Engine.AudioArgs(v, new Settings(), auto).Contains("-map 0:a:0"), "one audio track is kept as is");
        var obs = new VideoInfo { Path = "x", SizeBytes = 452L << 20, Duration = 47.3, Width = 1920, Height = 1080, Fps = 60, AudioTracks = 3 };
        var obsPlan = Engine.MakePlan(obs, new Settings());
        Check(Engine.AudioArgs(obs, new Settings(), obsPlan).Contains("[0:a:0][0:a:1][0:a:2]amix=inputs=3"), "several audio tracks are mixed");
        Check(Engine.AudioArgs(obs, new Settings { MixAudio = false }, obsPlan).Contains("-map 0:a:0 "), "mixing off keeps the first track");
        Check(Engine.AudioArgs(obs, new Settings { Mute = true }, Engine.MakePlan(obs, new Settings { Mute = true })).StartsWith(" -an"), "mute drops every track");

        if (args.Length >= 3 && args[0] == "download")
        {
            var flag = new CancelFlag();
            int last = -1;
            Engine.DownloadFfmpeg(args[1], p => { if (p / 25 != last) { last = p / 25; Console.WriteLine("download " + p + "%"); } }, flag);
            Check(File.Exists(Path.Combine(args[1], "ffmpeg.exe")) && File.Exists(Path.Combine(args[1], "ffprobe.exe")), "download extracted both tools");
        }
        else if (args.Length >= 2)
        {
            string ffmpeg = Engine.FindTool("ffmpeg"), ffprobe = Engine.FindTool("ffprobe");
            string input = args[0], dir = args[1];
            var info = Engine.Probe(ffprobe, input);
            Console.WriteLine("probed " + info.Width + "x" + info.Height + " " + info.Fps + " fps " + info.Duration + "s audio tracks=" + info.AudioTracks + " hdr=" + info.Hdr);
            bool nvenc = Engine.EncoderWorks(ffmpeg, "h264_nvenc");
            Console.WriteLine("nvenc works: " + nvenc);

            Encode(ffmpeg, ffprobe, info, new Settings { TargetMB = 5, TrimStart = 5, TrimEnd = 25 }, Path.Combine(dir, "x264 trim.mp4"));
            if (nvenc) Encode(ffmpeg, ffprobe, info, new Settings { TargetMB = 8, Nvenc = true }, Path.Combine(dir, "nvenc.mp4"));
            Encode(ffmpeg, ffprobe, info, new Settings { TargetMB = 3, Mute = true, TrimEnd = 10, AutoRes = false, Short = 360, AutoFps = false, Fps = 24 }, Path.Combine(dir, "manual mute.mp4"));

            var flag = new CancelFlag();
            string cancelled = Path.Combine(dir, "cancelled.mp4");
            var s = new Settings { TargetMB = 8 };
            bool threw = false;
            var t = new Thread(() =>
            {
                try { Engine.Run(ffmpeg, info, s, Engine.MakePlan(info, s), cancelled, (f, m) => { if (f > 0.02) flag.Cancel(); }, flag); }
                catch (CancelledException) { threw = true; }
            });
            t.Start();
            t.Join();
            Check(threw && !File.Exists(cancelled) && !File.Exists(cancelled + ".part.mp4"), "cancel leaves no files");
        }

        Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
        return failed;
    }

    static void Encode(string ffmpeg, string ffprobe, VideoInfo info, Settings s, string output)
    {
        var plan = Engine.MakePlan(info, s);
        var start = DateTime.Now;
        long size = Engine.Run(ffmpeg, info, s, plan, output, (f, m) => { }, new CancelFlag());
        var o = Engine.Probe(ffprobe, output);
        Console.WriteLine(string.Format("{0}: {1:0.00} MB, {2}x{3} {4:0.##} fps, {5:0.0}s, audio={6}, took {7:0}s",
            Path.GetFileName(output), size / 1048576.0, o.Width, o.Height, o.Fps, o.Duration, o.HasAudio, (DateTime.Now - start).TotalSeconds));
        Check(size <= (long)(plan.TargetMB * 1048576), Path.GetFileName(output) + " under target");
        Check(Math.Min(o.Width, o.Height) == plan.Short && Math.Abs(o.Fps - plan.Fps) < 1, Path.GetFileName(output) + " matches plan");
        Check(Math.Abs(o.Duration - plan.Kept) < 1, Path.GetFileName(output) + " duration matches trim");
        Check(o.HasAudio == (plan.AudioKbps > 0), Path.GetFileName(output) + " audio matches plan");
    }
}
