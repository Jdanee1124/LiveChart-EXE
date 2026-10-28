using CommunityToolkit.Mvvm.ComponentModel;

namespace LineChart.ViewModels
{
    /// <summary>
    /// 所有业务 ViewModel 的公共基类（父类）
    /// 继承自微软官方高质高效的 ObservableObject 类。
    /// 核心职责：提供 INotifyPropertyChanged 属性变更通知机制。
    /// 无论在后台修改了什么变量，都能以极低的损耗通知界面 XAML 自动刷新，
    /// 是整个软件双向数据绑定（Two-Way Binding）的基础地基。
    /// </summary>
    public class ViewModelBase : ObservableObject
    {
        // 继承 ObservableObject 后，即可使用 [ObservableProperty] 等现代源码生成器特性，
        // 告别传统手写繁琐的 OnPropertyChanged("PropertyName") 代码，大幅减少冗余。
    }
}
