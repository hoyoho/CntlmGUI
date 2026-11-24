using System;
using System.IO;
using System.Windows.Forms;
using CntlmGUI.Models;
using CntlmGUI.Services;

namespace CntlmGUI;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // 首次运行且尚无自己的配置时，从已安装的 cntlm.ini 导入初始参数
        if (!File.Exists(GuiSettings.SettingsFile))
        {
            foreach (var ini in new[]
                     {
                         @"C:\Program Files (x86)\Cntlm\cntlm.ini",
                         @"C:\Program Files\Cntlm\cntlm.ini"
                     })
            {
                if (!File.Exists(ini)) continue;
                try
                {
                    var seed = CntlmConfigGenerator.Import(ini);
                    seed.Save();
                    break;
                }
                catch
                {
                    // 导入失败忽略，保持默认
                }
            }
        }

        bool createdNew;
        using var mutex = new Mutex(true, "CntlmGUI_SingleInstance", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show("CntlmGUI 已经在运行。", "CntlmGUI",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new MainForm());
    }
}