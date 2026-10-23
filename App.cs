using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
namespace FishReader;
public static class App
{
    [System.STAThread]
    public static void Main(string[] args)
    {
        try {
            string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FishReader");
            int dataIndex = Array.IndexOf(args, "--data-dir");
            if (dataIndex >= 0 && dataIndex + 1 < args.Length) dataDir = Path.GetFullPath(args[dataIndex + 1]);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToUpperInvariant())))[..20];
            using var mutex = new Mutex(true, "Local\\FishReader-" + key, out bool created);
            if (!created) { MessageBox.Show("轻阅已经运行，请从任务栏或托盘打开。", "轻阅"); return; }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var reader = new ReaderWindow(new StateStore(Path.Combine(dataDir, "state.json")));
            string? book = args.FirstOrDefault(a => a.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            if (book != null) reader.State.LastFile = Path.GetFullPath(book);
            app.Run(reader);
        } catch (Exception ex) { MessageBox.Show("启动失败：\n" + ex.Message, "轻阅", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
