using CntlmGUI;
using CntlmGUI.Models;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "CntlmGUI_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("CntlmGUI 已经在运行。", "CntlmGUI",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var settings = GuiSettings.Load();
        Application.Run(new MainForm(settings));

        GC.KeepAlive(mutex);
    }
}
