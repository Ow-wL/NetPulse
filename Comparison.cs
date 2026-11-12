using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace NetPulse
{
    internal sealed class SavedRun
    {
        internal string Mode, Id, EndReason;
        internal DateTimeOffset Started;
        internal readonly Dictionary<string, Dictionary<string, string>> Targets =
            new Dictionary<string, Dictionary<string, string>>();

        internal static SavedRun Read(string path)
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2) throw new FormatException();
            string[] columns = lines[0].Split(',');
            var run = new SavedRun();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] values = lines[i].Split(',');
                if (columns.Length != values.Length) throw new FormatException();
                var row = columns.Select((name, index) => new { name, value = values[index] })
                    .ToDictionary(item => item.name, item => item.value);
                if (i == 1)
                {
                    run.Mode = row["mode"];
                    run.Id = row["session"];
                    run.EndReason = row["reason"];
                    run.Started = DateTimeOffset.Parse(row["started"], CultureInfo.InvariantCulture);
                    if (run.Mode != "Mesh" && run.Mode != "WiFi") throw new FormatException();
                }
                if (row["mode"] != run.Mode || row["session"] != run.Id) throw new FormatException();
                foreach (string metric in new[] { "valid", "loss_pct", "avg_ms", "max_ms", "longest_loss", "probe_errors" })
                    if (!row.ContainsKey(metric)) throw new FormatException();
                run.Targets.Add(row["ip"], row);
            }
            return run;
        }

        public override string ToString()
        {
            string reason = EndReason == "Stopped" ? "중지" : EndReason == "Completed" ? "완료" : "오류";
            return Started.ToString("yyyy-MM-dd HH:mm:ss") + " · " + reason;
        }
    }

    internal sealed class Comparison
    {
        internal readonly Window Window;
        private readonly ComboBox mesh, wifi;
        private readonly StackPanel rows;
        private readonly Style glass;
        private readonly int skippedFiles;

        internal Comparison(Window owner, string root)
        {
            var sessions = new List<SavedRun>();
            if (Directory.Exists(root))
            {
                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    string path = Path.Combine(dir, "summary.csv");
                    if (!File.Exists(path)) continue;
                    try { sessions.Add(SavedRun.Read(path)); }
                    catch (Exception ex)
                    {
                        if (!(ex is FormatException || ex is KeyNotFoundException || ex is ArgumentException ||
                            ex is IOException || ex is UnauthorizedAccessException)) throw;
                        skippedFiles++;
                    }
                }
            }

            Window = new Window
            {
                Owner = owner, Title = "NetPulse · 결과 비교", WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
                Width = 740, Height = 725, MinWidth = 545, MinHeight = 450,
                FontFamily = owner.FontFamily, Foreground = Ui.Ink, FontSize = 14,
                Language = owner.Language, UseLayoutRounding = true, SnapsToDevicePixels = true,
                Background = Ui.Brush("#F5F6F8")
            };
            WindowChrome.SetWindowChrome(Window, new WindowChrome
            {
                CaptionHeight = 45, ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(14)
            });
            glass = (Style)owner.Resources["Glass"];

            var background = new Grid { Background = (Brush)owner.Resources["Backdrop"] };
            Window.Content = new Border { Child = background, BorderThickness = new Thickness(1), BorderBrush = Ui.Brush("#D9DDE3") };

            var frame = new DockPanel();
            background.Children.Add(frame);
            var titleBar = new DockPanel { Height = 45, Margin = new Thickness(23, 0, 10, 0) };
            DockPanel.SetDock(titleBar, Dock.Top);
            frame.Children.Add(titleBar);
            var close = new Button
            {
                Style = (Style)owner.Resources["CaptionButton"], Content = "\uE8BB", ToolTip = "닫기"
            };
            System.Windows.Automation.AutomationProperties.SetName(close, "닫기");
            close.Click += (sender, args) => Window.Close();
            DockPanel.SetDock(close, Dock.Right);
            titleBar.Children.Add(close);
            var brand = Ui.Text("NetPulse", 14, Ui.Ink, true);
            brand.VerticalAlignment = VerticalAlignment.Center;
            titleBar.Children.Add(brand);

            var content = new StackPanel { Margin = new Thickness(28, 14, 28, 23) };
            var scroller = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            scroller.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = owner.Resources["SlimScrollBar"];
            frame.Children.Add(scroller);
            content.Children.Add(Ui.Text("연결 방식 비교", 32, Ui.Ink, true));
            var caption = Ui.Text("저장된 측정에서 메인 공유기와 외부 IP의 결과를 비교합니다.", 13, Ui.Muted);
            caption.Margin = new Thickness(0, 6, 0, 17);
            content.Children.Add(caption);

            var selectors = Columns(1, 1);
            var comboStyle = (Style)owner.Resources["GlassCombo"];
            mesh = AddSelector(selectors, 0, "Mesh 경유", sessions.Where(s => s.Mode == "Mesh"), comboStyle);
            wifi = AddSelector(selectors, 1, "직접 Wi‑Fi", sessions.Where(s => s.Mode == "WiFi"), comboStyle);
            content.Children.Add(new Border { Style = glass, Child = selectors, Padding = new Thickness(16, 13, 16, 15) });
            rows = new StackPanel { Margin = new Thickness(0, 13, 0, 0) };
            content.Children.Add(rows);
            mesh.SelectionChanged += (sender, args) => ShowRows();
            wifi.SelectionChanged += (sender, args) => ShowRows();
            ShowRows();
        }

        private static ComboBox AddSelector(Grid grid, int column, string label, IEnumerable<SavedRun> runs, Style style)
        {
            SavedRun[] sorted = runs.OrderByDescending(run => run.Started).ToArray();
            var box = new ComboBox { ItemsSource = sorted, Style = style,
                IsEnabled = sorted.Length > 0, SelectedIndex = sorted.Length > 0 ? 0 : -1 };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            var panel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 8, 0, column == 0 ? 8 : 0, 0) };
            var title = Ui.Text(label, 13, Ui.Ink, true);
            title.Margin = new Thickness(2, 0, 0, 6);
            panel.Children.Add(title);
            if (sorted.Length == 0) box.ItemsSource = new[] { "저장된 측정 없음" };
            if (sorted.Length == 0) box.SelectedIndex = 0;
            panel.Children.Add(box);
            Grid.SetColumn(panel, column);
            grid.Children.Add(panel);
            return box;
        }

        private void ShowRows()
        {
            rows.Children.Clear();
            var left = mesh.SelectedItem as SavedRun;
            var right = wifi.SelectedItem as SavedRun;
            foreach (Target target in new[] { Dashboard.Router, Dashboard.Internet })
            {
                var body = new StackPanel();
                var title = new StackPanel { Orientation = Orientation.Horizontal };
                title.Children.Add(Ui.Text(target.Name, 16, Ui.Ink, true));
                var address = Ui.Text(target.Ip, 12, Ui.Muted);
                address.VerticalAlignment = VerticalAlignment.Center;
                address.Margin = new Thickness(9, 0, 0, 0);
                title.Children.Add(address);
                title.Margin = new Thickness(0, 0, 0, 9);
                body.Children.Add(title);
                AddRow(body, "지표", "Mesh 경유", "직접 Wi‑Fi", true);
                body.Children.Add(new Border { Height = 1, Background = Ui.Brush("#E7E9ED"),
                    Margin = new Thickness(0, 4, 0, 4) });
                AddMetric(body, target, left, right, "유효 측정 수", "valid", "회");
                AddMetric(body, target, left, right, "손실률", "loss_pct", "%");
                AddMetric(body, target, left, right, "평균 지연", "avg_ms", " ms");
                AddMetric(body, target, left, right, "최대 지연", "max_ms", " ms");
                AddMetric(body, target, left, right, "최장 연속 손실", "longest_loss", "회");
                AddMetric(body, target, left, right, "검사 오류", "probe_errors", "회");
                rows.Children.Add(new Border { Style = glass, Padding = new Thickness(17, 12, 17, 11),
                    Margin = new Thickness(0, 0, 0, 10), Child = body });
            }
            string note = "비슷한 시간대에 같은 길이로 측정한 결과를 선택하세요.";
            if (skippedFiles > 0) note += " 읽을 수 없는 요약 " + skippedFiles + "개를 제외했습니다.";
            rows.Children.Add(Ui.Text(note, 12.5, Ui.Muted));
        }

        private static void AddMetric(StackPanel panel, Target target, SavedRun left, SavedRun right,
            string label, string field, string unit)
        {
            AddRow(panel, label, Metric(left, target.Ip, field, unit), Metric(right, target.Ip, field, unit));
        }

        private static string Metric(SavedRun run, string ip, string field, string unit)
        {
            Dictionary<string, string> row;
            string value;
            return run != null && run.Targets.TryGetValue(ip, out row) && row.TryGetValue(field, out value)
                && !string.IsNullOrWhiteSpace(value) ? value + unit : "—";
        }

        private static Grid Columns(params double[] weights)
        {
            var grid = new Grid();
            foreach (double weight in weights)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
            return grid;
        }

        private static void AddRow(StackPanel panel, string label, string left, string right, bool header = false)
        {
            var grid = Columns(1.35, 1, 1);
            grid.Margin = new Thickness(0, 2, 0, 2);
            var cells = new[] { label, left, right };
            for (int i = 0; i < cells.Length; i++)
            {
                var cell = Ui.Text(cells[i], 12, header || i == 0 ? Ui.Muted : Ui.Ink, header);
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }
            panel.Children.Add(grid);
        }
    }
}
