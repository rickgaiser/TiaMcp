using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using ModelContextProtocol;

namespace TiaMcp.Mcp;

/// <summary>
/// What a long source tree call is doing while it runs: each phase and item goes out as an MCP
/// progress notification (shown by clients that ask for them), to the ProgressWindow, and as a
/// line in the tree's _progress.log (to follow with 'Get-Content _progress.log -Wait'), and the
/// time per phase ends up in the result.
///
/// Cancelling the call (notifications/cancelled, or the window's Cancel button) stops it at the next item of a cancellable phase
/// - one before anything is written to the project or the tree. In other phases the caller asks
/// Cancelled and stops starting new writes, so what was written is still read back into the tree.
/// </summary>
internal sealed class ProgressLog : IDisposable
{
    public const string FileName = "_progress.log";

    private readonly IProgress<ProgressNotificationValue>? _progress;
    private readonly CancellationTokenSource _cancel;
    private readonly ProgressWindow? _window;
    private readonly StreamWriter? _file;
    private readonly object _lock = new();
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Stopwatch _phaseTime = new();
    // Per phase name its time so far, in the order the phases started (a phase can run once per device).
    private readonly List<(string Name, TimeSpan Time)> _phases = new();
    private string? _phase;
    private bool _cancellable;
    private int _phaseItems;
    private int? _phaseTotal;
    // Monotonic, as MCP progress must be: every phase and item counts one.
    private int _steps;

    // directory: where _progress.log goes (null: no file); title: its first line.
    public ProgressLog(string? directory, string title, IProgress<ProgressNotificationValue>? progress, CancellationToken cancel)
    {
        _progress = progress;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        // The button can still be clicked while the call is ending.
        if (ProgressWindow.Enabled) _window = new ProgressWindow(title, () => { try { _cancel.Cancel(); } catch (ObjectDisposedException) { } });
        if (directory == null) return;
        try
        {
            // Shared, so it can be followed (and opened in an editor) while the call writes it.
            var stream = new FileStream(Path.Combine(directory, FileName), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _file = new StreamWriter(stream) { AutoFlush = true };
            // Whether the client asked for progress notifications (sent a progressToken): without
            // one the SDK binds a no-op.
            Line($"{title} (MCP progress notifications: {(progress == null || progress.GetType().Name == "NullProgress" ? "not requested by the client" : "sent")})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Progress is a courtesy: a locked or read-only tree doesn't stop the call.
        }
    }

    public bool Cancelled => _cancel.IsCancellationRequested;
    private void ThrowIfCancelled() => _cancel.Token.ThrowIfCancellationRequested();

    // Starts a phase, ending the previous one. total: its item count, when known up front.
    public void Phase(string name, bool cancellable = false, int? total = null)
    {
        lock (_lock)
        {
            EndPhase();
            _phase = name;
            _cancellable = cancellable;
            _phaseItems = 0;
            _phaseTotal = total;
            _phaseTime.Restart();
            Report(total == null ? name : $"{name} ({total})");
            _window?.Update(name, cancellable, "", 0, total);
        }
        if (cancellable) ThrowIfCancelled();
    }

    // One item of the current phase is about to be done.
    public void Item(string text)
    {
        lock (_lock)
        {
            _phaseItems++;
            Report($"{_phase} {_phaseItems}{(_phaseTotal == null ? "" : $"/{_phaseTotal}")}: {text}");
            _window?.Update(_phase ?? "", _cancellable, text, _phaseItems, _phaseTotal);
        }
        if (_cancellable) ThrowIfCancelled();
    }

    // One item of a part of the current phase that has its own count (e.g. the blocks of one PLC):
    // the index-th of total, about to be done.
    public void Item(string part, int index, int total, string text)
    {
        lock (_lock)
        {
            Report($"{_phase} {part} {index}/{total}: {text}");
            _window?.Update($"{_phase} - {part}", _cancellable, text, index, total);
        }
        if (_cancellable) ThrowIfCancelled();
    }

    // Something within the current phase that isn't an item (e.g. a compile between layers).
    public void Note(string text)
    {
        lock (_lock)
        {
            Report($"{_phase}: {text}");
            _window?.Update(_phase ?? "", _cancellable, text, _phaseItems, _phaseTotal);
        }
    }

    // "export 41 s, Multiuser marks 62 s": the time per phase so far.
    public string Timings
    {
        get
        {
            lock (_lock)
            {
                var phases = _phases.ToList();
                if (_phase != null) phases = Add(phases, _phase, _phaseTime.Elapsed);
                return string.Join(", ", phases.Select(p => $"{p.Name} {Seconds(p.Time)}"));
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            EndPhase();
            _phase = null;
            Line($"{(Cancelled ? "Cancelled" : "Done")} after {Seconds(_total.Elapsed)}: {string.Join(", ", _phases.Select(p => $"{p.Name} {Seconds(p.Time)}"))}");
            _file?.Dispose();
        }
        _window?.Dispose();
        _cancel.Dispose();
    }

    private void EndPhase()
    {
        if (_phase == null) return;
        _phaseTime.Stop();
        var phases = Add(_phases, _phase, _phaseTime.Elapsed);
        _phases.Clear();
        _phases.AddRange(phases);
    }

    private static List<(string Name, TimeSpan Time)> Add(List<(string Name, TimeSpan Time)> phases, string name, TimeSpan time)
    {
        var result = phases.ToList();
        var i = result.FindIndex(p => p.Name == name);
        if (i < 0) result.Add((name, time));
        else result[i] = (name, result[i].Time + time);
        return result;
    }

    private void Report(string message)
    {
        _steps++;
        _progress?.Report(new ProgressNotificationValue { Progress = _steps, Message = message });
        Line(message);
    }

    private void Line(string text)
    {
        try { _file?.WriteLine($"{DateTime.Now:HH:mm:ss}  {text}"); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static string Seconds(TimeSpan time) =>
        time.TotalSeconds.ToString(time.TotalSeconds < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";
}
