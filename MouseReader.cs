using System.Text;
using HidSharp;

namespace MausAkku;

/// <summary>Momentaufnahme des Mauszustands.</summary>
internal readonly record struct MouseSnapshot(
    bool ReceiverPresent,   // 2,4-GHz-Empfänger steckt
    bool CablePresent,      // Maus hängt per USB-Kabel am PC (= lädt)
    int? Battery,           // zuletzt gemeldeter Akkustand in %
    DateTime? LastReportUtc // Zeitpunkt der letzten Akkumeldung
);

/// <summary>
/// Liest den Akkustand der Glorious Model D 2 Wireless (Pixart-Chip, VID 0x093A).
///
/// Protokoll (aus Community-Reverse-Engineering, nicht offiziell dokumentiert):
///  * Empfänger  PID 0x824D, Maus am Kabel PID 0x824A (wird als "lädt" gewertet).
///  * Die herstellerspezifische Schnittstelle (Usage Page 0xFF00) liefert 8-Byte-Input-Reports
///    der Form  06 FB &lt;Akku %&gt; ...  – Byte 2 ist der Akkustand (1–100).
///  * Auf der Feature-Schnittstelle (64 Byte) wird regelmäßig eine Abfrage angestoßen
///    (GetFeature Report 0x03 und die bekannte Glorious-Akkuanfrage 0x83).
/// Alles Gelesene landet im Diagnose-Log, damit sich das Verhalten prüfen lässt.
/// </summary>
internal sealed class MouseReader : IDisposable
{
    private const int VendorId = 0x093A;
    private const int PidReceiver = 0x824D;
    private const int PidCable = 0x824A;
    private static readonly HashSet<int> KnownPids = new() { 0x824D, 0x824A, 0x824B };

    // [ReportID 0] 00 00 02 02 00 83  -> Akku-Abfrage (Glorious)
    private static readonly byte[] BatteryRequest = { 0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x83 };

    private readonly object _sync = new();
    private readonly Dictionary<string, CancellationTokenSource> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _wake = new(false);

    private bool _receiver;
    private bool _cable;
    private int? _battery;
    private DateTime? _lastReportUtc;
    private DateTime _lastRequestUtc = DateTime.MinValue;
    private bool _forceRequest = true;
    private string _lastLayout = "";
    private int _rawLogBudget = 40;
    private int _requestLogBudget = 3;
    private volatile int _pollSeconds = 60;

    public int PollSeconds
    {
        get => _pollSeconds;
        set => _pollSeconds = Math.Max(15, value);
    }

    public MouseSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return new MouseSnapshot(_receiver, _cable, _battery, _lastReportUtc);
        }
    }

    public void Start()
    {
        var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "MausAkku-Worker" };
        worker.Start();
    }

    /// <summary>Sofort neu abfragen (Menüpunkt "Jetzt aktualisieren").</summary>
    public void RefreshNow()
    {
        lock (_sync)
            _forceRequest = true;
        _wake.Set();
    }

    private void WorkerLoop()
    {
        Log.Write($"MausAkku {Application.ProductVersion} gestartet.");
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var featureInterfaces = Rescan();

                bool due;
                lock (_sync)
                {
                    // Solange noch kein Wert da ist: alle 15 s versuchen, danach im eingestellten Takt
                    int interval = _battery == null ? 15 : _pollSeconds;
                    due = _forceRequest || DateTime.UtcNow - _lastRequestUtc >= TimeSpan.FromSeconds(interval);
                    if (due)
                    {
                        _forceRequest = false;
                        _lastRequestUtc = DateTime.UtcNow;
                    }
                }

                if (due && featureInterfaces.Count > 0)
                    SendRequests(featureInterfaces);
            }
            catch (Exception ex)
            {
                Log.Write("Fehler im Abfrage-Durchlauf: " + ex.Message);
            }

            // Geräteliste alle 5 s prüfen (Empfänger/Kabel ein- oder ausgesteckt)
            WaitHandle.WaitAny(new[] { _wake, _stop.Token.WaitHandle }, TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Sucht die Schnittstellen der Maus und startet/stoppt die Lese-Threads.</summary>
    private List<HidDevice> Rescan()
    {
        List<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices(VendorId)
                .Where(d => KnownPids.Contains(d.ProductID))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Write("HID-Geräte konnten nicht aufgezählt werden: " + ex.Message);
            devices = new List<HidDevice>();
        }

        var inputs = new List<HidDevice>();
        var features = new List<HidDevice>();
        var layout = new StringBuilder();

        foreach (var d in devices)
        {
            int page = UsagePage(d);
            int inLen = Safe(d.GetMaxInputReportLength);
            int featLen = Safe(d.GetMaxFeatureReportLength);
            int outLen = Safe(d.GetMaxOutputReportLength);
            layout.AppendLine($"    PID=0x{d.ProductID:X4} UsagePage=0x{page:X4} In={inLen} Feature={featLen} Out={outLen}  {d.DevicePath}");

            // Nur herstellerspezifische Schnittstellen (0xFF00–0xFFFF); 0 = unbekannt, trotzdem versuchen.
            // Maus-/Tastatur-Schnittstellen sperrt Windows ohnehin.
            bool vendor = page >= 0xFF00 || page == 0;
            if (!vendor)
                continue;
            if (inLen > 0 && inLen <= 65)
                inputs.Add(d);
            if (featLen > 0)
                features.Add(d);
        }

        string layoutText = layout.ToString();
        if (layoutText != _lastLayout)
        {
            _lastLayout = layoutText;
            Log.Write(devices.Count == 0
                ? "Keine Model-D-2-Schnittstellen gefunden (Empfänger eingesteckt? Bluetooth wird nicht unterstützt)."
                : "Gefundene Schnittstellen:" + Environment.NewLine + layoutText.TrimEnd());
            lock (_sync)
            {
                _rawLogBudget = 40;
                _requestLogBudget = 3;
                _forceRequest = true;
            }
        }

        bool receiver = devices.Any(d => d.ProductID == PidReceiver);
        bool cable = devices.Any(d => d.ProductID == PidCable);
        var wanted = new HashSet<string>(inputs.Select(d => d.DevicePath), StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            _receiver = receiver;
            _cable = cable;

            foreach (var path in _readers.Keys.Where(p => !wanted.Contains(p)).ToList())
            {
                _readers[path].Cancel();
                _readers.Remove(path);
            }

            foreach (var d in inputs)
            {
                if (_readers.ContainsKey(d.DevicePath))
                    continue;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                _readers[d.DevicePath] = cts;
                var device = d;
                new Thread(() => ReadLoop(device, cts)) { IsBackground = true, Name = "MausAkku-Read" }.Start();
            }
        }

        return features;
    }

    /// <summary>Liest dauerhaft Input-Reports einer Schnittstelle.</summary>
    private void ReadLoop(HidDevice device, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            using HidStream stream = device.Open();
            stream.ReadTimeout = 1000;
            while (!token.IsCancellationRequested)
            {
                byte[] buffer;
                try
                {
                    buffer = stream.Read();
                }
                catch (TimeoutException)
                {
                    continue;
                }
                HandleReport(device, buffer);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Log.Write($"Schnittstelle PID 0x{device.ProductID:X4} nicht lesbar ({ex.GetType().Name}: {ex.Message})");
                Thread.Sleep(2000); // nicht im Sekundentakt neu öffnen
            }
        }
        finally
        {
            // Austragen, damit der nächste Scan es erneut versucht
            lock (_sync)
            {
                if (_readers.TryGetValue(device.DevicePath, out var current) && ReferenceEquals(current, cts))
                    _readers.Remove(device.DevicePath);
            }
        }
    }

    private void HandleReport(HidDevice device, byte[] buffer)
    {
        if (buffer.Length == 0)
            return;

        bool logRaw;
        lock (_sync)
        {
            logRaw = _rawLogBudget > 0;
            if (logRaw)
                _rawLogBudget--;
        }
        if (logRaw)
            Log.Write($"Empfangen PID 0x{device.ProductID:X4} ({buffer.Length} Byte): {Hex(buffer, 16)}");

        // 06 FB <Akku %> ...
        if (buffer.Length >= 3 && buffer[0] == 0x06 && buffer[1] == 0xFB && buffer[2] is >= 1 and <= 100)
        {
            int level = buffer[2];
            bool changed;
            lock (_sync)
            {
                changed = _battery != level;
                _battery = level;
                _lastReportUtc = DateTime.UtcNow;
            }
            if (changed)
                Log.Write($"Akkustand: {level} %");
        }
    }

    /// <summary>Stößt auf den Feature-Schnittstellen eine Akkumeldung an.</summary>
    private void SendRequests(List<HidDevice> features)
    {
        bool logThis;
        lock (_sync)
        {
            logThis = _requestLogBudget > 0;
            if (logThis)
                _requestLogBudget--;
        }

        // Empfänger zuerst
        foreach (var device in features.OrderBy(d => d.ProductID == PidReceiver ? 0 : 1))
        {
            if (_stop.IsCancellationRequested)
                return;

            int length = Safe(device.GetMaxFeatureReportLength);
            if (length <= 0)
                length = 64;

            HidStream stream;
            try
            {
                stream = device.Open();
            }
            catch (Exception ex)
            {
                if (logThis)
                    Log.Write($"Feature-Schnittstelle PID 0x{device.ProductID:X4} nicht zu öffnen: {ex.Message}");
                continue;
            }

            using (stream)
            {
                stream.ReadTimeout = 6000;
                stream.WriteTimeout = 2000;

                // 1) GetFeature Report 0x03 – der Empfänger antwortet darauf (verzögert) mit 06 FB .. auf der Input-Schnittstelle
                var primer = new byte[length];
                primer[0] = 0x03;
                try
                {
                    stream.GetFeature(primer);
                    if (logThis)
                        Log.Write($"GetFeature 0x03 (PID 0x{device.ProductID:X4}): {Hex(primer, 16)}");
                }
                catch (Exception ex)
                {
                    if (logThis)
                        Log.Write($"GetFeature 0x03 (PID 0x{device.ProductID:X4}) abgelehnt: {ex.GetType().Name} (normal)");
                }

                // 2) Klassische Glorious-Akkuanfrage
                var request = new byte[length];
                Array.Copy(BatteryRequest, request, Math.Min(BatteryRequest.Length, length));
                try
                {
                    stream.SetFeature(request);
                }
                catch (Exception ex)
                {
                    if (logThis)
                        Log.Write($"SetFeature 0x83 (PID 0x{device.ProductID:X4}) abgelehnt: {ex.GetType().Name} (normal)");
                }
            }
        }
    }

    private static int UsagePage(HidDevice device)
    {
        try
        {
            byte[] d = device.GetRawReportDescriptor();
            for (int i = 0; i < d.Length; i++)
            {
                if (d[i] == 0x06 && i + 2 < d.Length)
                    return d[i + 1] | (d[i + 2] << 8);   // Usage Page (2 Byte)
                if (d[i] == 0x05 && i + 1 < d.Length)
                    return d[i + 1];                     // Usage Page (1 Byte)
            }
        }
        catch
        {
            // Deskriptor nicht lesbar
        }
        return 0;
    }

    private static int Safe(Func<int> getter)
    {
        try { return getter(); }
        catch { return 0; }
    }

    private static string Hex(byte[] data, int max) =>
        BitConverter.ToString(data, 0, Math.Min(max, data.Length)).Replace('-', ' ');

    public void Dispose()
    {
        _stop.Cancel();
        _wake.Set();
    }
}
