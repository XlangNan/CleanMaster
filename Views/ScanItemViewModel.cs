using System.ComponentModel;
using CleanMaster.Core;

namespace CleanMaster.Views
{
    /// <summary>
    /// 特意把"UI 绑定用的可变状态"和"Core 里的扫描结果数据"分开,
    /// 避免 Core 层(业务逻辑/未来可能复用到别的界面或单元测试)
    /// 被迫依赖 WPF 的 INotifyPropertyChanged。
    /// </summary>
    public class ScanItemViewModel : INotifyPropertyChanged
    {
        public ScanResultItem Item { get; }

        public ScanItemViewModel(ScanResultItem item)
        {
            Item = item;
            _isChecked = item.IsChecked;
        }

        public string DisplayName => Item.Rule.DisplayName;
        public string Explanation => Item.Rule.Explanation;
        public string SizeDisplay => Item.SizeDisplay;
        public int FileCount => Item.FileCount;
        public bool AccessDenied => Item.AccessDenied;

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                Item.IsChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
