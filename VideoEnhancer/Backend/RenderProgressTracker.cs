using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoEnhancer;

// FPS 从第一条进度后的新增帧计算，并排除暂停时间；每个宿主步骤拥有独立实例。
internal sealed class RenderProgressTracker(TimeProvider? timeProvider = null)
{
    private static readonly Regex FramePattern = new(@"FPS:\s*([\d.]+)\s+Current Frame:\s*(\d+)\s+ETA:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex TotalPattern = new(@"Total Output Frames:\s*(\d+)", RegexOptions.Compiled);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private long? _firstStamp;
    private long _firstFrame, _totalFrames;
    private long? _pauseStamp;
    private TimeSpan _paused, _firstPaused;

    internal void SetPaused(bool paused)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (paused && _pauseStamp is null) _pauseStamp = now;
            else if (!paused && _pauseStamp is long start)
            {
                _paused += _clock.GetElapsedTime(start, now);
                _pauseStamp = null;
            }
        }
    }

    internal string RewriteLine(string line)
    {
        lock (_gate)
        {
            var total = TotalPattern.Match(line);
            if (total.Success && long.TryParse(total.Groups[1].Value, out var count)) _totalFrames = count;
            var match = FramePattern.Match(line);
            if (!match.Success || !long.TryParse(match.Groups[2].Value, out var frame)) return line;
            var now = _clock.GetTimestamp();
            var paused = _paused + (_pauseStamp is long start ? _clock.GetElapsedTime(start, now) : TimeSpan.Zero);
            if (_firstStamp is null || frame < _firstFrame)
            {
                _firstStamp = now; _firstFrame = frame; _firstPaused = paused;
                return line;
            }
            var active = _clock.GetElapsedTime(_firstStamp.Value, now) - (paused - _firstPaused);
            long measured = frame - _firstFrame;
            if (active <= TimeSpan.Zero || measured <= 0) return line;
            var fps = measured / active.TotalSeconds;
            string eta = match.Groups[3].Value;
            if (_totalFrames > frame)
            {
                var remaining = TimeSpan.FromSeconds(Math.Ceiling((_totalFrames - frame) / fps));
                eta = ((long)remaining.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + remaining.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
            }
            string rewritten = $"FPS: {fps.ToString("F2", CultureInfo.InvariantCulture)} Current Frame: {frame} ETA: {eta}";
            return line[..match.Index] + rewritten + line[(match.Index + match.Length)..];
        }
    }
}
