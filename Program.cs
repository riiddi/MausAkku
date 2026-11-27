namespace MausAkku;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Nur eine Instanz zulassen
        using var mutex = new Mutex(true, @"Local\MausAkku-SingleInstance", out bool isFirst);
        if (!isFirst)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
