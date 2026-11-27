using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LLPlayer.Services;

internal static class StudyClip
{
    internal static async Task<string> Prepare(ClipSource src, CancellationToken token)
    {
        var input = new FileInfo(src.Path);
        if (!input.Exists || input.Length != src.Bytes || input.LastWriteTimeUtc.Ticks != src.Modified)
            throw new InvalidDataException("视频源已变化或不存在，请重新打开并查词。");
        var duration = src.End-src.Start;
        if (src.Track < 0) throw new InvalidDataException("没有明确的内嵌英语轨。请在播放器选中有英语标签的正确音轨，再查词。");
        if (!double.IsFinite(src.Start) || !double.IsFinite(duration) || src.Start < 0 || duration is < .2 or > 30)
            throw new InvalidDataException("对白必须完整，且时长在 0.2 至 30 秒之间。请校正起止时间。");
        var path = Path.Combine(Path.GetTempPath(),"original-review-"+Guid.NewGuid().ToString("N")+".mp3");
        try
        {
            await Run(token,"-protocol_whitelist","file,pipe,crypto,data","-ss", Number(src.Start),"-i",input.FullName,
                "-map",$"0:{src.Track}","-t",Number(duration),"-vn","-sn","-dn","-map_metadata","-1","-map_chapters","-1",
                "-c:a","libmp3lame","-ac","1","-b:a","96k","-id3v2_version","0","-n",path);
            var pcm = await Run(token,"-i",path,"-ac","1","-ar","8000","-f","s16le","pipe:1");
            var hasSound = false;
            for (var i=0; i+1 < pcm.Length && !hasSound; i+=2) hasSound = Math.Abs((int)BitConverter.ToInt16(pcm,i)) >= 50;
            if (!hasSound || pcm.Length > 480000 || pcm.Length < (duration-.15)*16000)
                throw new InvalidDataException("原声过长、无声或不完整。请检查音轨及对白起止时间。");
            return path;
        }
        catch { ForgetPreview(path); throw; }
    }
    private static string Number(double value) => value.ToString("0.######",CultureInfo.InvariantCulture);
    internal static void ForgetPreview(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    internal static string Executable()
    {
        if (Environment.GetEnvironmentVariable("REVIEW_FFMPEG") is { Length: > 0 } configured) return configured;
        var home=Path.GetDirectoryName(typeof(StudyClip).Assembly.Location) ?? AppContext.BaseDirectory;
        var bundled=Path.Combine(home,"ClipTools","ffmpeg.exe");
        return File.Exists(bundled) ? bundled : "ffmpeg";
    }
    private static async Task<byte[]> Run(CancellationToken token, params string[] args)
    {
        var start = new ProcessStartInfo(Executable()) { UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (var arg in new[]{"-nostdin","-hide_banner","-loglevel","error","-xerror"}) start.ArgumentList.Add(arg);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var ffmpeg = Process.Start(start) ?? throw new InvalidDataException("无法启动 ffmpeg。");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancel.CancelAfter(TimeSpan.FromSeconds(30));
        using var kill = cancel.Token.Register(() => { try { if (!ffmpeg.HasExited) ffmpeg.Kill(); } catch (InvalidOperationException) { } });
        using var data = new MemoryStream();
        var stdout = ffmpeg.StandardOutput.BaseStream.CopyToAsync(data,cancel.Token);
        var stderr = ffmpeg.StandardError.ReadToEndAsync(cancel.Token);
        await Task.WhenAll(stdout,stderr,ffmpeg.WaitForExitAsync(cancel.Token));
        if (ffmpeg.ExitCode != 0) throw new InvalidDataException("无法截取对应原声。请检查英语轨和本地媒体，确认已安装 ffmpeg。");
        return data.ToArray();
    }
}
