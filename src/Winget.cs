using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Phosphor
{
    public sealed class InstallOutcome
    {
        public InstallState State { get; set; }
        public string Detail { get; set; }
        public int ExitCode { get; set; }
    }

    /// <summary>Thin wrapper around winget.exe.</summary>
    public static class Winget
    {
        // winget HRESULTs (see winget-cli doc/windows/package-manager/winget/returnCodes.md)
        private static readonly int NoPackageFound      = unchecked((int)0x8A150014);
        private static readonly int UpdateNotApplicable = unchecked((int)0x8A15002B);
        private static readonly int PackageAlreadyInst  = unchecked((int)0x8A150061);
        private static readonly int InstallPackageInUse = unchecked((int)0x8A150101);
        private static readonly int InstallInProgress   = unchecked((int)0x8A150102);
        private static readonly int InstallDiskFull     = unchecked((int)0x8A150105);
        private static readonly int InstallNoNetwork    = unchecked((int)0x8A150107);
        private static readonly int RebootToFinish      = unchecked((int)0x8A150109);
        private static readonly int CancelledByUser     = unchecked((int)0x8A15010C);
        private static readonly int InstallerAlreadyIn  = unchecked((int)0x8A15010D);
        private static readonly int BlockedByPolicy     = unchecked((int)0x8A15010F);
        private static readonly int PackageInUseByApp   = unchecked((int)0x8A150111);

        /// <summary>Returns the winget version string, or null when winget is not available.</summary>
        public static Task<string> GetVersionAsync() => Task.Run(() =>
        {
            try
            {
                using (var p = Start("--version"))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(20000)) { TryKill(p); return null; }
                    var v = outTask.Result.Trim();
                    return p.ExitCode == 0 && v.Length > 0 ? v : null;
                }
            }
            catch (Win32Exception) { return null; } // winget.exe not found
        });

        public static string BuildArgs(AppDef app)
        {
            var source = string.IsNullOrWhiteSpace(app.Source) ? "winget" : app.Source.Trim();
            var sb = new StringBuilder();
            sb.Append("install --id ").Append(Quote(app.Id))
              .Append(" --exact --source ").Append(Quote(source))
              .Append(" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
            if (!string.IsNullOrWhiteSpace(app.Args)) sb.Append(' ').Append(app.Args.Trim());
            return sb.ToString();
        }

        /// <param name="onLine">Meaningful output lines (called on a worker thread).</param>
        /// <param name="onProgress">Download progress text like "12.0 MB / 100 MB" (worker thread).</param>
        public static Task<InstallOutcome> InstallAsync(AppDef app, Action<string> onLine, Action<string> onProgress) => Task.Run(() =>
        {
            using (var p = Start(BuildArgs(app)))
            {
                var filter = new OutputFilter(onLine, onProgress);
                var stdout = Task.Run(() => Pump(p.StandardOutput, filter));
                var stderr = Task.Run(() => Pump(p.StandardError, filter));
                p.WaitForExit();
                Task.WaitAll(stdout, stderr);
                return Classify(p.ExitCode);
            }
        });

        private static InstallOutcome Classify(int code)
        {
            InstallOutcome R(InstallState s, string d) => new InstallOutcome { State = s, Detail = d, ExitCode = code };

            if (code == 0) return R(InstallState.Done, "Готово");
            if (code == RebootToFinish) return R(InstallState.Done, "Готово · нужна перезагрузка");
            if (code == PackageAlreadyInst || code == InstallerAlreadyIn) return R(InstallState.Skipped, "Уже установлено");
            if (code == UpdateNotApplicable) return R(InstallState.Skipped, "Последняя версия");
            if (code == NoPackageFound) return R(InstallState.Failed, "ID не найден");
            if (code == InstallNoNetwork) return R(InstallState.Failed, "Нет сети");
            if (code == InstallDiskFull) return R(InstallState.Failed, "Нет места на диске");
            if (code == CancelledByUser) return R(InstallState.Failed, "Отменено");
            if (code == BlockedByPolicy) return R(InstallState.Failed, "Запрещено политикой");
            if (code == InstallPackageInUse || code == PackageInUseByApp) return R(InstallState.Failed, "Файлы заняты");
            if (code == InstallInProgress) return R(InstallState.Failed, "Занят другой установкой");
            return R(InstallState.Failed, "Ошибка");
        }

        public static string Hex(int code) => "0x" + code.ToString("X8");

        private static Process Start(string args)
        {
            var psi = new ProcessStartInfo("winget", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            return Process.Start(psi);
        }

        private static void Pump(StreamReader reader, OutputFilter filter)
        {
            var buf = new char[512];
            var line = new StringBuilder();
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    char c = buf[i];
                    // winget redraws spinners and progress bars with '\r'
                    if (c == '\r' || c == '\n') { filter.Emit(line.ToString()); line.Clear(); }
                    else line.Append(c);
                }
            }
            filter.Emit(line.ToString());
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";

        private static void TryKill(Process p) { try { p.Kill(); } catch { /* already gone */ } }

        /// <summary>Drops spinner frames, turns progress bars into short progress text, de-duplicates lines.</summary>
        private sealed class OutputFilter
        {
            private static readonly char[] BarChars = { '█', '▒', '▓', '░' };
            private readonly Action<string> _onLine, _onProgress;
            private readonly object _gate = new object();
            private string _last;

            public OutputFilter(Action<string> onLine, Action<string> onProgress) { _onLine = onLine; _onProgress = onProgress; }

            public void Emit(string raw)
            {
                var t = raw.Trim();
                if (t.Length == 0 || t.All(ch => ch == '-' || ch == '\\' || ch == '|' || ch == '/' || ch == ' ')) return;

                int bar = t.LastIndexOfAny(BarChars);
                if (bar >= 0)
                {
                    var tail = t.Substring(bar + 1).Trim();
                    if (tail.Length > 0) _onProgress?.Invoke(tail);
                    return;
                }

                lock (_gate)
                {
                    if (t == _last) return;
                    _last = t;
                }
                _onLine?.Invoke(t);
            }
        }
    }
}
