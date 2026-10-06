using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace Phosphor
{
    public partial class MainWindow : Window
    {
        private const string InstallLabel = "Установить";
        private const string AbortLabel = "Прервать";
        private const int MaxLogLines = 3000;

        private readonly ObservableCollection<AppItem> _apps = new ObservableCollection<AppItem>();
        private readonly ObservableCollection<CategoryItem> _cats = new ObservableCollection<CategoryItem>();
        private readonly List<string> _history = new List<string>();
        private ICollectionView _view;
        private string _category = CategoryItem.All;

        private List<string> _preselect;
        private readonly bool _autoInstall;
        private bool _wingetOk;
        private bool _busy;
        private bool _abort;
        private StreamWriter _logFile;
        private string _logDir;

        private enum Tone { Normal, Dim, Bright, Warn, Error, Cmd }

        public MainWindow(List<string> preselect, bool autoInstall)
        {
            InitializeComponent();
            _preselect = preselect;
            _autoInstall = autoInstall;

            InstallButton.Content = InstallLabel;
            CategoryList.ItemsSource = _cats;

            Loaded += async (s, e) => await BootAsync();
            PreviewKeyDown += OnPreviewKeyDown;
            StateChanged += (s, e) => SyncMaximized();
            Closing += OnClosing;
            Activated += (s, e) => { if (!_busy) Taskbar.ProgressState = TaskbarItemProgressState.None; };
        }

        // ═════════════════════════ boot ═════════════════════════

        private async Task BootAsync()
        {
            bool admin = IsAdmin();
            AdminBadge.Text = admin ? "Администратор" : "Пользователь";
            AdminBadge.Foreground = (Brush)FindResource(admin ? "SubtleBrush" : "MutedBrush");
            ElevateButton.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

            Log("PHOSPHOR v1.0 :: портативный пакетный установщик на базе winget", Tone.Bright);
            Log($"хост {Environment.MachineName} · пользователь {Environment.UserName} · права: {(admin ? "администратор" : "обычные")}", Tone.Dim);
            if (!admin)
                Log("подсказка: без прав администратора winget будет запрашивать UAC для части пакетов — проще нажать «Повысить права»", Tone.Dim);

            LoadCatalog();
            await CheckWingetAsync();

            StateText.Text = _wingetOk ? "" : "winget не найден";
            SearchBox.Focus();

            if (_autoInstall && _wingetOk) await InstallAsync();
        }

        private async Task CheckWingetAsync()
        {
            Log("проверка winget …", Tone.Dim);
            var v = await Winget.GetVersionAsync();
            _wingetOk = v != null;
            if (_wingetOk)
            {
                WingetInfo.Text = "winget " + v;
                WingetInfo.Foreground = (Brush)FindResource("FaintBrush");
                Log($"winget {v} — ок", Tone.Normal);
            }
            else
            {
                WingetInfo.Text = "winget не найден";
                WingetInfo.Foreground = (Brush)FindResource("ErrorBrush");
                Log("winget не найден. Установите «Установщик приложений» (App Installer) из Microsoft Store:", Tone.Error);
                Log("    ms-windows-store://pdp/?productid=9NBLGGH4NNS1   или   https://aka.ms/getwinget", Tone.Error);
            }
        }

        // ═════════════════════════ catalog ═════════════════════════

        private void LoadCatalog()
        {
            // Keep the user's ticks across F5 reloads.
            var wasChecked = new HashSet<string>(_apps.Where(a => a.IsChecked).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            bool reload = _apps.Count > 0;

            var res = Catalog.Load();
            if (res.Warning != null) Log(res.Warning, Tone.Warn);

            foreach (var a in _apps) a.PropertyChanged -= OnAppChanged;
            _apps.Clear();

            HashSet<string> pre = _preselect != null ? new HashSet<string>(_preselect, StringComparer.OrdinalIgnoreCase) : null;
            foreach (var def in res.Apps)
            {
                var item = new AppItem(def);
                if (pre != null) item.IsChecked = pre.Contains(def.Id);
                else if (reload) item.IsChecked = wasChecked.Contains(def.Id);
                item.PropertyChanged += OnAppChanged;
                _apps.Add(item);
            }
            if (pre != null)
            {
                foreach (var miss in pre.Where(id => _apps.All(a => !a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
                    Log($"--select: «{miss}» нет в каталоге, пропущено", Tone.Warn);
                _preselect = null; // only on first load
            }

            // categories in order of first appearance
            _cats.Clear();
            _cats.Add(new CategoryItem(CategoryItem.All, "Все"));
            _cats.Add(new CategoryItem(CategoryItem.Selected, "Выбранные"));
            foreach (var name in _apps.Select(a => a.Category).Distinct(StringComparer.OrdinalIgnoreCase))
                _cats.Add(new CategoryItem(name, name));

            _view = CollectionViewSource.GetDefaultView(_apps);
            _view.Filter = Filter;
            AppList.ItemsSource = _view;

            var keep = _cats.FirstOrDefault(c => c.Key.Equals(_category, StringComparison.OrdinalIgnoreCase)) ?? _cats[0];
            CategoryList.SelectedItem = keep;

            UpdateCounts();
            RefreshView();

            Log($"каталог: {_apps.Count} пакетов в {_cats.Count - 2} категориях ← {res.Origin}", Tone.Normal);
        }

        private bool Filter(object o)
        {
            var a = (AppItem)o;
            var q = SearchBox.Text;
            bool searching = !string.IsNullOrWhiteSpace(q);

            if (_category == CategoryItem.Selected) { if (!a.IsChecked && a.State == InstallState.Idle) return false; }
            else if (!searching && _category != CategoryItem.All &&
                     !a.Category.Equals(_category, StringComparison.OrdinalIgnoreCase)) return false;

            // a search looks through every category
            return a.Matches(q);
        }

        private void RefreshView()
        {
            if (_view == null) return;
            _view.Refresh();
            int shown = _view.Cast<object>().Count();
            ViewInfo.Text = shown == 1 ? "1 пакет" : $"{shown} пакетов";
            EmptyHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;

            bool searching = !string.IsNullOrWhiteSpace(SearchBox.Text);
            var cat = CategoryList.SelectedItem as CategoryItem;
            AppsTitle.Text = searching ? "Результаты поиска" : (cat?.Title ?? "Приложения");
        }

        private void OnAppChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppItem.IsChecked)) UpdateCounts();
        }

        private void UpdateCounts()
        {
            int total = _apps.Count, sel = _apps.Count(a => a.IsChecked);
            foreach (var c in _cats)
            {
                if (c.Key == CategoryItem.All) { c.Count = total; c.Checked = sel; }
                else if (c.Key == CategoryItem.Selected) { c.Count = sel; c.Checked = sel; }
                else
                {
                    var inCat = _apps.Where(a => a.Category.Equals(c.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                    c.Count = inCat.Count;
                    c.Checked = inCat.Count(a => a.IsChecked);
                }
            }
            SelectedInfo.Text = $"Выбрано {sel} из {total}";
        }

        // ═════════════════════════ install queue ═════════════════════════

        private async Task InstallAsync()
        {
            if (_busy) { RequestAbort(); return; }

            if (!_wingetOk)
            {
                await CheckWingetAsync(); // maybe it was installed in the meantime
                if (!_wingetOk) return;
            }

            var queue = _apps.Where(a => a.IsChecked).ToList();
            if (queue.Count == 0) { Log("ничего не выбрано — отметьте пакеты пробелом или кликом", Tone.Warn); return; }

            SetBusy(true);
            OpenLogFile();

            foreach (var a in _apps) { a.State = InstallState.Idle; a.Detail = null; }
            foreach (var a in queue) a.State = InstallState.Queued;
            RefreshView();

            Log("", Tone.Dim);
            Log($"— установка: {queue.Count} пакет(ов)", Tone.Bright);

            var total = Stopwatch.StartNew();
            int ok = 0, skipped = 0, failed = 0, done = 0;
            bool reboot = false;
            UpdateProgress(0, queue.Count);

            foreach (var item in queue)
            {
                if (_abort)
                {
                    item.State = InstallState.Skipped;
                    item.Detail = "Прервано";
                    skipped++; done++;
                    continue;
                }

                item.State = InstallState.Running;
                item.Detail = null;
                StateText.Text = "Устанавливается " + item.Name;
                Log($"[{done + 1}/{queue.Count}] {item.Name}  ({item.Id})", Tone.Bright);
                Log("    > winget " + Winget.BuildArgs(item.Def), Tone.Cmd);

                var sw = Stopwatch.StartNew();
                InstallOutcome res;
                try
                {
                    res = await Winget.InstallAsync(item.Def,
                        line => Dispatcher.InvokeAsync(() => Log("    " + line, Tone.Dim)),
                        prog => Dispatcher.InvokeAsync(() => { if (item.State == InstallState.Running) item.Detail = FormatProgress(prog); }));
                }
                catch (Exception ex)
                {
                    res = new InstallOutcome { State = InstallState.Failed, Detail = "Ошибка", ExitCode = -1 };
                    Log("    " + ex.Message, Tone.Error);
                }

                item.Detail = res.Detail;
                item.State = res.State;
                var secs = sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
                var code = res.ExitCode == 0 ? "" : "  код " + Winget.Hex(res.ExitCode);

                switch (res.State)
                {
                    case InstallState.Done: ok++; Log($"    => {res.Detail}  ({secs} c){code}", Tone.Bright); break;
                    case InstallState.Skipped: skipped++; Log($"    => {res.Detail}  ({secs} c){code}", Tone.Normal); break;
                    default: failed++; Log($"    => {res.Detail}  ({secs} c){code}", Tone.Error); break;
                }
                if (res.Detail != null && res.Detail.Contains("перезагрузка")) reboot = true;

                done++;
                UpdateProgress(done, queue.Count);
            }

            Log($"— итого: успешно {ok} · пропущено {skipped} · ошибок {failed} · {FormatSpan(total.Elapsed)}",
                failed > 0 ? Tone.Warn : Tone.Bright);
            if (failed > 0)
                Log("    не установлены: " + string.Join(", ", queue.Where(a => a.State == InstallState.Failed).Select(a => a.Name)), Tone.Error);
            if (reboot)
                Log("    для завершения установки некоторых пакетов нужна перезагрузка", Tone.Warn);
            if (_logFile != null) Log("    лог сохранён: " + ((FileStream)_logFile.BaseStream).Name, Tone.Dim);

            StateText.Text = _abort ? "Прервано" : failed > 0 ? $"Готово, ошибок: {failed}" : "Готово";
            // leave a full green/red bar in the taskbar until the window is focused again
            Taskbar.ProgressValue = 1;
            Taskbar.ProgressState = failed > 0 ? TaskbarItemProgressState.Error : TaskbarItemProgressState.Normal;
            if (IsActive && failed == 0) Taskbar.ProgressState = TaskbarItemProgressState.None;
            CloseLogFile();
            SetBusy(false);
        }

        private void RequestAbort()
        {
            if (_abort) return;
            _abort = true;
            InstallButton.IsEnabled = false;
            StateText.Text = "Прерывание…";
            Log("прерывание: текущий пакет будет доустановлен, остальные пропущены", Tone.Warn);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _abort = false;
            InstallButton.IsEnabled = true;
            InstallButton.Content = busy ? AbortLabel : InstallLabel;
            ReloadButton.IsEnabled = !busy;
            SelectButton.IsEnabled = !busy;
            ClearButton.IsEnabled = !busy;
            AppList.IsHitTestVisible = !busy; // keep rows readable, just not clickable
            ElevateButton.IsEnabled = !busy;
            if (busy) Taskbar.ProgressState = TaskbarItemProgressState.Normal;
        }

        private void UpdateProgress(int done, int total)
        {
            Progress.Visibility = Visibility.Visible;
            Progress.Value = total == 0 ? 0 : (double)done / total;
            ProgressText.Text = $"{done} / {total}";
            Taskbar.ProgressValue = total == 0 ? 0 : (double)done / total;
        }

        private static readonly Regex SizeRx = new Regex(@"^([\d.,]+)\s*([KMGT]?B)\s*/\s*([\d.,]+)\s*([KMGT]?B)$", RegexOptions.IgnoreCase);

        /// <summary>"12.0 MB / 100 MB" → "↓ 12%"; "45%" → "↓ 45%".</summary>
        private static string FormatProgress(string raw)
        {
            var m = SizeRx.Match(raw);
            if (m.Success && TryBytes(m.Groups[1].Value, m.Groups[2].Value, out var cur) &&
                TryBytes(m.Groups[3].Value, m.Groups[4].Value, out var max) && max > 0)
                return $"{Math.Min(100, (int)(100 * cur / max))}%";
            if (raw.EndsWith("%")) return raw;
            return raw.Length > 14 ? raw.Substring(0, 14) : raw;
        }

        private static bool TryBytes(string num, string unit, out double bytes)
        {
            bytes = 0;
            if (!double.TryParse(num.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
            switch (unit.ToUpperInvariant())
            {
                case "KB": v *= 1024; break;
                case "MB": v *= 1024 * 1024; break;
                case "GB": v *= 1024d * 1024 * 1024; break;
                case "TB": v *= 1024d * 1024 * 1024 * 1024; break;
            }
            bytes = v;
            return true;
        }

        private static string FormatSpan(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        // ═════════════════════════ log ═════════════════════════

        private void Log(string text, Tone tone)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            var line = text.Length == 0 ? "" : $"{stamp}  {text}";
            _history.Add(line);
            _logFile?.WriteLine(line);

            if (LogPara.Inlines.Count > 0) LogPara.Inlines.Add(new LineBreak());
            if (line.Length > 0)
            {
                LogPara.Inlines.Add(new Run(stamp + "  ") { Foreground = (Brush)FindResource("FaintBrush") });
                LogPara.Inlines.Add(new Run(text) { Foreground = ToneBrush(tone) });
            }

            // trim old lines (each line is up to 3 inlines)
            while (LogPara.Inlines.Count > MaxLogLines * 3)
            {
                var first = LogPara.Inlines.FirstInline;
                LogPara.Inlines.Remove(first);
                if (first is LineBreak) break;
            }
            LogBox.ScrollToEnd();
        }

        private Brush ToneBrush(Tone t)
        {
            switch (t)
            {
                case Tone.Bright: return (Brush)FindResource("TextBrush");
                case Tone.Warn: return (Brush)FindResource("AccentBrush");
                case Tone.Error: return (Brush)FindResource("ErrorBrush");
                case Tone.Cmd: return (Brush)FindResource("FaintBrush");
                case Tone.Dim: return (Brush)FindResource("MutedBrush");
                default: return (Brush)FindResource("SubtleBrush");
            }
        }

        private void OpenLogFile()
        {
            CloseLogFile();
            var name = $"phosphor-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            foreach (var dir in new[] { Path.Combine(Catalog.AppDir, "logs"), Path.Combine(Path.GetTempPath(), "Phosphor", "logs") })
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    _logFile = new StreamWriter(Path.Combine(dir, name), false, new UTF8Encoding(true)) { AutoFlush = true };
                    _logDir = dir;
                    foreach (var h in _history) _logFile.WriteLine(h);
                    return;
                }
                catch { /* read-only location, try next */ }
            }
        }

        private void CloseLogFile()
        {
            _logFile?.Dispose();
            _logFile = null;
        }

        // ═════════════════════════ actions ═════════════════════════

        private void SelectVisible()
        {
            var visible = _view.Cast<AppItem>().ToList();
            if (visible.Count == 0) return;
            bool all = visible.All(a => a.IsChecked);
            foreach (var a in visible) a.IsChecked = !all;
        }

        private void ClearAll()
        {
            foreach (var a in _apps) a.IsChecked = false;
            if (_category == CategoryItem.Selected) RefreshView();
        }

        private void Reload()
        {
            if (_busy) return;
            Log("перечитываю каталог …", Tone.Dim);
            LoadCatalog();
        }

        private void EditCatalog()
        {
            if (!File.Exists(Catalog.FilePath)) LoadCatalog(); // creates it
            try { Process.Start(new ProcessStartInfo(Catalog.FilePath) { UseShellExecute = true }); }
            catch
            {
                try { Process.Start("notepad.exe", "\"" + Catalog.FilePath + "\""); }
                catch (Exception ex) { Log("не удалось открыть apps.json: " + ex.Message, Tone.Error); return; }
            }
            Log("apps.json открыт в редакторе — после сохранения нажмите F5", Tone.Normal);
        }

        private void Elevate()
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var ids = _apps.Where(a => a.IsChecked).Select(a => a.Id).ToList();
            var args = "--select \"" + string.Join(",", ids) + "\"";
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Catalog.AppDir });
                _busy = false;
                Close();
            }
            catch (Win32Exception) { Log("повышение прав отменено", Tone.Warn); }
        }

        private static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // ═════════════════════════ event handlers ═════════════════════════

        private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.F2: if (!_busy) SelectVisible(); e.Handled = true; break;
                case Key.F3: if (!_busy) ClearAll(); e.Handled = true; break;
                case Key.F5: Reload(); e.Handled = true; break;
                case Key.F6: EditCatalog(); e.Handled = true; break;
                case Key.F9: e.Handled = true; await InstallAsync(); break;
                case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                    SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; break;
                case Key.Escape when SearchBox.Text.Length > 0:
                    SearchBox.Clear(); e.Handled = true; break;
                case Key.Down when SearchBox.IsKeyboardFocused:
                    FocusFirstRow(); e.Handled = true; break;
            }
        }

        private void FocusFirstRow()
        {
            if (AppList.Items.Count == 0) return;
            var c = AppList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            c?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshView();
            AppScroll.ScrollToTop();
        }

        private void CategoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CategoryList.SelectedItem is CategoryItem c)
            {
                _category = c.Key;
                RefreshView();
                AppScroll?.ScrollToTop();
            }
        }

        private async void Install_Click(object sender, RoutedEventArgs e) => await InstallAsync();
        private void SelectVisible_Click(object sender, RoutedEventArgs e) => SelectVisible();
        private void Clear_Click(object sender, RoutedEventArgs e) => ClearAll();
        private void Reload_Click(object sender, RoutedEventArgs e) => Reload();
        private void EditCatalog_Click(object sender, RoutedEventArgs e) => EditCatalog();
        private void Elevate_Click(object sender, RoutedEventArgs e) => Elevate();

        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            var dir = _logDir ?? Path.Combine(Catalog.AppDir, "logs");
            if (!Directory.Exists(dir)) { Log("логов пока нет — они пишутся во время установки", Tone.Dim); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void SyncMaximized()
        {
            // a chromeless maximized window overhangs the screen by the resize border
            bool max = WindowState == WindowState.Maximized;
            Frame.Margin = max ? new Thickness(7) : new Thickness(0);
            Frame.BorderThickness = max ? new Thickness(0) : new Thickness(1);
            MaxButton.Content = max ? "\uE923" : "\uE922";
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_busy) return;
            var r = MessageBox.Show(this,
                "Идёт установка. Закрыть Phosphor?\n\nТекущий установщик доработает в фоне, остальные пакеты будут пропущены.",
                "Phosphor", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) e.Cancel = true;
            else CloseLogFile();
        }
    }
}
