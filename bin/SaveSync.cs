// SaveSync.exe - controller- and keyboard-navigable push/pull for ludusavi saves.
// Created by Claude 2026-09-18. V3 (2026-09-22): multi-game, driven by games.conf.
// Built with csc.exe, no external dependencies.
//
//   (no args)                 the window
//   --toast  <mode> <key>     do the work, flash a notification, exit  (Steam wrapper)
//   --silent <mode> <key>     do the work with no window at all        (scheduled task)
//
// mode is backup | restore. key is a short name from games.conf:
//   %APPDATA%\savesync\games.conf     <key>|<exact ludusavi game name>
//
// Navigation: D-pad / left stick / WASD / arrow keys move, A or Enter activates,
// B or Esc goes back. Anything that overwrites a live save asks first.
//
// Nothing here targets a machine. ludusavi writes to C:\SaveSync\ludusavi and
// Syncthing replicates that folder to every connected peer (PC, Deck, Pi).
//
// Paths use forward slashes on purpose - Windows accepts them, and it keeps the
// source free of backslash escapes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Xml;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// ---------------------------------------------------------------- gamepad
public static class Pad
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STATE { public uint dwPacketNumber; public GAMEPAD Gamepad; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    static extern uint Get14(uint i, ref STATE s);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    static extern uint Get910(uint i, ref STATE s);

    static bool legacy = false;

    public const ushort UP = 0x0001, DOWN = 0x0002, LEFT = 0x0004, RIGHT = 0x0008,
                        A = 0x1000, B = 0x2000;

    public static ushort Read()
    {
        STATE st = new STATE();
        bool found = false;
        for (uint i = 0; i < 4; i++)
        {
            uint rc = 1;
            if (!legacy)
            {
                try { rc = Get14(i, ref st); }
                catch (DllNotFoundException) { legacy = true; }
                catch (EntryPointNotFoundException) { legacy = true; }
            }
            if (legacy)
            {
                try { rc = Get910(i, ref st); } catch { rc = 1; }
            }
            if (rc == 0) { found = true; break; }
        }
        if (!found) return 0;

        ushort b = st.Gamepad.wButtons;
        if (st.Gamepad.sThumbLY > 16000) b |= UP;
        if (st.Gamepad.sThumbLY < -16000) b |= DOWN;
        if (st.Gamepad.sThumbLX > 16000) b |= RIGHT;
        if (st.Gamepad.sThumbLX < -16000) b |= LEFT;
        return b;
    }
}

// ---------------------------------------------------------------- model
public class Game
{
    public string Key;    // spiderman
    public string Name;   // Marvel's Spider-Man Remastered
}

public class Restore
{
    public string Id;     // backup-20260921T122254Z
    public string When;   // 2026-09-21T07:22:54
    public string Os;     // Windows / Linux

    public int Files;     // measured from disk - ludusavi reports neither
    public long Bytes;

    public string Size()
    {
        double n = Bytes;
        string[] units = { "B", "KiB", "MiB", "GiB" };
        int i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return (i == 0) ? (Bytes + " B") : (n.ToString("0.00") + " " + units[i]);
    }

    public string Pretty()
    {
        return When.Replace("T", "  ") + "   [" + Os + "]";
    }

    // The line shown in the picker: date, which machine, how much.
    public string Line()
    {
        return Pretty() + "   -   " + Files + " files, " + Size();
    }
}

public static class Sync
{
    public const string Script = "C:/SaveSync/bin/savesync.ps1";

    public static string ConfPath()
    {
        string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appdata, "savesync", "games.conf");
    }

    // The registry of games, shared with the Deck (same format, same keys).
    public static List<Game> Games()
    {
        List<Game> list = new List<Game>();
        try
        {
            foreach (string raw in File.ReadAllLines(ConfPath()))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int bar = line.IndexOf('|');
                if (bar <= 0) continue;
                Game g = new Game();
                g.Key = line.Substring(0, bar).Trim();
                g.Name = line.Substring(bar + 1).Trim();
                if (g.Key.Length > 0 && g.Name.Length > 0) list.Add(g);
            }
        }
        catch { }
        return list;
    }

    // Syncthing normally notices a change via its filesystem watcher, but that can
    // lag (and the Deck runs inside a flatpak sandbox where inotify is less
    // reliable). Poking the local instance to rescan makes a push propagate at once
    // instead of whenever the watcher or the hourly rescan gets round to it.
    public static string Rescan()
    {
        try
        {
            string cfg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Syncthing", "config.xml");
            if (!File.Exists(cfg)) return "no syncthing config";

            XmlDocument doc = new XmlDocument();
            doc.Load(cfg);
            XmlNode node = doc.SelectSingleNode("//gui/apikey");
            if (node == null) return "no api key";

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(
                "http://127.0.0.1:8384/rest/db/scan?folder=savesync-ludusavi");
            req.Method = "POST";
            req.Headers.Add("X-API-Key", node.InnerText);
            req.ContentLength = 0;
            req.Timeout = 8000;
            using (WebResponse resp = req.GetResponse()) { }
            return "ok";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string Raw(string mode, string key, string extra, out int code)
    {
        string text;
        code = -1;
        try
        {
            string q = "\"";
            string args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "
                        + q + Script + q + " " + mode + " " + key;
            if (extra != null && extra.Length > 0) args += " " + extra;

            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            // ludusavi writes UTF-8; without this .NET decodes it as the OEM
            // codepage and a delta sign arrives as mojibake in the log.
            psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
            psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
            Process p = Process.Start(psi);
            text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            code = p.ExitCode;
        }
        catch (Exception ex) { text = ex.Message; }
        return text;
    }

    public static string Summarise(string text)
    {
        // The dispatcher refuses to back up a save that is older than the newest
        // restore point. Surface that verbatim - reporting it as "0 games" reads
        // like a failure when it is the guard doing its job.
        foreach (string line in text.Replace("\r", "").Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("savesync: SKIPPED"))
            {
                // Keep the dates - they are the evidence for why nothing happened.
                string detail = t.Substring("savesync: SKIPPED".Length).Trim();
                return "NOTHING TO PUSH - this PC is behind."
                     + Environment.NewLine + Environment.NewLine
                     + detail
                     + Environment.NewLine + Environment.NewLine
                     + "Your live save here is older than the newest restore point, so "
                     + "pushing it would make old progress look like the newest save. "
                     + "Use PULL to bring the newer save here, or RESTORE AN OLDER SAVE "
                     + "to pick a specific one.";
            }
            if (t.StartsWith("savesync: nothing to back up"))
                return "NOTHING TO PUSH - this PC is behind. Pull first.";
        }

        string games = "", size = "";
        foreach (string line in text.Replace("\r", "").Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("Games:")) games = t.Substring(6).Trim();
            if (t.StartsWith("Size:")) size = t.Substring(5).Trim();
        }
        if (games.Length > 0 || size.Length > 0)
            return games + (size.Length > 0 ? "  -  " + size : "");
        return text.Trim();
    }

    public static int Run(string mode, string key, string extra, out string summary)
    {
        int code;
        string text = Raw(mode, key, extra, out code);
        summary = Summarise(text);
        if (code == 0) Rescan();     // push it out now, do not wait for the watcher
        return code;
    }

    // Parses:   - "backup-20260921T122254Z" (2026-09-21T07:22:54) [Windows]
    public static List<Restore> List(string key)
    {
        List<Restore> list = new List<Restore>();
        int code;
        string text = Raw("backups", key, null, out code);
        // ludusavi prints the folder its backups live in - use it to measure each
        // restore point, since neither the text nor the API reports size/count.
        string root = "";
        Match fm = Regex.Match(text, @"^\s*Folder:\s*(.+)$", RegexOptions.Multiline);
        if (fm.Success) root = fm.Groups[1].Value.Trim();

        Regex re = new Regex("- \"(?<id>[^\"]+)\"\\s+\\((?<when>[^)]+)\\)\\s+\\[(?<os>[^\\]]+)\\]");
        foreach (Match m in re.Matches(text))
        {
            Restore r = new Restore();
            r.Id = m.Groups["id"].Value;
            r.When = m.Groups["when"].Value;
            r.Os = m.Groups["os"].Value;
            if (root.Length > 0)
            {
                try
                {
                    DirectoryInfo di = new DirectoryInfo(Path.Combine(root, r.Id));
                    if (di.Exists)
                    {
                        foreach (FileInfo fi in di.GetFiles("*", SearchOption.AllDirectories))
                        {
                            r.Files++;
                            r.Bytes += fi.Length;
                        }
                    }
                }
                catch { }
            }
            list.Add(r);
        }
        list.Reverse();          // newest first
        return list;
    }
}

// ---------------------------------------------------------------- shared UI
public static class Ui
{
    public static Button MakeButton(string text, int y)
    {
        Button b = new Button();
        b.Text = text;
        b.Location = new Point(20, y);
        b.Size = new Size(520, 62);
        b.Font = new Font("Segoe UI", 13, FontStyle.Bold);
        b.FlatStyle = FlatStyle.Flat;
        b.ForeColor = Color.White;
        b.BackColor = Color.FromArgb(44, 48, 56);
        b.FlatAppearance.BorderSize = 3;
        b.FlatAppearance.BorderColor = Color.FromArgb(44, 48, 56);
        b.TextAlign = ContentAlignment.MiddleLeft;
        b.Padding = new Padding(16, 0, 0, 0);
        b.TabStop = false;
        return b;
    }

    public static void Highlight(Button[] items, int sel)
    {
        for (int i = 0; i < items.Length; i++)
        {
            bool on = (i == sel);
            items[i].FlatAppearance.BorderColor = on ? Color.FromArgb(90, 190, 255) : Color.FromArgb(44, 48, 56);
            items[i].BackColor = on ? Color.FromArgb(60, 70, 86) : Color.FromArgb(44, 48, 56);
        }
    }
}

// ---------------------------------------------------------------- main window
public class SaveSyncForm : Form
{
    List<Game> games;
    int gameIdx = 0;

    Button gameBtn, pushBtn, pullBtn, histBtn, refreshBtn, quitBtn;
    Button[] items;
    int sel = 0;
    TextBox output;
    System.Windows.Forms.Timer padTimer;
    System.Windows.Forms.Timer autoTimer;
    ushort lastButtons = 0;
    bool busy = false;

    string Key { get { return games.Count > 0 ? games[gameIdx].Key : ""; } }
    string GameName { get { return games.Count > 0 ? games[gameIdx].Name : "(no games registered)"; } }

    public SaveSyncForm()
    {
        games = Sync.Games();

        Text = "SaveSync";
        ClientSize = new Size(560, 628);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 26, 31);
        ForeColor = Color.White;
        KeyPreview = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        gameBtn = Ui.MakeButton("", 20);
        gameBtn.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        gameBtn.Click += delegate { NextGame(); };

        pushBtn = Ui.MakeButton("PUSH      back this PC's save up", 90);
        pushBtn.Click += delegate { Go("backup", "Push", null, false); };

        pullBtn = Ui.MakeButton("PULL      bring the synced save here", 158);
        pullBtn.Click += delegate { Go("restore", "Pull", null, true); };

        histBtn = Ui.MakeButton("RESTORE AN OLDER SAVE ...", 226);
        histBtn.Click += delegate { OpenHistory(); };

        refreshBtn = Ui.MakeButton("REFRESH   check for new saves now", 294);
        refreshBtn.Click += delegate { ForceRefresh(); };

        quitBtn = Ui.MakeButton("CLOSE", 362);
        quitBtn.Click += delegate { Close(); };

        items = new Button[] { gameBtn, pushBtn, pullBtn, histBtn, refreshBtn, quitBtn };

        output = new TextBox();
        output.Multiline = true;
        output.ReadOnly = true;
        output.ScrollBars = ScrollBars.Vertical;
        output.BackColor = Color.FromArgb(16, 17, 20);
        output.ForeColor = Color.FromArgb(180, 220, 180);
        output.BorderStyle = BorderStyle.FixedSingle;
        output.Font = new Font("Consolas", 10);
        output.Location = new Point(20, 440);
        output.Size = new Size(520, 130);
        output.TabStop = false;
        output.Text = "Reading backup history ...";

        Label hint = new Label();
        hint.Text = "D-pad / stick / WASD / arrows move   -   A or Enter selects   -   B or Esc closes"
                  + "\r\nSaves go to the Pi and any other machine that is online - neither needs the other awake.";
        hint.ForeColor = Color.FromArgb(140, 145, 155);
        hint.Font = new Font("Segoe UI", 8.5f);
        hint.Location = new Point(20, 576);
        hint.Size = new Size(520, 40);

        Controls.AddRange(new Control[] { gameBtn, pushBtn, pullBtn, histBtn, quitBtn, output, hint });

        KeyDown += OnKey;
        foreach (Button b in items) b.KeyDown += OnKey;

        padTimer = new System.Windows.Forms.Timer();
        padTimer.Interval = 80;
        padTimer.Tick += OnPad;
        padTimer.Start();

        // The list used to be read once at open, so a save pushed from the Deck
        // while this window sat there never appeared. Re-read it periodically.
        autoTimer = new System.Windows.Forms.Timer();
        autoTimer.Interval = 20000;
        autoTimer.Tick += delegate { if (!busy) LoadHistoryLine(true); };
        autoTimer.Start();

        RefreshGameLabel();
        Ui.Highlight(items, sel);
        Shown += delegate { LoadHistoryLine(); };
    }

    void RefreshGameLabel()
    {
        if (games.Count == 0)
        {
            gameBtn.Text = "NO GAMES IN games.conf";
        }
        else
        {
            string more = games.Count > 1 ? "   (A to change, " + games.Count + " games)" : "";
            gameBtn.Text = "GAME:  " + GameName + more;
        }
    }

    void NextGame()
    {
        if (games.Count < 2) return;
        gameIdx = (gameIdx + 1) % games.Count;
        RefreshGameLabel();
        LoadHistoryLine();
    }

    // Shows when the newest restore point was written, and by which machine.
    // Ask the local Syncthing to rescan, then re-read the list. This is what the
    // REFRESH button does: it turns "wait for the watcher" into "look right now".
    void ForceRefresh()
    {
        if (busy) return;
        output.Text = "Refreshing ...";
        Refresh();
        Thread t = new Thread(delegate()
        {
            string r = Sync.Rescan();
            try { Invoke((MethodInvoker)delegate { LoadHistoryLine(false, r); }); } catch { }
        });
        t.IsBackground = true;
        t.Start();
    }

    void LoadHistoryLine() { LoadHistoryLine(false, null); }
    void LoadHistoryLine(bool quiet) { LoadHistoryLine(quiet, null); }

    void LoadHistoryLine(bool quiet, string rescanResult)
    {
        if (games.Count == 0)
        {
            output.Text = "No games registered.\r\n\r\nAdd one line to:\r\n" + Sync.ConfPath()
                        + "\r\n\r\n    <key>|<exact ludusavi game name>";
            return;
        }
        string key = Key, name = GameName;
        if (!quiet) output.Text = "Reading backup history for " + key + " ...";
        Thread t = new Thread(delegate()
        {
            List<Restore> list = Sync.List(key);
            string msg;
            if (list.Count == 0)
                msg = name + "\r\nNo backups yet for key '" + key + "'.";
            else
                msg = name + "\r\nLast push: " + list[0].Line() + "\r\n"
                    + list.Count + " restore point(s) kept.";
            try { Invoke((MethodInvoker)delegate { output.Text = msg; }); } catch { }
        });
        t.IsBackground = true;
        t.Start();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.W: case Keys.Up:    Step(-1); e.Handled = true; break;
            case Keys.S: case Keys.Down:  Step(1);  e.Handled = true; break;
            case Keys.A: case Keys.Left:  Step(-1); e.Handled = true; break;
            case Keys.D: case Keys.Right: Step(1);  e.Handled = true; break;
            case Keys.Enter: case Keys.Space: Fire(); e.Handled = true; break;
            case Keys.Escape: Close(); e.Handled = true; break;
        }
        e.SuppressKeyPress = e.Handled;
    }

    void Step(int d) { sel = (sel + d + items.Length) % items.Length; Ui.Highlight(items, sel); }
    void Fire() { if (!busy) items[sel].PerformClick(); }

    void OnPad(object sender, EventArgs e)
    {
        ushort b = Pad.Read();
        ushort pressed = (ushort)(b & ~lastButtons);   // edge-triggered
        lastButtons = b;
        if ((pressed & Pad.UP) != 0) Step(-1);
        if ((pressed & Pad.DOWN) != 0) Step(1);
        if ((pressed & Pad.A) != 0) Fire();
        if ((pressed & Pad.B) != 0) Close();
    }

    void OpenHistory()
    {
        if (games.Count == 0) return;
        padTimer.Stop();
        output.Text = "Loading restore points ...";
        Refresh();

        List<Restore> list = Sync.List(Key);
        if (list.Count == 0)
        {
            output.Text = "No restore points found for " + Key + ".";
            lastButtons = 0;
            padTimer.Start();
            return;
        }

        HistoryForm h = new HistoryForm(list, GameName);
        h.ShowDialog(this);

        if (h.Chosen != null)
            Go("restore", "Restore " + h.Chosen.When, "--backup " + h.Chosen.Id, false);
        else
            LoadHistoryLine();

        lastButtons = 0;
        padTimer.Start();
    }

    void Go(string mode, string label, string extra, bool confirm)
    {
        if (busy || games.Count == 0) return;

        if (confirm)
        {
            DialogResult r = MessageBox.Show(
                "This OVERWRITES this PC's live save for:\n\n    " + GameName + "\n\nContinue?",
                "SaveSync - confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
        }

        busy = true;
        string key = Key;
        output.Text = label + " " + key + " ...";
        padTimer.Stop();

        Thread t = new Thread(delegate()
        {
            string summary;
            int code = Sync.Run(mode, key, extra, out summary);
            string final = label + (code == 0 ? " complete." : " FAILED (exit " + code + ").")
                         + "\r\n\r\n" + summary;
            try
            {
                Invoke((MethodInvoker)delegate
                {
                    output.Text = final;
                    busy = false;
                    lastButtons = 0;
                    padTimer.Start();
                });
            }
            catch { }
        });
        t.IsBackground = true;
        t.Start();
    }
}

// ---------------------------------------------------------------- history picker
public class HistoryForm : Form
{
    public Restore Chosen = null;

    Button[] items;
    List<Restore> points;
    int sel = 0;
    System.Windows.Forms.Timer padTimer;
    ushort lastButtons = 0;

    public HistoryForm(List<Restore> list, string gameName)
    {
        points = list;

        Text = "SaveSync - restore an older save";
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(24, 26, 31);
        ForeColor = Color.White;
        KeyPreview = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        Label head = new Label();
        head.Text = gameName + "\r\nNewest first. Choosing one overwrites the live save on this PC.";
        head.ForeColor = Color.FromArgb(200, 205, 215);
        head.Font = new Font("Segoe UI", 9.5f);
        head.Location = new Point(20, 12);
        head.Size = new Size(520, 38);
        Controls.Add(head);

        List<Button> buttons = new List<Button>();
        int y = 58;
        for (int i = 0; i < points.Count; i++)
        {
            string tag = (i == 0) ? "NEWEST  " : "        ";
            Button b = Ui.MakeButton(tag + points[i].Line(), y);
            b.Height = 52;
            b.Font = new Font("Consolas", 11, FontStyle.Bold);
            int captured = i;
            b.Click += delegate { Pick(captured); };
            b.KeyDown += OnKey;
            buttons.Add(b);
            Controls.Add(b);
            y += 58;
        }

        Button back = Ui.MakeButton("BACK   (change nothing)", y + 6);
        back.Height = 52;
        back.Click += delegate { Chosen = null; Close(); };
        back.KeyDown += OnKey;
        buttons.Add(back);
        Controls.Add(back);

        items = buttons.ToArray();
        ClientSize = new Size(560, y + 76);

        KeyDown += OnKey;

        padTimer = new System.Windows.Forms.Timer();
        padTimer.Interval = 80;
        padTimer.Tick += OnPad;
        padTimer.Start();

        FormClosed += delegate { padTimer.Stop(); };

        Ui.Highlight(items, sel);
    }

    void Pick(int i)
    {
        Restore r = points[i];
        DialogResult d = MessageBox.Show(
            "Restore the save from:\n\n    " + r.Line() + "\n\n"
            + "This OVERWRITES the live save on this PC.\n"
            + "Your other restore points are not deleted.",
            "SaveSync - confirm restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (d != DialogResult.Yes) return;
        Chosen = r;
        Close();
    }

    void Step(int d) { sel = (sel + d + items.Length) % items.Length; Ui.Highlight(items, sel); }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.W: case Keys.Up:    Step(-1); e.Handled = true; break;
            case Keys.S: case Keys.Down:  Step(1);  e.Handled = true; break;
            case Keys.A: case Keys.Left:  Step(-1); e.Handled = true; break;
            case Keys.D: case Keys.Right: Step(1);  e.Handled = true; break;
            case Keys.Enter: case Keys.Space: items[sel].PerformClick(); e.Handled = true; break;
            case Keys.Escape: Chosen = null; Close(); e.Handled = true; break;
        }
        e.SuppressKeyPress = e.Handled;
    }

    void OnPad(object sender, EventArgs e)
    {
        ushort b = Pad.Read();
        ushort pressed = (ushort)(b & ~lastButtons);
        lastButtons = b;
        if ((pressed & Pad.UP) != 0) Step(-1);
        if ((pressed & Pad.DOWN) != 0) Step(1);
        if ((pressed & Pad.A) != 0) items[sel].PerformClick();
        if ((pressed & Pad.B) != 0) { Chosen = null; Close(); }
    }
}

// ---------------------------------------------------------------- toast
public class ToastForm : Form
{
    public ToastForm(string title, string body, bool ok)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 26, 31);
        ClientSize = new Size(430, 96);

        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - 24, wa.Bottom - Height - 24);

        Panel stripe = new Panel();
        stripe.BackColor = ok ? Color.FromArgb(90, 200, 120) : Color.FromArgb(230, 90, 90);
        stripe.Location = new Point(0, 0);
        stripe.Size = new Size(6, ClientSize.Height);

        Label t = new Label();
        t.Text = title;
        t.ForeColor = Color.White;
        t.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        t.Location = new Point(22, 16);
        t.Size = new Size(395, 26);

        Label b = new Label();
        b.Text = body;
        b.ForeColor = Color.FromArgb(175, 182, 195);
        b.Font = new Font("Segoe UI", 9.5f);
        b.Location = new Point(22, 46);
        b.Size = new Size(395, 40);

        Controls.AddRange(new Control[] { stripe, t, b });

        System.Windows.Forms.Timer life = new System.Windows.Forms.Timer();
        life.Interval = ok ? 2500 : 6000;   // failures stay up longer
        life.Tick += delegate { life.Stop(); Close(); };
        life.Start();

        Click += delegate { Close(); };
        foreach (Control c in Controls) c.Click += delegate { Close(); };
    }
}

// ---------------------------------------------------------------- entry point
public static class Program
{
    static string ResolveKey(string[] args)
    {
        if (args.Length >= 3 && args[2].Length > 0) return args[2];
        List<Game> g = Sync.Games();
        return g.Count > 0 ? g[0].Key : "";   // fall back to the first registered game
    }

    [STAThread]
    public static void Main(string[] args)
    {
        Application.EnableVisualStyles();

        // --silent: no window at all. The scheduled auto-push uses this; a console
        // app such as powershell.exe flashes a window and steals focus every run.
        if (args.Length >= 2 && args[0] == "--silent")
        {
            string mode = args[1];
            // "--silent backup all" backs up every registered game in one go
            List<Game> targets = new List<Game>();
            string key = ResolveKey(args);
            if (args.Length >= 3 && args[2] == "all") targets = Sync.Games();
            else { Game one = new Game(); one.Key = key; targets.Add(one); }

            foreach (Game g in targets)
            {
                string summary;
                int code = Sync.Run(mode, g.Key, null, out summary);
                try
                {
                    File.AppendAllText("C:/SaveSync/bin/autopush.log",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + mode + "  " + g.Key +
                        "  exit=" + code + "  " + summary + Environment.NewLine);
                }
                catch { }
            }
            return;
        }

        // --toast: do the work, then show a self-closing notification.
        if (args.Length >= 2 && args[0] == "--toast")
        {
            string mode = args[1];
            string key = ResolveKey(args);
            string summary;
            int code = Sync.Run(mode, key, null, out summary);
            string title = (mode == "restore") ? "SaveSync  -  save restored" : "SaveSync  -  save backed up";
            if (code != 0) title = "SaveSync  -  " + mode + " FAILED (exit " + code + ")";
            Application.Run(new ToastForm(title, key + ":  " + summary, code == 0));
            return;
        }

        Application.Run(new SaveSyncForm());
    }
}
