using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;

namespace CapCompress
{
    public class VideoInfo
    {
        public string Path;
        public long SizeBytes;
        public double Duration;
        public int Width, Height;
        public double Fps;
        public int AudioTracks;
        public bool Hdr;         // PQ or HLG: needs tone mapping to look right in SDR

        public bool HasAudio { get { return AudioTracks > 0; } }

        public int ShortSide { get { return Math.Min(Width, Height); } }
        public int LongSide { get { return Math.Max(Width, Height); } }
        public int MaxFps { get { return Math.Max(1, (int)Math.Round(Fps)); } }
    }

    public class Settings
    {
        public double TargetMB = 10;
        public double TrimStart;
        public double TrimEnd;   // 0 means the end of the video
        public bool AutoRes = true, AutoFps = true;
        public int Short;        // used when AutoRes is off
        public int Fps;          // used when AutoFps is off
        public bool Nvenc, Mute;
        public bool MixAudio = true;   // with several audio tracks, mix them all instead of keeping the first
        public bool NoLimit;           // no target size: the size is what the encode comes out at
        public bool AutoBitrate = true;  // with NoLimit: constant quality; off: VideoKbps
        public int VideoKbps;            // used with NoLimit when AutoBitrate is off
    }

    public class Plan
    {
        public double Kept;
        public double MinMB, MaxMB, TargetMB;
        public bool CanReduce;
        public bool NoLimit;
        public int AudioKbps, VideoKbps;
        public List<int> Rungs;
        public int Short, Fps, MinFps, ResCap, FpsCap;
        public int OutLong;
    }

    public class CancelFlag
    {
        public volatile bool Cancelled;
        public volatile Process Current;

        public void Cancel()
        {
            Cancelled = true;
            var p = Current;
            try { if (p != null && !p.HasExited) p.Kill(); } catch { }
        }
    }

    public class CancelledException : Exception { }

    public static class Engine
    {
        const double Mb = 1024 * 1024;
        // Share of the target given to the encoder; the rest is headroom for overshoot and muxing.
        const double Usable = 0.95;
        // Bits per pixel per frame. Below the floor the encoder can't reliably hit the bitrate;
        // Auto aims for the goal.
        const double FloorX264 = 0.01, FloorNvenc = 0.02, Goal = 0.1;
        const int MinVideoX264 = 50, MinVideoNvenc = 100, MinAudio = 48, FpsFloor = 24;
        // Constant quality for No limit: visually close to the source for both encoders.
        public const int Crf = 20, Cq = 23;
        const int NoLimitAudio = 128;
        static readonly int[] Ladder = { 144, 240, 360, 480, 540, 720, 900, 1080, 1440, 2160, 2880, 4320 };

        public const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        static long Pixels(VideoInfo v, int shortSide)
        {
            return (long)shortSide * OutLong(v, shortSide);
        }

        static int OutLong(VideoInfo v, int shortSide)
        {
            return (int)Math.Round((double)v.LongSide * shortSide / v.ShortSide / 2) * 2;
        }

        static double Bpp(VideoInfo v, int shortSide, int fps, int videoKbps)
        {
            return videoKbps * 1000.0 / (Pixels(v, shortSide) * (double)fps);
        }

        public static Plan MakePlan(VideoInfo v, Settings s)
        {
            var p = new Plan();
            double end = s.TrimEnd > 0 ? Math.Min(s.TrimEnd, v.Duration) : v.Duration;
            p.Kept = Math.Max(0.1, end - s.TrimStart);

            bool hasAudio = v.HasAudio && !s.Mute;
            int minVideo = s.Nvenc ? MinVideoNvenc : MinVideoX264;
            double floor = s.Nvenc ? FloorNvenc : FloorX264;

            double minBytes = (minVideo + (hasAudio ? MinAudio : 0)) * 1000.0 * p.Kept / 8 / Usable;
            double sourceMB = v.SizeBytes * (p.Kept / v.Duration) / Mb;
            p.MinMB = Math.Max(0.1, Math.Ceiling(minBytes / Mb * 10) / 10);
            p.MaxMB = Math.Floor(sourceMB * 10) / 10;
            if (p.MaxMB >= sourceMB) p.MaxMB -= 0.1;   // never allow a target at or above the source
            p.CanReduce = p.MaxMB >= p.MinMB;
            p.TargetMB = Math.Max(p.MinMB, Math.Min(p.MaxMB, s.TargetMB));

            double totalKbps = p.TargetMB * Mb * 8 * Usable / p.Kept / 1000;
            p.AudioKbps = !hasAudio ? 0 : totalKbps >= 1000 ? 128 : totalKbps >= 300 ? 96 : totalKbps >= 120 ? 64 : MinAudio;
            p.VideoKbps = Math.Max(1, (int)(totalKbps - p.AudioKbps));
            if (s.NoLimit)
            {
                // No bitrate to share out, so nothing caps resolution or fps below the source.
                // VideoKbps is only a stand-in big enough for the caps; the encode uses CRF/CQ.
                p.NoLimit = true;
                p.CanReduce = true;
                p.AudioKbps = hasAudio ? NoLimitAudio : 0;
                // A bitrate picked by hand caps resolution and fps like a size does.
                p.VideoKbps = s.AutoBitrate ? 1000000 : Math.Max(minVideo, s.VideoKbps);
            }

            p.Rungs = MakeRungs(v);
            p.MinFps = Math.Min(FpsFloor, v.MaxFps);

            Func<int, int> resCap = fps =>
            {
                int best = p.Rungs[0];
                foreach (int r in p.Rungs)
                    if (Bpp(v, r, fps, p.VideoKbps) >= floor) best = r;
                return best;
            };
            Func<int, int> fpsCap = shortSide =>
            {
                int f = (int)(p.VideoKbps * 1000.0 / (Pixels(v, shortSide) * floor));
                return Math.Max(p.MinFps, Math.Min(v.MaxFps, f));
            };
            Func<int, int> bestResAtGoal = fps =>
            {
                int best = p.Rungs[0];
                foreach (int r in p.Rungs)
                    if (Bpp(v, r, fps, p.VideoKbps) >= Goal) best = r;
                return best;
            };

            if (s.AutoRes && s.AutoFps)
            {
                // Spend the bitrate on resolution before motion: 60 -> 30 fps first,
                // then step resolution down, and only then drop to the fps floor.
                int top = p.Rungs[p.Rungs.Count - 1];
                p.Fps = v.MaxFps;
                if (Bpp(v, top, p.Fps, p.VideoKbps) < Goal && p.Fps > 30) p.Fps = 30;
                p.Short = bestResAtGoal(p.Fps);
                if (Bpp(v, p.Short, p.Fps, p.VideoKbps) < Goal) p.Fps = p.MinFps;
            }
            else if (s.AutoRes)
            {
                p.Fps = Clamp(s.Fps, p.MinFps, v.MaxFps);
                p.Short = bestResAtGoal(p.Fps);
                p.Fps = Math.Min(p.Fps, fpsCap(p.Short));
            }
            else if (s.AutoFps)
            {
                p.Short = Math.Min(NearestRung(p.Rungs, s.Short), resCap(p.MinFps));
                p.Fps = p.MinFps;
                foreach (int f in new[] { v.MaxFps, 30 })
                    if (f <= v.MaxFps && f > p.Fps && Bpp(v, p.Short, f, p.VideoKbps) >= Goal) { p.Fps = f; break; }
            }
            else
            {
                p.Fps = Clamp(s.Fps, p.MinFps, v.MaxFps);
                p.Short = Math.Min(NearestRung(p.Rungs, s.Short), resCap(p.Fps));
                p.Fps = Math.Min(p.Fps, fpsCap(p.Short));
            }

            p.ResCap = resCap(p.Fps);
            p.FpsCap = fpsCap(p.Short);
            p.OutLong = OutLong(v, p.Short);
            return p;
        }

        static List<int> MakeRungs(VideoInfo v)
        {
            int source = v.ShortSide / 2 * 2;
            var rungs = new List<int>();
            foreach (int r in Ladder)
                if (r < source) rungs.Add(r);
            rungs.Add(source);
            return rungs;
        }

        static int NearestRung(List<int> rungs, int value)
        {
            int best = rungs[0];
            foreach (int r in rungs)
                if (r <= value) best = r;
            return best;
        }

        static int Clamp(int x, int lo, int hi) { return Math.Max(lo, Math.Min(hi, x)); }

        // ---- ffmpeg discovery and download ----

        public static string FindTool(string name)
        {
            string file = name + ".exe";
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            var dirs = new List<string> { appDir, Path.Combine(appDir, "ffmpeg") };
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links"));
            foreach (string dir in dirs)
            {
                try
                {
                    if (dir.Length == 0) continue;
                    string path = Path.Combine(dir.Trim(), file);
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public static void DownloadFfmpeg(string destDir, Action<int> percent, CancelFlag cancel)
        {
            Directory.CreateDirectory(destDir);
            string zip = Path.Combine(destDir, "ffmpeg-download.zip");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            try
            {
                using (var web = new WebClient())
                using (var done = new ManualResetEvent(false))
                {
                    Exception error = null;
                    web.DownloadProgressChanged += (o, e) =>
                    {
                        percent(e.ProgressPercentage);
                        if (cancel.Cancelled) web.CancelAsync();
                    };
                    web.DownloadFileCompleted += (o, e) => { error = e.Error; done.Set(); };
                    web.DownloadFileAsync(new Uri(FfmpegZipUrl), zip);
                    done.WaitOne();
                    if (cancel.Cancelled) throw new CancelledException();
                    if (error != null) throw error;
                }
                using (var archive = ZipFile.OpenRead(zip))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name == "ffmpeg.exe" || entry.Name == "ffprobe.exe")
                            entry.ExtractToFile(Path.Combine(destDir, entry.Name), true);
                    }
                }
                if (!File.Exists(Path.Combine(destDir, "ffmpeg.exe")) || !File.Exists(Path.Combine(destDir, "ffprobe.exe")))
                    throw new Exception("The download didn't contain ffmpeg.exe and ffprobe.exe.");
            }
            finally
            {
                try { File.Delete(zip); } catch { }
            }
        }

        public static bool HasEncoder(string ffmpeg, string encoder)
        {
            try { return Capture(ffmpeg, "-hide_banner -encoders").Contains(" " + encoder + " "); }
            catch { return false; }
        }

        public static bool HasFilter(string ffmpeg, string filter)
        {
            try { return Capture(ffmpeg, "-hide_banner -filters").Contains(" " + filter + " "); }
            catch { return false; }
        }

        // An ffmpeg built with an encoder can still lack the hardware for it (NVENC without an
        // NVIDIA card), so this encodes one small frame to be sure. Slow (about a second): call it
        // off the UI thread.
        public static bool EncoderWorks(string ffmpeg, string encoder)
        {
            if (!HasEncoder(ffmpeg, encoder)) return false;
            var psi = new ProcessStartInfo(ffmpeg,
                "-hide_banner -loglevel error -nostdin -f lavfi -i color=c=black:s=256x256:r=25 -frames:v 1 -c:v " + encoder + " -f null -")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                using (var proc = Process.Start(psi))
                {
                    proc.OutputDataReceived += (o, e) => { };
                    proc.ErrorDataReceived += (o, e) => { };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    if (!proc.WaitForExit(20000))
                    {
                        try { proc.Kill(); } catch { }
                        return false;
                    }
                    return proc.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // ---- probing ----

        static string Capture(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var proc = Process.Start(psi))
            {
                proc.ErrorDataReceived += (o, e) => { };
                proc.BeginErrorReadLine();
                string text = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                return text;
            }
        }

        public static VideoInfo Probe(string ffprobe, string path)
        {
            var v = new VideoInfo { Path = path, SizeBytes = new FileInfo(path).Length };
            string text = Capture(ffprobe, string.Format(
                "-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate,r_frame_rate,color_transfer:format=duration -of default=nw=1 \"{0}\"", path));
            double avg = 0, raw = 0;
            foreach (string line in text.Split('\n'))
            {
                string[] kv = line.Trim().Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;
                if (kv[0] == "width") int.TryParse(kv[1], out v.Width);
                else if (kv[0] == "height") int.TryParse(kv[1], out v.Height);
                else if (kv[0] == "duration") double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v.Duration);
                else if (kv[0] == "avg_frame_rate") avg = ParseRate(kv[1]);
                else if (kv[0] == "r_frame_rate") raw = ParseRate(kv[1]);
                else if (kv[0] == "color_transfer") v.Hdr = kv[1] == "smpte2084" || kv[1] == "arib-std-b67";
            }
            if (v.Width <= 0 || v.Height <= 0 || v.Duration <= 0)
                throw new Exception("This file doesn't look like a video that can be read.");
            v.Fps = avg > 0 ? avg : raw > 0 ? raw : 30;
            string audio = Capture(ffprobe, string.Format(
                "-v error -select_streams a -show_entries stream=codec_type -of default=nw=1 \"{0}\"", path));
            foreach (string line in audio.Split('\n'))
                if (line.Trim() == "codec_type=audio") v.AudioTracks++;
            return v;
        }

        static double ParseRate(string rate)
        {
            string[] parts = rate.Split('/');
            double n, d = 1;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return 0;
            if (parts.Length > 1 && (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d) || d == 0)) return 0;
            return n / d;
        }

        // ---- encoding ----

        public static string OutputPath(string input)
        {
            string dir = Path.GetDirectoryName(input);
            string name = Path.GetFileNameWithoutExtension(input) + "_compressed";
            string path = Path.Combine(dir, name + ".mp4");
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(dir, string.Format("{0} ({1}).mp4", name, i));
            return path;
        }

        // Returns the size of the finished file. The output only appears once it fits the target.
        public static long Run(string ffmpeg, VideoInfo v, Settings s, Plan p, string output,
                               Action<double, string> progress, CancelFlag cancel)
        {
            string part = output + ".part.mp4";
            string passlog = Path.Combine(Path.GetTempPath(), "cap-compress-" + Guid.NewGuid().ToString("N"));
            long targetBytes = (long)(p.TargetMB * Mb);
            int minVideo = s.Nvenc ? MinVideoNvenc : MinVideoX264;
            int video = p.VideoKbps;
            bool toneMap = v.Hdr && HasFilter(ffmpeg, "zscale");
            try
            {
                if (p.NoLimit) return RunNoLimit(ffmpeg, v, s, p, output, part, passlog, toneMap, progress, cancel);
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    string label = attempt == 1 ? "" : string.Format(" (retry {0})", attempt - 1);
                    if (s.Nvenc)
                    {
                        Encode(ffmpeg, BaseArgs(v, s, p, toneMap) + NvencArgs(video) + AudioArgs(v, s, p) + Quote(part), p.Kept,
                               f => progress(f, "Encoding" + label), cancel);
                    }
                    else
                    {
                        Encode(ffmpeg, BaseArgs(v, s, p, toneMap) + X264Args(video) + string.Format(" -pass 1 -passlogfile {0} -an -f null NUL", Quote(passlog)), p.Kept,
                               f => progress(f * 0.35, "Analysing" + label), cancel);
                        Encode(ffmpeg, BaseArgs(v, s, p, toneMap) + X264Args(video) + string.Format(" -pass 2 -passlogfile {0}", Quote(passlog)) + AudioArgs(v, s, p) + Quote(part), p.Kept,
                               f => progress(0.35 + f * 0.65, "Encoding" + label), cancel);
                    }

                    long size = new FileInfo(part).Length;
                    if (size <= targetBytes)
                    {
                        File.Move(part, output);
                        progress(1, "Done");
                        return size;
                    }

                    double total = (video + p.AudioKbps) * (targetBytes * Usable / size);
                    video = (int)(total - p.AudioKbps);
                    if (video < minVideo) break;
                }
                throw new Exception("Couldn't get the file under the target size. Try a larger target or trim the video.");
            }
            finally
            {
                try { File.Delete(part); } catch { }
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(passlog), Path.GetFileName(passlog) + "*"))
                    try { File.Delete(f); } catch { }
            }
        }

        // One encode at constant quality, or at the bitrate picked by hand; the size is whatever it
        // comes out at, even bigger than the source. No retries: there's no target to hit.
        static long RunNoLimit(string ffmpeg, VideoInfo v, Settings s, Plan p, string output, string part, string passlog,
                               bool toneMap, Action<double, string> progress, CancelFlag cancel)
        {
            if (s.AutoBitrate)
                Encode(ffmpeg, BaseArgs(v, s, p, toneMap) + QualityArgs(s) + AudioArgs(v, s, p) + Quote(part), p.Kept,
                       f => progress(f, "Encoding"), cancel);
            else if (s.Nvenc)
                Encode(ffmpeg, BaseArgs(v, s, p, toneMap) + NvencArgs(p.VideoKbps) + AudioArgs(v, s, p) + Quote(part), p.Kept,
                       f => progress(f, "Encoding"), cancel);
            else
            {
                Encode(ffmpeg, BaseArgs(v, s, p, toneMap) + X264Args(p.VideoKbps) + string.Format(" -pass 1 -passlogfile {0} -an -f null NUL", Quote(passlog)), p.Kept,
                       f => progress(f * 0.35, "Analysing"), cancel);
                Encode(ffmpeg, BaseArgs(v, s, p, toneMap) + X264Args(p.VideoKbps) + string.Format(" -pass 2 -passlogfile {0}", Quote(passlog)) + AudioArgs(v, s, p) + Quote(part), p.Kept,
                       f => progress(0.35 + f * 0.65, "Encoding"), cancel);
            }
            long size = new FileInfo(part).Length;
            File.Move(part, output);
            progress(1, "Done");
            return size;
        }

        internal static string QualityArgs(Settings s)
        {
            return s.Nvenc
                ? string.Format(" -c:v h264_nvenc -preset p7 -tune hq -rc vbr -cq {0} -b:v 0 -multipass fullres", Cq)
                : string.Format(" -c:v libx264 -preset slow -crf {0}", Crf);
        }

        static string Quote(string path) { return " \"" + path + "\""; }

        // Internal so the tests can check the arguments.
        internal static string BaseArgs(VideoInfo v, Settings s, Plan p, bool toneMap)
        {
            var a = new StringBuilder("-hide_banner -loglevel error -nostats -nostdin -progress pipe:1 -y");
            if (s.TrimStart > 0) a.AppendFormat(CultureInfo.InvariantCulture, " -ss {0:0.###}", s.TrimStart);
            a.Append(" -i").Append(Quote(v.Path));
            a.AppendFormat(CultureInfo.InvariantCulture, " -t {0:0.###}", p.Kept);
            a.Append(" -map 0:v:0 -sn -dn");
            a.Append(" -vf \"");
            if (p.Fps < v.MaxFps) a.AppendFormat("fps={0},", p.Fps);
            // Works for landscape and for phone videos stored rotated.
            a.AppendFormat("scale='if(gte(iw,ih),-2,{0})':'if(gte(iw,ih),{0},-2)'", p.Short);
            // HDR (PQ or HLG) squeezed straight into yuv420p looks grey and washed out: map it to
            // SDR BT.709 first. After the scale, so the float maths runs on fewer pixels.
            if (toneMap)
                a.Append(",zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
            a.Append("\" -pix_fmt yuv420p");
            if (toneMap) a.Append(" -color_primaries bt709 -color_trc bt709 -colorspace bt709");
            return a.ToString();
        }

        static string X264Args(int video)
        {
            return string.Format(" -c:v libx264 -preset slow -b:v {0}k -maxrate {1}k -bufsize {2}k", video, video * 3 / 2, video * 2);
        }

        static string NvencArgs(int video)
        {
            return string.Format(" -c:v h264_nvenc -preset p7 -tune hq -rc vbr -multipass fullres -b:v {0}k -maxrate {1}k -bufsize {2}k", video, video * 3 / 2, video * 2);
        }

        internal static string AudioArgs(VideoInfo v, Settings s, Plan p)
        {
            string audio;
            if (p.AudioKbps == 0) audio = " -an";
            else if (v.AudioTracks > 1 && s.MixAudio)
            {
                // OBS puts the mic and the desktop on separate tracks; keeping only the first loses one.
                var mix = new StringBuilder(" -filter_complex \"");
                for (int i = 0; i < v.AudioTracks; i++) mix.AppendFormat("[0:a:{0}]", i);
                mix.AppendFormat("amix=inputs={0}:duration=longest:dropout_transition=0:normalize=0[a]\" -map \"[a]\"", v.AudioTracks);
                audio = mix.ToString() + string.Format(" -c:a aac -b:a {0}k", p.AudioKbps);
            }
            else audio = string.Format(" -map 0:a:0 -c:a aac -b:a {0}k", p.AudioKbps);
            return audio + " -movflags +faststart";
        }

        static void Encode(string ffmpeg, string args, double kept, Action<double> progress, CancelFlag cancel)
        {
            if (cancel.Cancelled) throw new CancelledException();
            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var errors = new Queue<string>();
            using (var proc = new Process { StartInfo = psi })
            {
                proc.OutputDataReceived += (o, e) =>
                {
                    long us;
                    if (e.Data != null && e.Data.StartsWith("out_time_ms=") && long.TryParse(e.Data.Substring(12), out us))
                        progress(Math.Max(0, Math.Min(1, us / 1000000.0 / kept)));
                };
                proc.ErrorDataReceived += (o, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (errors)
                    {
                        errors.Enqueue(e.Data);
                        if (errors.Count > 6) errors.Dequeue();
                    }
                };
                proc.Start();
                cancel.Current = proc;
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                cancel.Current = null;
                if (cancel.Cancelled) throw new CancelledException();
                if (proc.ExitCode != 0)
                {
                    lock (errors)
                        throw new Exception("ffmpeg failed:\n" + string.Join("\n", errors.ToArray()));
                }
            }
        }
    }
}
