using Avalonia.Controls;
using LineChart.Controls;
using LineChart.ViewModels;

namespace LineChart.Views
{
    /// <summary>
    /// 主界面代码后置类（Code-Behind）
    /// 核心职责：充当“界面事件连接桥梁”。
    /// 将后台业务模型（MainWindowViewModel）中的通信数据与操作指令，
    /// 精准调度送入高性能自研折线图控件（MyLiveChart）中进行高速渲染。
    /// </summary>
    public partial class MainWindow : Window
    {
        // 引用前台 XAML 中定义的折线图自定义控件实例
        private MyLiveChart? _chartControl;

        /// <summary>
        /// 主窗口构造函数：初始化界面与组件
        /// </summary>
        public MainWindow()
        {
            // 加载 MainWindow.axaml 中声明的视觉布局、按钮、下拉框与网格
            InitializeComponent();
        }

        /// <summary>
        /// 窗口已在屏幕上完全展开并可见后的生命周期回调
        /// 【工业经验】：绝对不要在构造函数里做繁重的跨控件事件绑定，因为此时控件可能尚未完成尺寸测量；
        /// 在 OnOpened 中绑定事件，能 100% 确保界面组件已经就绪，不会发生空引用或尺寸为 0 的 Bug。
        /// </summary>
        protected override void OnOpened(System.EventArgs e)
        {
            base.OnOpened(e);

            // 从 XAML 视觉树中通过 Name="ChartControl" 寻找并持有折线图控件实例
            _chartControl = this.FindControl<MyLiveChart>("ChartControl");

            // 判断当前界面的数据上下文是否是 MainWindowViewModel，且折线图控件已成功寻得
            if (DataContext is MainWindowViewModel vm && _chartControl != null)
            {
                // ========== 桥梁1：收到串口/网口真实测量数据时 ==========
                // 当通信线程收到下位机传来的数据字符串时，触发此事件
                vm.OnDataReceived += data =>
                {
                    // 【线程安全跨线程调度】：网口和串口运行在后台独立线程中，
                    // Windows 严禁非 UI 线程直接触碰界面控件；
                    // 必须通过 Dispatcher.UIThread.Post 将数据安全封送至主 UI 线程排队消费。
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        // 将数据字符串塞入图表的 DataInput 属性，触发实时折线动态绘制与存盘
                        _chartControl.DataInput = data;
                    });
                };

                // ========== 桥梁2：用户点击顶部“复位视角”按钮时 ==========
                vm.OnResetViewRequested += () =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        // 命令折线图将 X 轴缩放间距恢复默认，并开启 80% 黄金视口自动跟随
                        _chartControl.ResetView();
                    });
                };

                // ========== 桥梁3：用户点击顶部“清空数据”按钮时 ==========
                vm.OnClearDataRequested += () =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        // 命令折线图清空内存缓冲区，准备接收新一批工件的测量数据
                        _chartControl.ClearData();
                    });
                };

                // ========== 桥梁4：左下角点击“截图”完成后的状态反馈 ==========
                _chartControl.ChartCaptured += msg =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        // 将截图成功并保存至 D:\MyData 的完整路径显示在窗口最底部的状态栏中
                        vm.StatusMessage = msg;
                    });
                };

                // ========== 桥梁5：图表数据产生跳变刷新后的指标统计联动 ==========
                _chartControl.DataUpdated += () =>
                {
                    // 当图表新增了一个点，立即通知 ViewModel 刷新右下角指标栏：
                    // 同步更新“最新值”、“全天平均值”与“累计数据总点数”
                    vm.UpdateMetrics(_chartControl.LatestValue, _chartControl.AverageValue, _chartControl.DataCount);
                };
            }
        }
    }
}
