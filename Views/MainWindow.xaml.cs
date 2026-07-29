using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CleanMaster.Core;

namespace CleanMaster.Views
{
    public partial class MainWindow : Window
    {
        private readonly ScannerEngine _scanner = new();
        private readonly QuarantineManager _quarantine = new();

        private readonly ObservableCollection<ScanItemViewModel> _lowRiskItems = new();
        private readonly ObservableCollection<ScanItemViewModel> _mediumRiskItems = new();

        public MainWindow()
        {
            InitializeComponent();

            LowRiskList.ItemsSource = _lowRiskItems;
            MediumRiskList.ItemsSource = _mediumRiskItems;
            ExcludedNotesList.ItemsSource = ProtectedPaths.UserFacingExclusionNotes;

            // 启动时先清掉超过保留期的历史隔离批次,避免隔离区无限增长占用磁盘
            _quarantine.PurgeExpiredBatches();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            ScanButton.IsEnabled = false;
            CleanButton.IsEnabled = false;
            _lowRiskItems.Clear();
            _mediumRiskItems.Clear();
            TotalSizeText.Text = "扫描中…";

            var rulesPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Rules.json");
            var rules = _scanner.LoadRules(rulesPath);

            _scanner.OnProgress += path =>
            {
                Dispatcher.Invoke(() => ScanStatusText.Text = $"正在扫描: {path}");
            };

            var results = await _scanner.ScanAsync(rules);

            foreach (var item in results.OrderByDescending(r => r.SizeBytes))
            {
                var vm = new ScanItemViewModel(item);
                vm.PropertyChanged += (_, __) => UpdateSelectionSummary();

                if (item.Rule.Risk == RiskLevel.Low)
                    _lowRiskItems.Add(vm);
                else
                    _mediumRiskItems.Add(vm);
            }

            var totalBytes = results.Sum(r => r.SizeBytes);
            TotalSizeText.Text = FormatSize(totalBytes);
            ScanStatusText.Text = $"扫描完成,共发现 {results.Count} 项,合计 {FormatSize(totalBytes)}";

            ScanButton.IsEnabled = true;
            ScanButton.Content = "重新扫描";
            UpdateSelectionSummary();
        }

        private void UpdateSelectionSummary()
        {
            var allItems = _lowRiskItems.Concat(_mediumRiskItems).ToList();
            var checkedItems = allItems.Where(i => i.IsChecked).ToList();
            var checkedSize = checkedItems.Sum(i => i.Item.SizeBytes);
            var mediumCheckedCount = _mediumRiskItems.Count(i => i.IsChecked);

            if (checkedItems.Count == 0)
            {
                SelectionSummaryText.Text = "尚未勾选任何项目";
                CleanButton.IsEnabled = false;
                return;
            }

            var note = mediumCheckedCount > 0
                ? $"(其中 {mediumCheckedCount} 项属于建议逐项确认类别)"
                : "";

            SelectionSummaryText.Text =
                $"已勾选 {checkedItems.Count} 项,合计 {FormatSize(checkedSize)} {note}";
            CleanButton.IsEnabled = true;
        }

        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            foreach (var i in _lowRiskItems.Concat(_mediumRiskItems))
                i.IsChecked = false;
        }

        private async void CleanButton_Click(object sender, RoutedEventArgs e)
        {
            var checkedItems = _lowRiskItems.Concat(_mediumRiskItems)
                .Where(i => i.IsChecked)
                .ToList();

            if (checkedItems.Count == 0) return;

            var totalSize = checkedItems.Sum(i => i.Item.SizeBytes);
            var mediumCount = checkedItems.Count(i => i.Item.Rule.Risk == RiskLevel.Medium);

            // 二次确认弹窗:即便用户已经勾选,执行前再明确展示一次,
            // 尤其是把"建议逐项确认"类别的数量单独强调出来
            var mediumWarning = mediumCount > 0
                ? $"\n\n其中包含 {mediumCount} 项来自「建议逐项确认」类别,请确认这些内容确实可以清理。"
                : "";

            var confirm = MessageBox.Show(
                $"即将清理 {checkedItems.Count} 项,合计 {FormatSize(totalSize)}。" +
                mediumWarning +
                "\n\n清理后的文件会先移入隔离区保留 24 小时,如有需要可在此期间恢复。\n\n确认执行吗?",
                "确认清理",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            CleanButton.IsEnabled = false;
            ScanStatusText.Text = "正在移入隔离区…";

            var batch = await _quarantine.MoveToQuarantineAsync(checkedItems.Select(i => i.Item));

            MessageBox.Show(
                $"已完成,共释放 {FormatSize(batch.TotalSizeBytes)} 空间。\n" +
                "文件已移入隔离区,24 小时内如需恢复请联系开发者提供的恢复入口。",
                "清理完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // 清理完成后从列表里移除已处理条目,并刷新汇总
            foreach (var vm in checkedItems)
            {
                _lowRiskItems.Remove(vm);
                _mediumRiskItems.Remove(vm);
            }

            ScanStatusText.Text = "清理完成";
            UpdateSelectionSummary();
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size:0.##} {units[unitIndex]}";
        }
    }
}
