using System.Text.Json;
using Microsoft.Win32;

namespace MausAkku;

/// <summary>Einfaches Diagnose-Log unter %APPDATA%\MausAkku\diagnose.log</summary>
internal static class Log
{
    private static readonly object Sync = new();

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MausAkku");

    public static string FilePath { get; } = Path.Combine(Folder, "diagnose.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".alt", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging darf die App nie stören
        }
    }
}

/// <summary>Einstellungen unter %APPDATA%\MausAkku\einstellungen.json</summary>
internal sealed class Settings
{
    public int WarnPercent { get; set; } = 15;
    public int CriticalPercent { get; set; } = 5;
    public int PollSeconds { get; set; } = 60;
    public bool Notifications { get; set; } = true;

    private static string FilePath => Path.Combine(Log.Folder, "einstellungen.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write("Einstellungen konnten nicht gelesen werden: " + ex.Message);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write("Einstellungen konnten nicht gespeichert werden: " + ex.Message);
        }
    }
}

/// <summary>Autostart über HKCU\Software\Microsoft\Windows\CurrentVersion\Run (keine Adminrechte nötig).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MausAkku";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value &&
               string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable)
            key.SetValue(ValueName, Command);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
