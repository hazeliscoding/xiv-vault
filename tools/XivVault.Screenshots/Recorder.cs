using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace XivVault.Screenshots;

/// <summary>
/// Captures a headless window as it runs, then turns the frames into a GIF with ffmpeg. Animations
/// run on real time, so every frame keeps the time it was captured and plays back at that pace.
/// </summary>
internal sealed class Recorder(Window window, string folder, int framesPerSecond)
{
    private readonly List<(string File, TimeSpan At)> _frames = [];
    private readonly List<Task> _saves = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _next;

    public void For(double seconds) => While(Task.CompletedTask, seconds);

    /// <summary>Records while <paramref name="work"/> runs, then <paramref name="secondsAfter"/> more.</summary>
    public void While(Task work, double secondsAfter)
    {
        var deadline = _clock.Elapsed + TimeSpan.FromSeconds(30);
        while (!work.IsCompleted && _clock.Elapsed < deadline)
        {
            Step();
        }

        if (work.IsFaulted)
        {
            throw new InvalidOperationException("A recorded step failed.", work.Exception);
        }

        var end = _clock.Elapsed + TimeSpan.FromSeconds(secondsAfter);
        while (_clock.Elapsed < end)
        {
            Step();
        }
    }

    public void WriteGif(string path, int width)
    {
        Task.WaitAll(_saves);

        // An ffconcat list gives each frame the time until the next one was captured.
        var list = new StringBuilder("ffconcat version 1.0\n");
        for (var i = 0; i < _frames.Count; i++)
        {
            var duration = i + 1 < _frames.Count ? _frames[i + 1].At - _frames[i].At : TimeSpan.FromSeconds(1.0 / framesPerSecond);
            list.Append(CultureInfo.InvariantCulture, $"file '{Path.GetFileName(_frames[i].File)}'\nduration {duration.TotalSeconds:0.###}\n");
        }

        var listPath = Path.Combine(folder, "frames.ffconcat");
        File.WriteAllText(listPath, list.ToString());

        // One palette for the whole clip, and only the changed rectangle re-encoded per frame,
        // which suits a UI where most of the screen stays still.
        var filter = $"scale={width}:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";
        var ffmpeg = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var argument in new[] { "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", listPath, "-vf", filter, "-fps_mode", "vfr", "-loop", "0", Path.GetFullPath(path) })
        {
            ffmpeg.ArgumentList.Add(argument);
        }

        using var process = Process.Start(ffmpeg) ?? throw new InvalidOperationException("ffmpeg could not be started. Install it and make sure it is on PATH.");
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed: {errors}");
        }

        var seconds = (_frames[^1].At - _frames[0].At).TotalSeconds;
        Console.WriteLine($"  {Path.GetFileName(path)}: {_frames.Count} frames, {seconds:0.0} s, {new FileInfo(path).Length / 1024} KB");
    }

    private void Step()
    {
        // Animations follow the clock, not render ticks, and capturing renders a frame itself, so
        // between frames the UI only needs its queued work run.
        Dispatcher.UIThread.RunJobs();
        if (_clock.Elapsed < _next)
        {
            Thread.Sleep(2);
            return;
        }

        var at = _clock.Elapsed;
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
        var file = Path.Combine(folder, $"{_frames.Count:D5}.png");

        // Rendering a frame takes about as long as encoding one, so encoding runs in the background.
        _saves.Add(Task.Run(() =>
        {
            using (frame)
            {
#pragma warning disable CS0618 // The replacement overload needs encoder options this tool has no use for.
                frame.Save(file);
#pragma warning restore CS0618
            }
        }));
        _frames.Add((file, at));
        _next = at + TimeSpan.FromSeconds(1.0 / framesPerSecond);
    }
}
