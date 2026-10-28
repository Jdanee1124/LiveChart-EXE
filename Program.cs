using Avalonia;
using System;

namespace LineChart
{
    /// <summary>
    /// 应用程序启动的物理总入口类
    /// 类似于 C/C++ 中的 main 函数，或者工业控制器上电后的首行引导程序（Bootloader）。
    /// </summary>
    internal sealed class Program
    {
        /// <summary>
        /// [STAThread] 特性：单线程单元（Single-Threaded Apartment）。
        /// 工业与Windows桌面底层规则：所有的桌面UI控件、文件选择对话框、系统剪贴板，
        /// 都必须运行在被标记为 STA 的同一个主线程上，否则弹窗或调用系统底层组件时会直接崩溃报错。
        /// </summary>
        [STAThread]
        public static void Main(string[] args)
        {
            // 1. 调用 BuildAvaloniaApp() 初始化跨平台渲染引擎
            // 2. StartWithClassicDesktopLifetime(args) 开启经典桌面生命周期（负责创建主窗口、监听退出事件、处理托盘与命令行参数）
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        /// <summary>
        /// 配置并组装 Avalonia 跨平台 UI 引擎的大管家方法
        /// 这里就像组装工控机：先配好显卡驱动、字库、调试日志管道，然后再加载具体业务软件。
        /// </summary>
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()      // 指定 App 类为全局应用程序的定义（加载样式、全局资源）
                .UsePlatformDetect()            // 自动侦测当前操作系统平台（在 Windows 下自动选用 Direct3D/Win32/Skia，在 Linux 下自动选用 X11）
                .WithInterFont()                // 注入默认的 Inter 现代矢量字体，保证高分辨率工控屏上文字清晰不发糊
                .LogToTrace();                  // 将 UI 底层发生的警告和致命异常输出到 Visual Studio 的输出/跟踪窗口，方便程序员排查
    }
}
