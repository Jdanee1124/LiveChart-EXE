using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using LineChart.ViewModels;
using LineChart.Views;
using System.Linq;

namespace LineChart
{
    /// <summary>
    /// 全局应用程序生命周期与样式配置类
    /// 负责接管软件从冷启动、加载全局样式、实例化主界面到最终退出的全生命周期管理。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 第一阶段：初始化方法
        /// 当程序启动时由 Avalonia 引擎第一个调用。
        /// </summary>
        public override void Initialize()
        {
            // 解析并加载 App.axaml 中声明的全局资源（例如 Fluent 深色暗黑主题、全局矢量画刷、图标等）
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// 第二阶段：框架初始化完成事件
        /// 当底层的平台窗口系统（Win32）准备就绪后触发，此时正是创建并弹出主界面的最佳时机。
        /// </summary>
        public override void OnFrameworkInitializationCompleted()
        {
            // 判断当前程序是否以标准的“桌面窗口模式”运行（区别于移动端或嵌入式无头模式）
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // 【工业避坑点】：移除 Avalonia 原生多余的数据验证插件
                // 微软官方的 CommunityToolkit.Mvvm 库已经自带了严密的属性校验机制；
                // 如果不移除 Avalonia 默认的 DataAnnotations 插件，两者会在界面输入框上打架，产生重复报红或重复抛异常的 Bug。
                var dataValidationPluginsToRemove =
                    BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

                // 遍历并彻底注销重复的验证插件，保证属性校验纯净高效
                foreach (var plugin in dataValidationPluginsToRemove)
                {
                    BindingPlugins.DataValidators.Remove(plugin);
                }

                // 创建主窗口 MainWindow，并为其装配数据核心（DataContext = MainWindowViewModel）
                // 这一步正是经典 MVVM 架构中 View（视图）与 ViewModel（视图模型）产生物理绑定的“交钥匙时刻”
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(),
                };
            }

            // 调用父类基类逻辑，完成标准初始化的收尾动作
            base.OnFrameworkInitializationCompleted();
        }
    }
}
