using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TiaMcp.Mcp;

/// <summary>
/// A small always-on-top window showing what a long source tree call is doing - phase, item, count
/// and time - with a Cancel button, for clients that don't show MCP progress notifications. It
/// appears only when the call takes longer than ShowAfter, on its own UI thread, without taking
/// the focus, and closes when the call ends. Set TIAMCP_PROGRESS_WINDOW=0 to turn it off.
/// </summary>
internal sealed class ProgressWindow : IDisposable
{
    private static readonly TimeSpan ShowAfter = TimeSpan.FromSeconds(1.5);

    // What the window shows; written by ProgressLog, read by the window's timer.
    private readonly object _lock = new();
    private string _phase = "";
    private bool _cancellable;
    private string _item = "";
    private int _count;
    private int? _total;

    private readonly string _title;
    private readonly Action _cancel;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly ManualResetEventSlim _done = new();

    public static bool Enabled =>
        Environment.UserInteractive && Environment.GetEnvironmentVariable("TIAMCP_PROGRESS_WINDOW") is not ("0" or "false" or "False");

    // cancel: called (on the window's thread) when the Cancel button is clicked.
    public ProgressWindow(string title, Action cancel)
    {
        _title = title;
        _cancel = cancel;
        var thread = new Thread(Run) { IsBackground = true, Name = "TiaMcp progress window" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void Update(string phase, bool cancellable, string item, int count, int? total)
    {
        lock (_lock) { _phase = phase; _cancellable = cancellable; _item = item; _count = count; _total = total; }
    }

    public void Dispose() => _done.Set();

    private void Run()
    {
        // A short call ends before the window would show: no flash.
        if (_done.Wait(ShowAfter)) return;
        try
        {
            Application.EnableVisualStyles();
            using var form = new ProgressForm(this);
            Application.Run(form);
        }
        catch (Exception)
        {
            // The window is a courtesy: no desktop, or a UI failure, doesn't stop the call.
        }
    }

    private sealed class ProgressForm : Form
    {
        private readonly ProgressWindow _owner;
        private readonly Label _phase = new() { AutoSize = false, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), AutoEllipsis = true };
        private readonly Label _item = new() { AutoSize = false, AutoEllipsis = true };
        private readonly ProgressBar _bar = new() { MarqueeAnimationSpeed = 30 };
        private readonly Label _status = new() { AutoSize = false, AutoEllipsis = true, ForeColor = SystemColors.GrayText };
        private readonly Button _cancelButton = new() { Text = "Cancel" };
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
        private bool _cancelClicked;

        public ProgressForm(ProgressWindow owner)
        {
            _owner = owner;
            Text = $"TiaMcp - {owner._title}";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = true;
            ClientSize = new Size(440, 128);
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);

            _phase.SetBounds(12, 10, 416, 20);
            _item.SetBounds(12, 32, 416, 20);
            _bar.SetBounds(12, 56, 416, 18);
            _status.SetBounds(12, 94, 320, 20);
            _cancelButton.SetBounds(348, 90, 80, 26);
            Controls.AddRange(new Control[] { _phase, _item, _bar, _status, _cancelButton });

            _cancelButton.Click += (_, _) =>
            {
                _cancelClicked = true;
                _cancelButton.Enabled = false;
                _cancelButton.Text = "Cancelling";
                try { _owner._cancel(); } catch (Exception) { }
                UpdateView(this, EventArgs.Empty);
            };
            _timer.Tick += UpdateView;
            _timer.Start();
            UpdateView(this, EventArgs.Empty);
        }

        // Shown on top, but the focus stays where the user is working.
        protected override bool ShowWithoutActivation => true;

        // The call ends the window, not the user: closing it only hides it.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_owner._done.IsSet) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }

        // Any failure here would show WinForms' exception dialog: the window just closes instead.
        private void UpdateView(object? sender, EventArgs e)
        {
            try { ShowState(); }
            catch (Exception) { _timer.Stop(); Close(); }
        }

        private void ShowState()
        {
            if (_owner._done.IsSet) { _timer.Stop(); Close(); return; }

            string phase, item; bool cancellable; int count; int? total;
            lock (_owner._lock) { phase = _owner._phase; cancellable = _owner._cancellable; item = _owner._item; count = _owner._count; total = _owner._total; }

            _phase.Text = phase;
            _item.Text = item;
            if (total is > 0)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Maximum = total.Value;
                _bar.Value = Math.Min(count, total.Value);
            }
            else _bar.Style = ProgressBarStyle.Marquee;

            var elapsed = _owner._elapsed.Elapsed;
            var counted = count == 0 ? "" : total is > 0 ? $"{count} of {total}  -  " : $"{count} so far  -  ";
            _status.Text = _cancelClicked
                ? cancellable ? "Cancelling - nothing is changed" : "Cancelling after this item - what was written is read back"
                : $"{counted}{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
