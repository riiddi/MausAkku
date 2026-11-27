using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MausAkku;

/// <summary>Symbol im Infobereich der Taskleiste mit Rechtsklick-Menü.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings = Settings.Load();
    private readonly MouseReader _reader = new();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 2000 };
    private readonly Dictionary<string, Icon> _iconCache = new();
    private readonly DateTime _startUtc = DateTime.UtcNow;

    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _autostartItem = new("Mit Windows starten (Autostart)");
    private readonly ToolStripMenuItem _notifyItem = new("Warnmeldungen anzeigen");
    private readonly ToolStripMenuItem _warnMenu = new("Warnen bei Akkustand");
    private readonly ToolStripMenuItem _pollMenu = new("Abfrage-Intervall");

    private DateTime? _chargeStartUtc;
    private int? _levelBeforeCharge;
    private bool _warned;
    private bool _criticalWarned;
    private string _currentIconName = "";
    private string _currentStatus = "";

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        _statusItem.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Jetzt aktualisieren", null, (_, _) => _reader.RefreshNow());
        menu.Items.Add(new ToolStripSeparator());

        foreach (int percent in new[] { 10, 15, 20, 25, 30 })
        {
            var item = new ToolStripMenuItem($"{percent} %") { Tag = percent };
            item.Click += (_, _) =>
            {
                _settings.WarnPercent = percent;
                _settings.Save();
                _warned = false;
                UpdateMenuChecks();
            };
            _warnMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(_warnMenu);

        foreach (var (label, seconds) in new[] { ("30 Sekunden", 30), ("1 Minute", 60), ("2 Minuten", 120), ("5 Minuten", 300) })
        {
            var item = new ToolStripMenuItem(label) { Tag = seconds };
            item.Click += (_, _) =>
            {
                _settings.PollSeconds = seconds;
                _settings.Save();
                _reader.PollSeconds = seconds;
                UpdateMenuChecks();
            };
            _pollMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(_pollMenu);

        _notifyItem.Click += (_, _) =>
        {
            _settings.Notifications = !_settings.Notifications;
            _settings.Save();
            UpdateMenuChecks();
        };
        menu.Items.Add(_notifyItem);

        _autostartItem.Click += (_, _) => ToggleAutostart();
        menu.Items.Add(_autostartItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Diagnose-Log öffnen", null, (_, _) => OpenLog());
        menu.Items.Add("Beenden", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => UpdateMenuChecks();

        _tray = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = LoadIcon("aus"),
            Text = "Maus-Akku: suche Maus …",
            Visible = true
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _tray.ShowBalloonTip(3000, "Glorious Model D 2 Wireless", _currentStatus, ToolTipIcon.None);
        };

        _reader.PollSeconds = _settings.PollSeconds;
        _reader.Start();

        _uiTimer.Tick += (_, _) => UpdateUi();
        _uiTimer.Start();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        UpdateMenuChecks();
        UpdateUi();
    }

    // ───────────────────────── Anzeige ─────────────────────────

    private void UpdateUi()
    {
        var s = _reader.Snapshot;
        var offlineAfter = TimeSpan.FromSeconds(Math.Max(180, _settings.PollSeconds * 3));
        bool fresh = s.LastReportUtc is DateTime t && DateTime.UtcNow - t < offlineAfter;

        string icon;
        string status;

        if (s.CablePresent)
        {
            // Die Maus meldet am Kabel keinen Akkustand -> Ladedauer und Wert vor dem Anstecken anzeigen
            if (_chargeStartUtc == null)
            {
                _chargeStartUtc = DateTime.UtcNow;
                _levelBeforeCharge = s.Battery;
                Log.Write("Laden begonnen" + (s.Battery is int b0 ? $" (vorher {b0} %)." : "."));
            }
            icon = "laden";
            status = $"Lädt seit {Duration(DateTime.UtcNow - _chargeStartUtc.Value)}"
                     + (_levelBeforeCharge is int before ? $" (vorher {before} %)" : "");
            _warned = _criticalWarned = false;
        }
        else if (!s.ReceiverPresent)
        {
            icon = "aus";
            status = "Nicht verbunden – Empfänger nicht gefunden";
        }
        else if (fresh && s.Battery is int level)
        {
            icon = LevelIconName(level);
            status = $"Maus an – Akku {level} %";
            CheckLowBattery(level);
        }
        else
        {
            icon = "aus";
            if (s.Battery is int last && s.LastReportUtc is DateTime seen)
                status = $"Maus aus / Ruhemodus (zuletzt {last} %, {Ago(seen)})";
            else if (DateTime.UtcNow - _startUtc < offlineAfter)
                status = "Warte auf Antwort der Maus …";
            else
                status = "Maus aus oder keine Antwort";
        }

        if (!s.CablePresent && _chargeStartUtc != null)
        {
            Log.Write($"Laden beendet nach {Duration(DateTime.UtcNow - _chargeStartUtc.Value)}.");
            _chargeStartUtc = null;
            _levelBeforeCharge = null;
        }

        _currentStatus = status;
        _statusItem.Text = status;

        string text = "Model D 2 Wireless" + Environment.NewLine + status;
        _tray.Text = text.Length > 127 ? text[..127] : text;

        string iconName = IconFileName(icon) + "@" + TrayIconSize().Width;
        if (iconName != _currentIconName)
        {
            _currentIconName = iconName;
            _tray.Icon = LoadIcon(icon);
        }
    }

    private void CheckLowBattery(int level)
    {
        // Mit etwas Abstand zurücksetzen, damit es bei 15/16 % nicht ständig meldet
        if (level >= _settings.WarnPercent + 5)
            _warned = false;
        if (level >= _settings.CriticalPercent + 3)
            _criticalWarned = false;

        if (!_settings.Notifications)
            return;

        if (level <= _settings.CriticalPercent && !_criticalWarned)
        {
            _criticalWarned = _warned = true;
            _tray.ShowBalloonTip(10000, "Maus-Akku fast leer!",
                $"Nur noch {level} %. Bitte die Maus jetzt zum Laden anstecken.", ToolTipIcon.Error);
        }
        else if (level <= _settings.WarnPercent && !_warned)
        {
            _warned = true;
            _tray.ShowBalloonTip(10000, "Maus-Akku schwach",
                $"Noch {level} % – die Maus gehört zum Laden angesteckt.", ToolTipIcon.Warning);
        }
    }

    private static string LevelIconName(int level)
    {
        int step = Math.Clamp(level / 5 * 5, 0, 100);   // abrunden auf 5er-Schritte
        return step.ToString("000");
    }

    private static string Duration(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "unter 1 min";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min";
        return $"{(int)span.TotalHours} h {span.Minutes:00} min";
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "gerade eben";
        if (span.TotalMinutes < 60) return $"vor {(int)span.TotalMinutes} min";
        if (span.TotalHours < 24) return $"vor {(int)span.TotalHours} h";
        return $"am {utc.ToLocalTime():dd.MM. HH:mm}";
    }

    // ───────────────────────── Symbole ─────────────────────────

    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    private static string IconFileName(string key) => $"akku_{(TaskbarIsLight() ? "light" : "dark")}_{key}.ico";

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    /// <summary>
    /// Exakte Pixelgröße der Infobereich-Symbole bei der aktuellen Windows-Skalierung
    /// (100 % = 16 px, 125 % = 20, 150 % = 24, 175 % = 28, 200 % = 32 …).
    /// </summary>
    private static Size TrayIconSize()
    {
        try
        {
            const int SM_CXSMICON = 49;
            int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
            if (size > 0)
                return new Size(size, size);
        }
        catch
        {
            // ältere Windows-Versionen
        }
        return SystemInformation.SmallIconSize;
    }

    private Icon LoadIcon(string key)
    {
        string name = IconFileName(key);
        var size = TrayIconSize();
        string cacheKey = $"{name}@{size.Width}";
        if (_iconCache.TryGetValue(cacheKey, out var cached))
            return cached;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream == null)
        {
            Log.Write("Symbol fehlt: " + name);
            return SystemIcons.Application;
        }
        // Lädt genau das passend gezeichnete Bild aus der ICO-Datei – kein unscharfes Skalieren
        var icon = new Icon(stream, size);
        _iconCache[cacheKey] = icon;
        return icon;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Hell/Dunkel-Wechsel der Taskleiste -> passende Symbolvariante
        _currentIconName = "";
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Skalierung geändert -> Symbol in neuer Pixelgröße laden
        _currentIconName = "";
    }

    // ───────────────────────── Menü ─────────────────────────

    private void ToggleAutostart()
    {
        try
        {
            bool enable = !Autostart.IsEnabled();
            Autostart.Set(enable);
            _tray.ShowBalloonTip(3000, "MausAkku",
                enable ? "Wird ab jetzt mit Windows gestartet." : "Aus dem Autostart entfernt.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write("Autostart ändern fehlgeschlagen: " + ex.Message);
            _tray.ShowBalloonTip(5000, "MausAkku", "Autostart konnte nicht geändert werden: " + ex.Message, ToolTipIcon.Error);
        }
        UpdateMenuChecks();
    }

    private void UpdateMenuChecks()
    {
        _autostartItem.Checked = SafeAutostart();
        _notifyItem.Checked = _settings.Notifications;
        foreach (ToolStripMenuItem item in _warnMenu.DropDownItems)
            item.Checked = item.Tag is int p && p == _settings.WarnPercent;
        foreach (ToolStripMenuItem item in _pollMenu.DropDownItems)
            item.Checked = item.Tag is int s && s == _settings.PollSeconds;
    }

    private static bool SafeAutostart()
    {
        try { return Autostart.IsEnabled(); }
        catch { return false; }
    }

    private static void OpenLog()
    {
        try
        {
            if (!File.Exists(Log.FilePath))
                Log.Write("Log angelegt.");
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Log konnte nicht geöffnet werden:\n" + ex.Message, "MausAkku");
        }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _uiTimer.Stop();
        _reader.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        foreach (var icon in _iconCache.Values)
            icon.Dispose();
        base.ExitThreadCore();
    }
}
