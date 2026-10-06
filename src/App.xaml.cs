using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Phosphor
{
    /// <summary>
    /// Command line:
    ///   Phosphor.exe --select Google.Chrome,7zip.7zip   pre-select these ids (others unchecked)
    ///   Phosphor.exe --select ... --install             start installing right after boot
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            List<string> select = null;
            bool install = false;
            var args = e.Args;
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i].Trim();
                if (a.Equals("--install", StringComparison.OrdinalIgnoreCase)) install = true;
                else if (a.Equals("--select", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    select = args[++i].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }

            var w = new MainWindow(select, install);
            MainWindow = w;
            w.Show();
        }
    }
}
