using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Emerald.Setup;

/// <summary>
/// The setup window: Welcome, Folders, Installing, Finished. Built in code - four pages of labels
/// and text boxes do not need a designer - and sized in font units so it scales with the screen.
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly Payload _payload;
    private readonly bool _edl;
    private readonly string _product;
    private readonly string _target;
    private readonly string _exe;

    private readonly Label _title = new() { AutoSize = false, Dock = DockStyle.Top, Height = 42 };
    private readonly Label _subtitle = new() { AutoSize = false, Dock = DockStyle.Top, Height = 24 };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(24, 12, 24, 12) };
    private readonly Button _back = new() { Text = "< Back", Width = 96 };
    private readonly Button _next = new() { Text = "Next >", Width = 96 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 96 };

    // Welcome
    private readonly CheckBox _driver = new() { AutoSize = true };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill };

    // Folders: high-res, low-res, database for the plant; the media folder for the EDL.
    private readonly List<FolderRow> _folders = new();

    // Installing and Finished
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 18 };
    private readonly Label _doing = new() { Dock = DockStyle.Top, Height = 26 };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f), BackColor = SystemColors.Window, WordWrap = false,
    };
    private readonly CheckBox _launch = new() { Text = "Start Emerald now", AutoSize = true, Checked = true };

    private int _page;
    private bool _driverDecided;
    private bool _installing;
    private readonly StringBuilder _output = new();

    public bool Succeeded { get; private set; }

    public SetupForm(Payload payload)
    {
        _payload = payload;
        _edl = string.Equals(payload["Flavour"], "edl", StringComparison.OrdinalIgnoreCase);
        _product = payload["Product"] is { Length: > 0 } p ? p : _edl ? "Emerald EDL" : "Emerald";
        _target = Path.Combine(Machine.ProgramFiles, _product);
        _exe = Path.Combine(_target, "Emerald.App.exe");
        if (_edl) _launch.Text = "Start Emerald EDL now";

        Text = $"{_product} setup";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(680, 480);
        MinimumSize = new Size(600, 420);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;

        _title.Font = new Font("Segoe UI Semibold", 15f);
        _title.Padding = new Padding(20, 8, 0, 0);
        _subtitle.Padding = new Padding(22, 0, 0, 0);
        _subtitle.ForeColor = SystemColors.GrayText;

        var header = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = SystemColors.Window };
        header.Controls.Add(_subtitle);
        header.Controls.Add(_title);

        var rule = new Label { Dock = DockStyle.Top, Height = 1, BackColor = SystemColors.ControlDark };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 10, 12, 10),
        };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_next);
        buttons.Controls.Add(_back);

        Controls.Add(_body);
        Controls.Add(rule);
        Controls.Add(header);
        Controls.Add(buttons);

        _back.Click += (_, _) => Show(_page - 1);
        _next.Click += (_, _) => Next();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _next;

        BuildFolders();
        Show(0);
    }

    // ------------------------------------------------------------------ pages

    private void Show(int page)
    {
        _page = page;
        _body.SuspendLayout();
        _body.Controls.Clear();

        switch (page)
        {
            case 0: ShowWelcome(); break;
            case 1: ShowFolders(); break;
            case 2: ShowInstalling(); break;
            default: ShowFinished(); break;
        }

        _body.ResumeLayout();
        _back.Visible = page == 1;
        _cancel.Visible = page < 2;
        _next.Text = page switch { 0 => "Next >", 1 => "Install", _ => "Finish" };
        _next.Enabled = page != 2;
    }

    private void ShowWelcome()
    {
        bool update = File.Exists(_exe);
        Version have = Machine.VersionOf(_exe);
        string version = _payload["Version"];

        _title.Text = update ? $"Update {_product}" : $"Install {_product}";
        _subtitle.Text = $"Version {version}, built {_payload["Built"]}";

        var lines = new List<string>
        {
            update
                ? $"{_product} {Machine.Show(have)} is installed in {_target}. This replaces the program; " +
                  "your settings, folders and recordings are kept."
                : $"{_product} will be installed in {_target}. The next page asks where recordings and the database go.",
            "",
        };

        // ---- DELTACAST
        Version installed = Machine.InstalledDeltacast();
        Version bundled = Version.TryParse(_payload["Deltacast"], out Version b) ? b : null;
        List<(string Name, string Status)> cards = Machine.DeltacastCards();

        lines.Add("DELTACAST");
        lines.Add(cards.Count == 0
            ? "   Card: none fitted. That is fine - fit it later with the machine switched off, and Windows sets it up when it starts."
            : "   Card: " + string.Join(", ", cards.Select(c => c.Status == "OK" ? c.Name : $"{c.Name} ({c.Status})")));
        lines.Add(installed is null
            ? "   Driver: not installed."
            : $"   Driver: {Machine.Show(installed)} installed.");

        if (_driverDecided)
        {
            // Asked once: coming back to this page keeps what was chosen.
        }
        else if (bundled is null)
        {
            _driver.Text = "This setup carries no DELTACAST driver";
            _driver.Checked = false;
            _driver.Enabled = false;
            if (installed is null)
                lines.Add("   Install it from DELTACAST before recording or transmitting.");
        }
        else if (installed is not null && installed >= bundled)
        {
            _driver.Text = $"DELTACAST driver {Machine.Show(installed)} is already installed - leave it as it is";
            _driver.Checked = false;
            _driver.Enabled = false;
        }
        else
        {
            _driver.Text = installed is null
                ? $"Install the DELTACAST driver {Machine.Show(bundled)}"
                : $"Update the DELTACAST driver to {Machine.Show(bundled)}";
            _driver.Checked = true;
            _driver.Enabled = true;
        }

        _driverDecided = true;

        if (Machine.EmeraldRunning())
        {
            lines.Add("");
            lines.Add($"Emerald is running. Close it before going on - its files cannot be replaced while it holds them open. " +
                      "If it is recording, stop the recording first.");
        }

        _status.Text = string.Join(Environment.NewLine, lines);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_driver, 0, 1);
        _body.Controls.Add(layout);
    }

    private void BuildFolders()
    {
        Dictionary<string, string> saved = Machine.SavedFolders();
        string best = Machine.BestDrive();

        string Saved(string key) => saved.TryGetValue(key, out string v) ? v : null;

        if (_edl)
        {
            _folders.Add(new FolderRow("Media folder", "Where the media the EDL plays lives - a local drive or a share.",
                Saved("captureFolder") ?? $@"{best}\Emerald\media", Saved("captureFolder") is not null));
            return;
        }

        string capture = Saved("captureFolder");
        string high = Saved("highResFolder") ?? (capture is null ? null : Path.Combine(capture, "high"));
        string low = Saved("lowResFolder") ?? (capture is null ? null : Path.Combine(capture, "low"));

        _folders.Add(new FolderRow("High-res folder", "The DNxHD masters - about 65 GB an hour.",
            high ?? $@"{best}\Emerald\media\high", high is not null));
        _folders.Add(new FolderRow("Low-res folder", "The proxies Live Edit plays. On another drive than the masters is best.",
            low ?? $@"{best}\Emerald\media\low", low is not null));
        _folders.Add(new FolderRow("Database folder", "The log of everything and the machine's figures while recording. Kept 14 days.",
            Saved("dataFolder") ?? Machine.DefaultDataFolder, Saved("dataFolder") is not null));
    }

    private void ShowFolders()
    {
        _title.Text = "Where things go";
        _subtitle.Text = _folders.Any(f => f.Locked)
            ? "Folders already chosen on this machine are kept; change them in Emerald."
            : "Recordings are never put in Program Files.";

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true };
        foreach (FolderRow row in _folders) layout.Controls.Add(row.Panel);
        _body.Controls.Add(layout);
    }

    private void ShowInstalling()
    {
        _title.Text = $"Installing {_product}";
        _subtitle.Text = "This takes a minute or two.";

        _body.Controls.Add(_log);
        _body.Controls.Add(_doing);
        _body.Controls.Add(_progress);
    }

    private void ShowFinished()
    {
        _title.Text = Succeeded ? $"{_product} is installed" : "The install did not complete";
        _subtitle.Text = Succeeded
            ? "What it found is below; anything marked [--] still needs doing."
            : "What went wrong is below. Nothing you had set up was removed.";

        _launch.Visible = Succeeded && !Machine.EmeraldRunning();
        _body.Controls.Add(_log);
        _body.Controls.Add(_launch);
        _launch.Dock = DockStyle.Bottom;
    }

    // ------------------------------------------------------------------ moving on

    private void Next()
    {
        switch (_page)
        {
            case 0:
                if (Machine.EmeraldRunning())
                {
                    MessageBox.Show(this, "Emerald is still running. Close it, then press Next again.", Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    ShowWelcome();
                    return;
                }
                Show(1);
                return;

            case 1:
                foreach (FolderRow row in _folders)
                {
                    if (row.Problem() is { } problem)
                    {
                        MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        row.Focus();
                        return;
                    }
                }
                Show(2);
                _ = Install();
                return;

            default:
                if (Succeeded && _launch.Visible && _launch.Checked) Launch();
                Close();
                return;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installing)
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ the install

    private async Task Install()
    {
        _installing = true;
        string work = Path.Combine(Path.GetTempPath(), "EmeraldSetup-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        try
        {
            _doing.Text = "Unpacking ...";
            _progress.Maximum = 1000;
            long total = Math.Max(1, _payload.Bytes);

            await Task.Run(() => _payload.ExtractTo(work, done =>
            {
                int at = (int)(done * 1000 / total);
                if (at != _progress.Value) BeginInvoke(new Action(() => _progress.Value = Math.Min(1000, at)));
            }));

            _progress.Value = 1000;
            _doing.Text = "Installing ...";
            _progress.Style = ProgressBarStyle.Marquee;

            int code = await Task.Run(() => RunInstall(work));
            Succeeded = code == 0;

            _progress.Style = ProgressBarStyle.Continuous;
            _doing.Text = Succeeded ? "Done." : $"install.bat stopped with code {code}.";
        }
        catch (Exception ex)
        {
            Succeeded = false;
            Append($"[ERROR] {ex.Message}");
        }
        finally
        {
            SaveLog();
            try { Directory.Delete(work, true); } catch { /* Windows' temp clean-up will get it */ }
            _installing = false;
        }

        // Only the summary on the last page: the copying lines above it are in the log file.
        string text = _output.ToString();
        int summary = text.IndexOf(" Installed.", StringComparison.Ordinal);
        _log.Text = Succeeded && summary > 0 ? text.Substring(summary).TrimStart() : text;

        Show(3);
    }

    private int RunInstall(string folder)
    {
        string bat = Path.Combine(folder, "install.bat");
        var args = new StringBuilder();

        foreach (FolderRow row in _folders) args.Append(Quote(row.Value)).Append(' ');
        args.Append("-q");
        if (!_driver.Checked) args.Append(" -nodriver");

        // cmd /s /c "<command line>": the outer quotes are stripped and the rest is run as written.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/s /c \"{Quote(bat)} {args}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder,
            StandardOutputEncoding = Console(),
            StandardErrorEncoding = Console(),
        };

        Append($"> install.bat {args}");
        Append("");

        using Process process = Process.Start(start);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>cmd writes in the console's code page, not in UTF-8.</summary>
    private static Encoding Console()
    {
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { return Encoding.Default; }
    }

    private static string Quote(string s) => "\"" + s.TrimEnd('\\') + "\"";

    private void Append(string line)
    {
        lock (_output) _output.AppendLine(line);

        if (InvokeRequired) BeginInvoke(new Action(() => AppendToBox(line)));
        else AppendToBox(line);
    }

    private void AppendToBox(string line)
    {
        _log.AppendText(line + Environment.NewLine);
    }

    /// <summary>
    /// Kept beside the program, so the person who rings up about it can be asked to read it.
    /// In ProgramData if the program folder was never made.
    /// </summary>
    private void SaveLog()
    {
        string name = $"setup-{DateTime.Now:yyyyMMdd-HHmmss}.log";

        foreach (string folder in new[] { _target, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Emerald") })
        {
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, name);
                lock (_output) File.WriteAllText(path, _output.ToString());
                Append("");
                Append($"This log: {path}");
                return;
            }
            catch
            {
                // the next one
            }
        }
    }

    /// <summary>
    /// Started through Explorer, so it runs as the person logged in and not as the administrator
    /// this setup was elevated to. An elevated Emerald would also be refused files dragged in
    /// from Explorer.
    /// </summary>
    private void Launch()
    {
        try
        {
            if (_edl)
            {
                // Explorer passes no arguments, so the EDL goes by its shortcut, which carries --edl.
                string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    _product, $"{_product}.lnk");
                Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    Quote(File.Exists(shortcut) ? shortcut : _exe));
                return;
            }

            Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Quote(_exe));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Emerald is installed, but could not be started from here: {ex.Message}\n\n" +
                                  "Start it from the Start Menu.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ------------------------------------------------------------------ a folder question

    private sealed class FolderRow
    {
        private readonly TextBox _box = new() { Dock = DockStyle.Fill };
        private readonly Label _free = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly string _name;

        public Panel Panel { get; }
        public bool Locked { get; }
        public string Value => _box.Text.Trim();

        public FolderRow(string name, string hint, string value, bool locked)
        {
            _name = name;
            Locked = locked;
            _box.Text = value;
            _box.ReadOnly = locked;

            var label = new Label { Text = name, AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f) };
            var help = new Label { Text = locked ? "Already set on this machine - kept." : hint, AutoSize = true, ForeColor = SystemColors.GrayText };
            var browse = new Button { Text = "Browse...", Width = 90, Enabled = !locked };

            browse.Click += (_, _) =>
            {
                using var dialog = new FolderBrowserDialog { Description = name, ShowNewFolderButton = true };
                string existing = ExistingParent(Value);
                if (existing is not null) dialog.SelectedPath = existing;
                if (dialog.ShowDialog() == DialogResult.OK) _box.Text = dialog.SelectedPath;
            };

            _box.TextChanged += (_, _) => _free.Text = Machine.FreeSpace(Value);
            _free.Text = Machine.FreeSpace(Value);

            var line = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Height = 30 };
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            line.Controls.Add(_box, 0, 0);
            line.Controls.Add(browse, 1, 0);

            Panel = new Panel { Width = 620, Height = 98, Margin = new Padding(0, 0, 0, 6) };
            Panel.Controls.Add(_free);
            Panel.Controls.Add(line);
            Panel.Controls.Add(help);
            Panel.Controls.Add(label);
            _free.Dock = DockStyle.Top;
            line.Dock = DockStyle.Top;
            help.Dock = DockStyle.Top;
            label.Dock = DockStyle.Top;
        }

        public void Focus() => _box.Focus();

        /// <summary>Why this folder will not do, or null.</summary>
        public string Problem()
        {
            if (Locked) return null;
            string value = Value;

            if (value.Length == 0) return $"{_name}: choose a folder.";
            if (!Path.IsPathRooted(value) || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || value.IndexOf('"') >= 0)
                return $"{_name}: \"{value}\" is not a full folder path, like D:\\Emerald\\media.";

            string full;
            try { full = Path.GetFullPath(value).TrimEnd('\\'); }
            catch (Exception ex) { return $"{_name}: {ex.Message}"; }

            if (full.Length <= 2) return $"{_name}: choose a folder on the drive, not the whole drive.";

            foreach (string forbidden in new[] { Machine.ProgramFiles, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            {
                if (forbidden.Length > 0 && (full + "\\").StartsWith(forbidden.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    return $"{_name}: not under {forbidden} - a standard user cannot write there.";
            }

            return null;
        }

        private static string ExistingParent(string path)
        {
            try
            {
                string at = Path.GetFullPath(path);
                while (!string.IsNullOrEmpty(at) && !Directory.Exists(at)) at = Path.GetDirectoryName(at);
                return at;
            }
            catch
            {
                return null;
            }
        }
    }
}
