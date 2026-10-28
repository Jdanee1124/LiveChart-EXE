using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LineChart.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LineChart.Controls
{
    /// <summary>
    /// 自研高性能工业级动态折线图用户控件（Core Chart Engine）
    /// 核心职责：承载整个软件的核心视觉呈现。
    /// 包含：微观动态自适应对焦（方案A）、10万点滑动窗口内存防爆、
    /// 80% 黄金视口自动吸附、空气墙拖拽碰撞保护、参考线防重叠避让、以及时间戳对齐的高清截图。
    /// </summary>
    public partial class MyLiveChart : UserControl
    {
        // ========== 1. Avalonia 依赖属性注册（DirectProperty，实现 XAML 双向数据绑定） ==========

        // 外部原始数据输入属性：接收如 "30.123" 或多值 "12.5, 30.1"
        public static readonly DirectProperty<MyLiveChart, object> DataInputProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, object>(nameof(DataInput), o => o.DataInput, (o, v) => o.DataInput = v);

        // 图表顶部标题文本属性（对应当前线缆名称）
        public static readonly DirectProperty<MyLiveChart, string> ChartTitleProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, string>(nameof(ChartTitle), o => o.ChartTitle, (o, v) => o.ChartTitle = v);

        // 折线主体颜色画刷属性（默认青色 Cyan）
        public static readonly DirectProperty<MyLiveChart, IBrush> LineColorProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, IBrush>(nameof(LineColor), o => o.LineColor, (o, v) => o.LineColor = v);

        // Y 轴固定量程上限最大值属性（默认 3000.0）
        public static readonly DirectProperty<MyLiveChart, double> YRangeMaxProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, double>(nameof(YRangeMax), o => o.YRangeMax, (o, v) => o.YRangeMax = v);

        // Y 轴物理测量单位属性（默认 "mm"）
        public static readonly DirectProperty<MyLiveChart, string> YUnitProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, string>(nameof(YUnit), o => o.YUnit, (o, v) => o.YUnit = v);

        // 上限阈值报警红线数值属性
        public static readonly DirectProperty<MyLiveChart, double> UpperLimitProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, double>(nameof(UpperLimit), o => o.UpperLimit, (o, v) => o.UpperLimit = v);

        // 下限阈值报警红线数值属性
        public static readonly DirectProperty<MyLiveChart, double> LowerLimitProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, double>(nameof(LowerLimit), o => o.LowerLimit, (o, v) => o.LowerLimit = v);

        // 标准参考基准线数值属性（绿色参考线）
        public static readonly DirectProperty<MyLiveChart, double> StandardValueProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, double>(nameof(StandardValue), o => o.StandardValue, (o, v) => o.StandardValue = v);

        // 是否启用微观局部自适应自动聚焦属性（默认 true）
        public static readonly DirectProperty<MyLiveChart, bool> IsAutoYScaleProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, bool>(nameof(IsAutoYScale), o => o.IsAutoYScale, (o, v) => o.IsAutoYScale = v);

        // ========== 2. 内部私有业务状态字段 ==========

        private object dataInput = 0.0;
        private string chartTitle = "SHXJ-LiveChart";
        private IBrush lineColor = Brushes.Cyan;
        private double yRangeMax = 3000.0;
        private string yUnit = "mm";

        private double upperLimit = 2500.0;
        private double lowerLimit = 500.0;
        private double standardValue = 1500.0;
        private bool _isAutoYScale = true; // 默认启用方案A：微观局部动态自适应缩放

        // 核心双环缓存池：数值缓存与时间戳缓存（严格一一配对对应）
        private readonly List<double> _valueBuffer = new();
        private readonly List<DateTime> _timeBuffer = new();

        // ========== 3. XAML 界面控件引用指针 ==========

        private Polyline? _polyline;            // 承载高速多段折线的几何图形节点
        private Canvas? _canvas;                // 折线主画布（GraphCanvas）
        private Canvas? _xAxisCanvas;           // 底部 X 轴刻度画布
        private TextBlock? _totalCountLabel;    // 底部右下角 Total 计数文字
        private Line? _avgLine;                 // 橙色平均值虚线
        private TextBlock? _avgLabel;           // 左侧 Y 轴均值标签
        private TextBlock? _followStatusBadge;  // 顶栏右侧“[跟随中]/[手动视角]”状态角标

        private Line? _upperLine, _lowerLine, _stdLine;          // 红色上限线、红色下限线、绿色标准参考线
        private TextBlock? _upperLabel, _lowerLabel, _stdLabel;  // 左侧 Y 轴对应的文本标签

        private TextBlock? _topAxisLabel;       // 左侧 Y 轴最顶部的当前量程最大刻度文本
        private TextBlock? _bottomAxisLabel;    // 左侧 Y 轴最底部的当前量程最小刻度文本
        private Button? _autoYBtn;              // 顶栏右侧【🎯 微观聚焦】切换开关按钮

        // ========== 4. 视口镜头动力学与缩放平移控制系统 ==========

        private double _xStep = 25.0;           // 点间距（每个采样点在 X 轴占用的物理像素宽度，默认25px）
        private const double MinXStep = 2.0;    // 极限缩小保护阈值（防止无限缩小导致像素重叠卡死）
        private const double MaxXStep = 100.0;  // 极限放大跨度阈值（单个点最大展开到 100px）
        private const double ZoomSpeed = 1.2;   // 鼠标滚轮每滚一格的缩放倍率（1.2 倍平滑缩放）

        private double _panX = 0;               // 【灵魂参数】：视口全局横向位移偏移量。所有拖拽与平移均改变此值
        private bool _isDragging = false;        // 用户当前是否按住鼠标左键处于拖拽中
        private bool _isFollowing = true;        // 是否开启“自动跟随最新数据”状态
        private Point _lastMousePos;             // 记录上一帧鼠标的位置，用于差量计算位移

        // 对外开放的回调事件
        public event Action? DataUpdated;               // 数据发生增量并刷新时触发
        public event Action<string>? ChartCaptured;     // 完成图表截图存盘后触发

        // 供外部只读查询的实时指标属性
        public int DataCount => _valueBuffer.Count;                                              // 当前内存有效点总数
        public double AverageValue => _valueBuffer.Count > 0 ? _valueBuffer.Average() : 0;       // 当前所有点的数学平均值
        public double LatestValue => _valueBuffer.Count > 0 ? _valueBuffer[^1] : 0;              // 最新到达的一个实测值
        public bool IsFollowing => _isFollowing;                                                 // 当前是否处于自动跟随状态

        /// <summary>
        /// 折线图控件构造函数
        /// </summary>
        public MyLiveChart()
        {
            InitializeComponent();
            // 监听窗口尺寸变化事件：当用户拉大/拉小窗口时，自适应触发重绘重新排版
            this.SizeChanged += (s, e) => RedrawChart();
        }

        /// <summary>
        /// 加载 MyLiveChart.axaml XAML 视觉模板
        /// </summary>
        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// 当控件被成功挂载到操作系统窗口视觉树上时触发
        /// 在此阶段通过名称一一抓取 XAML 中的元素实例句柄
        /// </summary>
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _polyline = this.FindControl<Polyline>("MainPolyline");
            _canvas = this.FindControl<Canvas>("GraphCanvas");
            _xAxisCanvas = this.FindControl<Canvas>("XAxisCanvas");
            _totalCountLabel = this.FindControl<TextBlock>("TotalCountLabel");
            _avgLine = this.FindControl<Line>("AverageLine");
            _avgLabel = this.FindControl<TextBlock>("AverageLabel");
            _followStatusBadge = this.FindControl<TextBlock>("FollowStatusBadge");

            _upperLine = this.FindControl<Line>("UpperLimitLine");
            _lowerLine = this.FindControl<Line>("LowerLimitLine");
            _stdLine = this.FindControl<Line>("StandardLine");

            _upperLabel = this.FindControl<TextBlock>("UpperLimitLabel");
            _lowerLabel = this.FindControl<TextBlock>("LowerLimitLabel");
            _stdLabel = this.FindControl<TextBlock>("StandardLabel");

            _topAxisLabel = this.FindControl<TextBlock>("TopAxisLabel");
            _bottomAxisLabel = this.FindControl<TextBlock>("BottomAxisLabel");
            _autoYBtn = this.FindControl<Button>("AutoYBtn");

            if (_canvas != null)
            {
                // 关键点：给 Canvas 设置透明背景，保证鼠标点击、按住拖拽时能正常击中命中（Hit-Testable）
                _canvas.Background = Brushes.Transparent;
            }

            // 初始化角标文字与按钮高亮颜色
            UpdateBadge();
            UpdateAutoYBtnState();
            // 首次全量渲染
            RedrawChart();
        }

        /// <summary>
        /// 数据总入口属性包装器
        /// 支持在任意工作线程安全调用：内部自动调度转入 UI 主线程执行
        /// </summary>
        public object DataInput
        {
            get => dataInput;
            set
            {
                if (!Dispatcher.UIThread.CheckAccess())
                {
                    Dispatcher.UIThread.Post(() => DataInput = value);
                    return;
                }
                SetAndRaise(DataInputProperty, ref dataInput, value);
                UpdateChart(value);
            }
        }

        // 各参数包装器：当外部修改了参数，自动触发 SetAndRaise 并重新绘制折线图
        public string YUnit { get => yUnit; set { SetAndRaise(YUnitProperty, ref yUnit, value); RedrawChart(); } }
        public string ChartTitle { get => chartTitle; set { SetAndRaise(ChartTitleProperty, ref chartTitle, value); } }
        public IBrush LineColor { get => lineColor; set { SetAndRaise(LineColorProperty, ref lineColor, value); if (_polyline != null) _polyline.Stroke = lineColor; } }
        public double YRangeMax { get => yRangeMax; set { SetAndRaise(YRangeMaxProperty, ref yRangeMax, value); RedrawChart(); } }

        public double UpperLimit { get => upperLimit; set { SetAndRaise(UpperLimitProperty, ref upperLimit, value); RedrawChart(); } }
        public double LowerLimit { get => lowerLimit; set { SetAndRaise(LowerLimitProperty, ref lowerLimit, value); RedrawChart(); } }
        public double StandardValue { get => standardValue; set { SetAndRaise(StandardValueProperty, ref standardValue, value); RedrawChart(); } }

        /// <summary>
        /// 微观动态自适应聚焦模式开关属性
        /// </summary>
        public bool IsAutoYScale
        {
            get => _isAutoYScale;
            set
            {
                SetAndRaise(IsAutoYScaleProperty, ref _isAutoYScale, value);
                UpdateAutoYBtnState();
                RedrawChart();
            }
        }

        /// <summary>
        /// 联动刷新顶部【🎯 微观聚焦】按钮的外观与文本
        /// </summary>
        private void UpdateAutoYBtnState()
        {
            if (_autoYBtn != null)
            {
                if (_isAutoYScale)
                {
                    _autoYBtn.Content = "🎯 微观聚焦";
                    _autoYBtn.Background = new SolidColorBrush(Color.Parse("#0288D1")); // 蓝色高亮
                    _autoYBtn.Foreground = Brushes.White;
                }
                else
                {
                    _autoYBtn.Content = "📐 固定量程";
                    _autoYBtn.Background = new SolidColorBrush(Color.Parse("#424242")); // 灰色朴素
                    _autoYBtn.Foreground = Brushes.LightGray;
                }
            }
        }

        /// <summary>
        /// 点击顶栏切换按钮时触发
        /// </summary>
        public void OnToggleAutoYClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            IsAutoYScale = !IsAutoYScale;
        }

        /// <summary>
        /// 供 C# 后台代码直接塞入单一浮点数的快捷方法
        /// </summary>
        public void PushValue(double val)
        {
            UpdateChart(val.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 清空折线图数据逻辑
        /// </summary>
        public void ClearData()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _valueBuffer.Clear();       // 清空实测数值缓存
                _timeBuffer.Clear();        // 清空时间戳缓存
                _panX = 0;                  // 视口复位
                _isFollowing = true;        // 恢复跟随
                UpdateBadge();
                RedrawChart();
                DataUpdated?.Invoke();      // 触发更新回调
            });
        }

        /// <summary>
        /// 复位视角逻辑：把时间轴缩放倍率和位置还原
        /// </summary>
        public void ResetView()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _xStep = 25.0;              // 间距恢复默认 25px
                _isFollowing = true;        // 恢复自动跟随
                UpdateFollowPanX();         // 重新计算吸附
                UpdateBadge();
                RedrawChart();
            });
        }

        /// <summary>
        /// 刷新顶栏右侧“[跟随中]/[手动视角]”提示角标
        /// </summary>
        private void UpdateBadge()
        {
            if (_followStatusBadge != null)
            {
                _followStatusBadge.Text = _isFollowing ? "[跟随中]" : "[手动视角]";
                _followStatusBadge.Foreground = _isFollowing ? Brushes.LimeGreen : Brushes.Orange;
            }
        }

        /// <summary>
        /// 顶栏“复位视角”按钮点击事件处理器
        /// </summary>
        private void OnResetViewClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ResetView();
        }

        /// <summary>
        /// 顶栏“清空”按钮点击事件处理器
        /// </summary>
        private void OnClearDataClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ClearData();
        }

        /// <summary>
        /// 核心数据解析与流式吞吐流水线
        /// 接收外部传入的任意格式字符串，如 "30.125"、"12.3, 45.6\n78.9"
        /// 具备多值解析、时间锚定、实时写盘、10万点滑动窗口切除与黄金视口吸附。
        /// </summary>
        private void UpdateChart(object rawInput)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (rawInput == null) return;
                    string currentStr = rawInput.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(currentStr)) return;

                    // 1. 全局多符号拆分：兼容逗号、分号、空格、Tab制表符以及换行符
                    var parts = currentStr.Split(new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    var newValues = new List<double>();
                    foreach (var part in parts)
                    {
                        // 优先以不变区域文化解析浮点数（防止某些欧洲操作系统逗号当小数点的Bug）
                        if (double.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ||
                            double.TryParse(part.Trim(), out d))
                        {
                            newValues.Add(d);
                        }
                    }

                    if (newValues.Count == 0) return;

                    // 2. 为本次进入的每一个数值打上毫秒级的到达时间戳
                    var incomingTimes = new List<DateTime>();
                    foreach (var val in newValues)
                    {
                        var t = DateTime.Now;
                        _valueBuffer.Add(val);
                        _timeBuffer.Add(t);
                        incomingTimes.Add(t);
                    }

                    // 3. 【核心第一步】：数据先安全落盘追加写入当日 CSV 文件中，保证 0 漏点
                    CSVApi.AppendPoints(ChartTitle, newValues, incomingTimes);

                    // 4. 【内存防爆保护】：采用 FIFO（先进先出）队列滑动窗口。
                    // 当内存积累超过 10 万个点时，自动切除最老旧的数据，保持常驻内存永远只有 2MB 左右！
                    if (_valueBuffer.Count > 100000)
                    {
                        int removeCount = _valueBuffer.Count - 100000;
                        _valueBuffer.RemoveRange(0, removeCount);
                        _timeBuffer.RemoveRange(0, removeCount);
                    }

                    // 5. 如果处于跟随模式，且用户没有按住鼠标拖拽，自动平移视口追随最新数据
                    if (!_isDragging && _isFollowing)
                    {
                        UpdateFollowPanX();
                    }

                    // 6. 执行界面重绘
                    RedrawChart();
                    // 7. 通知上层业务刷新指标统计
                    DataUpdated?.Invoke();
                }
                catch { }
            });
        }

        /// <summary>
        /// 安全计算获取折线画布的有效物理像素宽度
        /// </summary>
        private double GetCanvasWidth()
        {
            if (_canvas != null && _canvas.Bounds.Width > 10) return _canvas.Bounds.Width;
            if (this.Bounds.Width > 100) return this.Bounds.Width - 80;
            return 800; // 兜底默认宽度
        }

        /// <summary>
        /// 跟随模式的摇杆控制算法：自动计算最新数据是否应该平移画布，
        /// 确保新生成的数据永远粘在屏幕 80% 相对位置的最佳视觉观察点。
        /// </summary>
        private void UpdateFollowPanX()
        {
            double w = GetCanvasWidth();
            if (w < 10) return;

            // 计算最新一个数据点在全局世界坐标系下的绝对物理 X 位置
            double lastPointX = Math.Max(0, (_valueBuffer.Count - 1) * _xStep);
            // 黄金观察点：屏幕右侧 80% 处（留出右侧 20% 空白展示未来走向）
            double targetX = w * 0.8;

            // 数据量还很少时，停在最左侧不动
            if (lastPointX < targetX)
            {
                _panX = 0;
            }
            else
            {
                // 数据超出了 80% 范围，平移视口将最新点锁定在 80%
                _panX = targetX - lastPointX;
            }
        }

        /// <summary>
        /// 核心渲染引擎：基于屏幕绝对坐标系重新结算所有的图形节点位置。
        /// 包含自适应微观局部动态聚焦算法 (方案A)、空气墙物理碰撞检测、以及视口外节点裁剪优化。
        /// </summary>
        private void RedrawChart()
        {
            if (_canvas == null || _polyline == null || _xAxisCanvas == null) return;

            if (_canvas.Background == null) _canvas.Background = Brushes.Transparent;
            _canvas.RenderTransform = null;
            _xAxisCanvas.RenderTransform = null;

            _polyline.StrokeThickness = 2.0;

            double w = GetCanvasWidth();
            double h = _canvas.Bounds.Height;
            if (w < 10 || h < 10) return;

            // 画布上下各预留 8px 像素安全边距，防止折线恰好顶格时被边缘线裁切
            double padding = 8.0;
            double actualH = h - 2 * padding;

            // ========== 核心算法：微观局部动态自适应缩放 (方案 A) ==========
            double currentMin = 0.0;
            double currentMax = YRangeMax > 0.1 ? YRangeMax : 1000.0;

            if (_isAutoYScale)
            {
                // 1. 提取当前视口内可见数据点
                var visibleVals = new List<double>();
                for (int i = 0; i < _valueBuffer.Count; i++)
                {
                    double px = i * _xStep + _panX;
                    // 只把位于屏幕内（带左右各50px富余度）的点纳入极值统计
                    if (px >= -50 && px <= w + 50)
                    {
                        visibleVals.Add(_valueBuffer[i]);
                    }
                }

                // 视口若无点（如刚启动或拖出视线），取缓冲区尾部数据
                if (visibleVals.Count == 0 && _valueBuffer.Count > 0)
                {
                    visibleVals.AddRange(_valueBuffer.TakeLast(50));
                }

                if (visibleVals.Count > 0)
                {
                    double minV = visibleVals.Min();
                    double maxV = visibleVals.Max();
                    double avgV = visibleVals.Average();

                    // 判断辅助线（标准值、上下限）是否属于当前数据的量级邻域，避免远隔千里（如默认 2500）的无效预设撑爆量程
                    double thresholdSpan = Math.Max(2.0, Math.Max(maxV - minV, Math.Abs(avgV) * 0.4));

                    bool stdRelevant = Math.Abs(StandardValue - avgV) <= thresholdSpan;
                    bool upperRelevant = Math.Abs(UpperLimit - avgV) <= thresholdSpan;
                    bool lowerRelevant = Math.Abs(LowerLimit - avgV) <= thresholdSpan;

                    // 若工艺标准和上下限设定在合理公差范围内，量程必须包容它们，让工人看得见公差红线
                    if (stdRelevant)
                    {
                        minV = Math.Min(minV, StandardValue);
                        maxV = Math.Max(maxV, StandardValue);
                    }
                    if (upperRelevant)
                    {
                        maxV = Math.Max(maxV, UpperLimit);
                    }
                    if (lowerRelevant)
                    {
                        minV = Math.Min(minV, LowerLimit);
                    }

                    double span = maxV - minV;
                    // 防止完全一条水平直线时跨度为0导致除以零错误
                    if (span < 0.05)
                    {
                        span = 0.1;
                        double center = (minV + maxV) / 2.0;
                        minV = center - 0.05;
                        maxV = center + 0.05;
                    }

                    // 上下各留 15% 呼吸空间，使波形跳动饱满且美观
                    double margin = span * 0.15;
                    currentMin = minV - margin;
                    currentMax = maxV + margin;
                }
                else
                {
                    // 暂无数据时，若配置了标准值，聚焦于标准值附近
                    if (StandardValue > 0)
                    {
                        currentMin = Math.Max(0, StandardValue * 0.9);
                        currentMax = StandardValue * 1.1;
                    }
                }
            }

            // 同步刷新左侧 Y 轴顶底刻度文本（带 3 位小数精准反馈）
            if (_topAxisLabel != null)
            {
                _topAxisLabel.Text = currentMax.ToString("F3");
            }
            if (_bottomAxisLabel != null)
            {
                _bottomAxisLabel.Text = currentMin.ToString("F3");
            }

            // ========== 拖拽空气墙物理规则碰撞检测 ==========
            double lastPointX = Math.Max(0, (_valueBuffer.Count - 1) * _xStep);

            // 【规则一向右拖壁垒】：向右拖拽查看老数据时，最多拖到最新数据卡在80%的地方（限制看未来空气）
            double minPanX = (w * 0.8) - lastPointX;
            if (minPanX > 0) minPanX = 0;

            // 【规则二向左拖壁垒】：向左滑回看新数据时，最多只能拖到第一个点到达Y轴（不允许轴分离）
            double maxPanX = 0;

            // 如果撞壁，锁死 _panX 不让它继续滑行产生视口脱节
            if (_panX < minPanX && !_isFollowing) _panX = minPanX;
            if (_panX > maxPanX) _panX = maxPanX;

            double spanY = currentMax - currentMin;
            if (spanY < 0.000001) spanY = 1.0;

            var points = new List<Point>();
            var polylinePoints = new List<Point>();
            for (int i = 0; i < _valueBuffer.Count; i++)
            {
                // 计算该数据点的 X 轴坐标（序号 * 步长 + 平移偏移量）
                double px = i * _xStep + _panX;
                double safeVal = _valueBuffer[i];
                // 【核心映射公式】：依据当前动态极值将数值等比例投射到画布高度上（底部为 currentMin，顶部为 currentMax）
                double py = (h - padding) - ((safeVal - currentMin) / spanY * actualH);
                py = Math.Clamp(py, padding, h - padding);

                var pt = new Point(px, py);
                points.Add(pt);

                // 【工业级性能剪裁策略】：只把在屏幕可见范围内（包含左右缓冲距）的点加入 PolyLine 渲染几何体中，
                // 屏幕外的 10 万个点直接忽略，实现超低 CPU/GPU 负载！
                if (px >= -w && px <= w * 2)
                {
                    polylinePoints.Add(pt);
                }
            }
            // 将点集赋给 Avalonia 几何多段线
            _polyline.Points = polylinePoints;

            // ========== 动态隐藏标签与密集恐惧症预防 ==========
            // 每次重绘先清空旧的圆点和文本标签
            _canvas.Children.RemoveAll(_canvas.Children.Where(c => c is Ellipse || c is TextBlock).ToList());
            
            // 只有当放得足够大（每个点间距 >= 10px）时，才显示圆点和具体数值，避免缩小时黑乎乎一片
            bool showDetails = _xStep >= 10.0;
            if (showDetails)
            {
                for (int i = 0; i < _valueBuffer.Count; i++)
                {
                    double px = points[i].X;
                    if (px >= -50 && px <= w + 50)
                    {
                        double py = points[i].Y;
                        // 圆点双色交替（偶数点红色，奇数点橙色，带 1px 白色描边）
                        IBrush dotColor = i % 2 == 0 ? Brushes.Red : Brushes.Orange;
                        var dot = new Ellipse { Width = 6, Height = 6, Fill = dotColor, StrokeThickness = 1, Stroke = Brushes.White };
                        Canvas.SetLeft(dot, px - 3);
                        Canvas.SetTop(dot, py - 3);
                        _canvas.Children.Add(dot);

                        // 数据文字与圆点颜色严格一致（偶数点为红圆点+红字，奇数点为橙圆点+橙字，字重加粗提高黑色画布对比度）
                        var label = new TextBlock 
                        { 
                            Text = _valueBuffer[i].ToString("F3"), 
                            Foreground = dotColor, 
                            FontSize = 10,
                            FontWeight = FontWeight.SemiBold,
                            Width = 70,
                            TextAlignment = TextAlignment.Center
                        };
                        Canvas.SetLeft(label, px - 35);
                        // 上下错位排列：偶数点数值标在圆点上方（-22px），奇数点数值标在圆点下方（+8px），彻底错开不挤占
                        Canvas.SetTop(label, i % 2 == 0 ? py - 22 : py + 8);
                        _canvas.Children.Add(label);
                    }
                }
            }

            // ========== 底部 X 轴采样序号生成 ==========
            _xAxisCanvas.Children.Clear();
            // 动态调节刻度疏密度：间距越小时跳着绘制（例如每隔 5 个点画一个刻度序号）
            int stepCount = _xStep < 40 ? (int)Math.Ceiling(40 / _xStep) : 1;
            for (int i = 0; i < _valueBuffer.Count; i += stepCount)
            {
                double px = i * _xStep + _panX;
                if (px >= -50 && px <= w + 50)
                {
                    var label = new TextBlock { Text = (i + 1).ToString(), Foreground = Brushes.Gray, FontSize = 10, Width = 40, TextAlignment = TextAlignment.Center };
                    Canvas.SetLeft(label, px - 20);
                    Canvas.SetTop(label, 2);
                    _xAxisCanvas.Children.Add(label);
                }
            }

            // 刷新右下角 Total 统计数
            if (_totalCountLabel != null)
            {
                _totalCountLabel.Text = $"Total: {_valueBuffer.Count}";
            }

            // 调度更新工艺上下限与均值辅助线（带碰撞避让算法）
            UpdateLimitLines(h, padding, currentMin, currentMax, actualH, _valueBuffer.Count > 0 ? _valueBuffer.Average() : 0, _valueBuffer.Count > 0);
        }

        /// <summary>
        /// 精准截取折线图核心区域图像，并智能锚定当前屏幕显示的数据真实时间戳（彻底杜绝回看时截图的时间错位 Bug）
        /// </summary>
        public (bool Success, string FilePath, string Message) CaptureChartImage()
        {
            try
            {
                // 获取当前控件在屏幕上的物理尺寸
                int w = (int)Math.Max(100, this.Bounds.Width);
                int h = (int)Math.Max(100, this.Bounds.Height);

                DateTime visibleDataTime;
                bool isReview = !_isFollowing; // 手动视角即为回看模式

                if (_valueBuffer.Count > 0 && _timeBuffer.Count > 0)
                {
                    if (_isFollowing)
                    {
                        // 跟随模式：取最新一个到达的数据时间戳
                        visibleDataTime = _timeBuffer[^1];
                    }
                    else
                    {
                        // 回看模式：利用逆向物理公式算出当前视口最右侧正在展示的那一个历史点的真实时间！
                        double canvasWidth = GetCanvasWidth();
                        int lastVisibleIndex = (int)Math.Floor((canvasWidth - _panX) / _xStep);
                        lastVisibleIndex = Math.Clamp(lastVisibleIndex, 0, _timeBuffer.Count - 1);
                        visibleDataTime = _timeBuffer[lastVisibleIndex];
                    }
                }
                else
                {
                    visibleDataTime = DateTime.Now;
                }

                // 规范化文件名：带线缆名称、年月日、时分秒
                string safeTitle = CSVApi.GetSafeFileName(ChartTitle);
                string timePart = visibleDataTime.ToString("HHmmss");
                string fileName = isReview
                    ? $"{DateTime.Now:yyyy-MM-dd}_{safeTitle}_回看{timePart}.png"
                    : $"{DateTime.Now:yyyy-MM-dd}_{safeTitle}_{timePart}.png";

                string dir = CSVApi.SaveDirectory;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string fullPath = System.IO.Path.Combine(dir, fileName);

                // 使用 Avalonia 专属位图渲染器 RenderTargetBitmap 进行高保真截图
                var pixelSize = new PixelSize(w, h);
                var dpi = new Vector(96, 96);
                using var rtb = new RenderTargetBitmap(pixelSize, dpi);
                rtb.Render(this);
                // 保存为标准 PNG 高清图片格式
                rtb.Save(fullPath);

                string modeTip = isReview 
                    ? $"[回看快照 对应数据时间 {visibleDataTime:HH:mm:ss}]" 
                    : $"[实时快照 对应数据时间 {visibleDataTime:HH:mm:ss}]";

                return (true, fullPath, $"{modeTip} 已保存至: {fileName}");
            }
            catch (Exception ex)
            {
                return (false, "", $"截图失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 左下角“📷 截取图表”按钮点击事件
        /// </summary>
        public void OnCaptureChartClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var res = CaptureChartImage();
            ChartCaptured?.Invoke(res.Message);
        }

        /// <summary>
        /// 用于辅助线文本标签碰撞检测的中间数据结构
        /// </summary>
        private class LabelCollisionItem
        {
            public TextBlock Label { get; set; } = null!; // 对应的文本控件
            public double ExactY { get; set; }           // 真实物理数学 Y 坐标
            public double AdjustedY { get; set; }        // 避让碰撞后调整的 Y 坐标
            public string Text { get; set; } = "";       // 展示的文本内容（如“标准: 30.000”）
        }

        /// <summary>
        /// 辅助线更新与 1D Relaxation 智能标签碰撞避让算法
        /// 核心设计：横线（Line）必须严谨保持在真实物理坐标上；
        /// 文本标签（Label）如果发生相互重叠遮挡（间距 < 16px），自动沿 Y 轴平滑上下推移错开！
        /// </summary>
        private void UpdateLimitLines(double h, double padding, double currentMin, double currentMax, double actualH, double avgValue, bool hasData)
        {
            if (_canvas == null) return;
            double span = currentMax - currentMin;
            if (span < 0.000001) span = 1.0;

            // 1. 物理辅助横线更新（保持真实物理位置不变）
            void UpdateLineOnly(Line? line, double val, bool show)
            {
                if (line == null) return;
                // 仅当数值在当前视口范围（包括边缘 5% 缓冲区）内时才绘制，否则隐藏
                if (!show || val > currentMax + span * 0.05 || val < currentMin - span * 0.05)
                {
                    line.IsVisible = false;
                    return;
                }
                // 真实物理 Y 坐标映射
                double y = (h - padding) - ((val - currentMin) / span * actualH);
                y = Math.Clamp(y, padding, h - padding);
                line.IsVisible = true;
                // 横贯整个画布绘制无限长水平虚线
                line.StartPoint = new Point(-1000000, y);
                line.EndPoint = new Point(5000000, y);
            }

            // 更新四条辅助线的位置
            UpdateLineOnly(_upperLine, UpperLimit, true);
            UpdateLineOnly(_lowerLine, LowerLimit, true);
            UpdateLineOnly(_stdLine, StandardValue, true);
            UpdateLineOnly(_avgLine, avgValue, hasData);

            // 2. 收集需要显示的文本标签，用于智能避让重叠
            var activeLabels = new List<LabelCollisionItem>();

            void CollectLabel(TextBlock? label, double val, string prefix, bool show)
            {
                if (label == null) return;
                // 不在视口范围内的标签不显示
                if (!show || val > currentMax + span * 0.05 || val < currentMin - span * 0.05)
                {
                    label.IsVisible = false;
                    return;
                }
                double y = (h - padding) - ((val - currentMin) / span * actualH);
                y = Math.Clamp(y, padding, h - padding);
                activeLabels.Add(new LabelCollisionItem
                {
                    Label = label,
                    ExactY = y,
                    AdjustedY = y,
                    Text = prefix + val.ToString("F3")
                });
            }

            CollectLabel(_upperLabel, UpperLimit, "上限: ", true);
            CollectLabel(_lowerLabel, LowerLimit, "下限: ", true);
            CollectLabel(_stdLabel, StandardValue, "标准: ", true);
            CollectLabel(_avgLabel, avgValue, "均值: ", hasData);

            // 隐藏未在 activeLabels 中的所有辅助标签
            if (_upperLabel != null && !activeLabels.Any(x => x.Label == _upperLabel)) _upperLabel.IsVisible = false;
            if (_lowerLabel != null && !activeLabels.Any(x => x.Label == _lowerLabel)) _lowerLabel.IsVisible = false;
            if (_stdLabel != null && !activeLabels.Any(x => x.Label == _stdLabel)) _stdLabel.IsVisible = false;
            if (_avgLabel != null && !activeLabels.Any(x => x.Label == _avgLabel)) _avgLabel.IsVisible = false;

            if (activeLabels.Count == 0) return;

            // 3. 标签智能避让算法 (1D Relaxation Collision Resolution)
            // 按照 Y 坐标从上至下排序（Y 越小越靠近顶部）
            activeLabels.Sort((a, b) => a.ExactY.CompareTo(b.ExactY));

            const double MinSpacing = 16.0; // 11.5号字舒适行高避让阈值（至少间隔16px）

            // 迭代多轮进行双向挤压避让松弛
            for (int pass = 0; pass < 6; pass++)
            {
                bool adjusted = false;
                for (int i = 0; i < activeLabels.Count - 1; i++)
                {
                    double overlap = MinSpacing - (activeLabels[i + 1].AdjustedY - activeLabels[i].AdjustedY);
                    if (overlap > 0)
                    {
                        adjusted = true;
                        // 重叠了：上面的往上推一半，下面的往下挤一半
                        double halfShift = overlap / 2.0;
                        activeLabels[i].AdjustedY -= halfShift;
                        activeLabels[i + 1].AdjustedY += halfShift;
                    }
                }
                if (!adjusted) break;
            }

            // 4. 边界吸附限制：不遮挡顶部刻度（TopAxisLabel ~32px）与底部刻度（BottomAxisLabel ~h - 22px）
            double topLimit = 32.0;
            double bottomLimit = h - 22.0;

            // 顶部向下推移边界保护
            for (int i = 0; i < activeLabels.Count; i++)
            {
                if (activeLabels[i].AdjustedY < topLimit)
                {
                    activeLabels[i].AdjustedY = topLimit;
                }
                if (i > 0 && activeLabels[i].AdjustedY < activeLabels[i - 1].AdjustedY + MinSpacing)
                {
                    activeLabels[i].AdjustedY = activeLabels[i - 1].AdjustedY + MinSpacing;
                }
            }

            // 底部向上回拉边界保护
            for (int i = activeLabels.Count - 1; i >= 0; i--)
            {
                if (activeLabels[i].AdjustedY > bottomLimit)
                {
                    activeLabels[i].AdjustedY = bottomLimit;
                }
                if (i < activeLabels.Count - 1 && activeLabels[i].AdjustedY > activeLabels[i + 1].AdjustedY - MinSpacing)
                {
                    activeLabels[i].AdjustedY = activeLabels[i + 1].AdjustedY - MinSpacing;
                }
            }

            // 5. 将计算出的避让坐标安全赋值到 Canvas 上
            foreach (var item in activeLabels)
            {
                item.Label.IsVisible = true;
                item.Label.Text = item.Text;
                Canvas.SetTop(item.Label, item.AdjustedY - 7); // -7 像素居中对齐参考虚线
            }
        }

        /// <summary>
        /// 鼠标滚轮缩放事件响应
        /// 以鼠标指针当前所在的物理像素位置为不动中心点，进行精准以鼠为轴放大缩小。
        /// </summary>
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (_canvas == null) return;

            // 获取鼠标在画布中的当前坐标
            var currentPos = e.GetPosition(_canvas);
            // 滚轮向上放大，滚轮向下缩小
            double zoomFactor = e.Delta.Y > 0 ? ZoomSpeed : 1.0 / ZoomSpeed;
            double oldXStep = _xStep;
            // 限制缩放步长在安全边界内
            double newXStep = Math.Clamp(_xStep * zoomFactor, MinXStep, MaxXStep);

            if (oldXStep == newXStep) return;
            _xStep = newXStep;

            // 以鼠标位置为锚点重新结算 _panX，保证放缩时鼠标下的数据点位置纹丝不动！
            _panX = currentPos.X - (currentPos.X - _panX) * (_xStep / oldXStep);
            _isFollowing = false; // 用户主动滚动滚轮放大时，自动切为手动视角，暂停吸附跟随

            UpdateBadge();
            RedrawChart();
        }

        /// <summary>
        /// 鼠标左键按下事件：开启拖拽平移模式
        /// </summary>
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _isDragging = true;
                _lastMousePos = e.GetPosition(this);
                // 鼠标指针变为“抓手”手势
                Cursor = new Cursor(StandardCursorType.Hand);
                // 捕获鼠标指针，防止拖拽过快甩出窗口导致事件丢失
                e.Pointer.Capture(this);
                e.Handled = true;
                _isFollowing = false; // 拖拽时立即暂停自动跟随，方便工人仔细检查历史波形
                UpdateBadge();
            }
        }

        /// <summary>
        /// 鼠标在界面上滑动移动事件：横向拖拽平移图表
        /// </summary>
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_isDragging && _canvas != null)
            {
                var currentPos = e.GetPosition(this);
                // 视口横向偏移量累加两帧之间的鼠标位移差
                _panX += currentPos.X - _lastMousePos.X;
                _lastMousePos = currentPos;
                // 重绘图表
                RedrawChart();
            }
        }

        /// <summary>
        /// 鼠标左键松开释放事件：结束拖拽，并进行边界与跟随吸附判断
        /// </summary>
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_isDragging)
            {
                _isDragging = false;
                Cursor = Cursor.Default; // 恢复默认箭头光标
                e.Pointer.Capture(null); // 释放鼠标捕获

                double w = GetCanvasWidth();
                double lastPointX = Math.Max(0, (_valueBuffer.Count - 1) * _xStep);
                double targetPanX = (w * 0.8) - lastPointX;
                if (targetPanX > 0) targetPanX = 0;

                // 智能判定：如果用户把画面拖回了最右侧最新数据处（距离小于 50px），
                // 自动恢复【跟随中】模式，省去用户再去手动点一次“复位视角”按钮的繁琐。
                if (Math.Abs(_panX - targetPanX) < 50)
                {
                    _isFollowing = true;
                    _panX = targetPanX;
                }
                UpdateBadge();
                RedrawChart();
            }
        }
    }
}
