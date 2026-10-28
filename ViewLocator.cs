using Avalonia.Controls;
using Avalonia.Controls.Templates;
using LineChart.ViewModels;
using System;

namespace LineChart
{
    /// <summary>
    /// MVVM 视图自动路由定位器（View Locator）
    /// 核心职责：充当“导航仪”。当界面需要展示某个 ViewModel（业务数据）时，
    /// 自动根据命名约定，通过反射无缝查找并动态创建出对应的 View（界面控件），实现数据与界面的自动装配。
    /// </summary>
    public class ViewLocator : IDataTemplate
    {
        /// <summary>
        /// 核心构建方法：根据传入的数据对象，动态反射构造出对应的界面控件
        /// </summary>
        /// <param name="data">传入的 ViewModel 实例，例如 MainWindowViewModel</param>
        /// <returns>实例化好的界面控件，例如 MainWindow</returns>
        public Control? Build(object? data)
        {
            // 如果数据源为空，直接返回空控件，防止空指针异常
            if (data is null)
                return null;

            // 命名规则替换：将命名空间里的 "ViewModel" 字符替换为 "View"
            // 例如把 "LineChart.ViewModels.MainWindowViewModel" 
            // 自动推导转换为 "LineChart.Views.MainWindowView"
            var name = data.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
            
            // 利用 C# 反射机制，在当前程序集中通过全限定类名查找对应的真实 C# 类 Type
            var type = Type.GetType(name);

            // 如果找到了对应的界面类型，使用反射无参构造函数动态实例化该界面
            if (type != null)
            {
                return (Control)Activator.CreateInstance(type)!;
            }

            // 如果未找到对应视图，弹出一个红字提示标签，防止整个软件因为找不到界面而直接崩溃闪退
            return new TextBlock { Text = "Not Found: " + name };
        }

        /// <summary>
        /// 匹配准入规则：判断传入的对象是否归属于本路由定位器管辖
        /// </summary>
        /// <param name="data">待检测的数据对象</param>
        /// <returns>只有继承自 ViewModelBase 的业务视图模型，才触发自动导航</returns>
        public bool Match(object? data)
        {
            return data is ViewModelBase;
        }
    }
}
