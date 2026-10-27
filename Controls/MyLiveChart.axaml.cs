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
    public partial class MyLiveChart : UserControl
    {
        public static readonly DirectProperty<MyLiveChart, object> DataInputProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, object>(nameof(DataInput), o => o.DataInput, (o, v) => o.DataInput = v);

        public static readonly DirectProperty<MyLiveChart, string> ChartTitleProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, string>(nameof(ChartTitle), o => o.ChartTitle, (o, v) => o.ChartTitle = v);

        public static readonly DirectProperty<MyLiveChart, IBrush> LineColorProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, IBrush>(nameof(LineColor), o => o.LineColor, (o, v) => o.LineColor = v);

        public static readonly DirectProperty<MyLiveChart, double> YRangeMaxProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, double>(nameof(YRangeMax), o => o.YRangeMax, (o, v) => o.YRangeMax = v);

        public static readonly DirectProperty<MyLiveChart, string> YUnitProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, string>(nameof(YUnit), o => o.YUnit, (o, v) => o.YUnit = v);

        public static readonly DirectProperty<MyLiveChart, double> UpperLimitProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, double>(nameof(UpperLimit), o => o.UpperLimit, (o, v) => o.UpperLimit = v);

        public static readonly DirectProperty<MyLiveChart, double> LowerLimitProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, double>(nameof(LowerLimit), o => o.LowerLimit, (o, v) => o.LowerLimit = v);

        public static readonly DirectProperty<MyLiveChart, double> StandardValueProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, double>(nameof(StandardValue), o => o.StandardValue, (o, v) => o.StandardValue = v);

        public static readonly DirectProperty<MyLiveChart, bool> IsAutoYScaleProperty =
            AvaloniaProperty.RegisterDirect<MyLiveChart, bool>(nameof(IsAutoYScale), o => o.IsAutoYScale, (o, v) => o.IsAutoYScale = v);

        private object dataInput = 0.0;
        private string chartTitle = "SHXJ-LiveChart";
        private IBrush lineColor = Brushes.Cyan;
        private double yRangeMax = 3000.0;
        private string yUnit = "mm";

        private double upperLimit = 2500.0;
        private double lowerLimit = 500.0;
        private double standardValue = 1500.0;
        private bool _isAutoYScale = true; // 默认启用方案A：微观局部动态自适应缩放

        private readonly List<double> _valueBuffer = new();
        private readonly List<DateTime> _timeBuffer = new();

        private Polyline? _polyline;
        private Canvas? _canvas;
        private Canvas? _xAxisCanvas;
        private TextBlock? _totalCountLabel;
        private Line? _avgLine;
        private TextBlock? _avgLabel;
        private TextBlock? _followStatusBadge;

        private Line? _upperLine, _lowerLine, _stdLine;
        private TextBlock? _upperLabel, _lowerLabel, _stdLabel;

        private TextBlock? _topAxisLabel;
        private TextBlock? _bottomAxisLabel;
        private Button? _autoYBtn;

        // ======= 核心状态控制系统 =======
        private double _xStep = 25.0;          // 图表动态缩放间距（每个点在 X 轴的物理距离，初始25）
        private const double MinXStep = 2.0;    // 无限缩小的极限（数据密恐极限保护）
        private const double MaxXStep = 100.0;  // 放大的极限跨度
        private const double ZoomSpeed = 1.2;   // 每次鼠标滚动一格放缩的倍率

        private double _panX = 0;               // 【灵魂参数】：当前摄影机（视口）的全局 X 轴平移补偿。一切拖放缩放都只改变它。
        private bool _isDragging = false;       // 判断用户当前是否按下了鼠标左键
        private bool _isFollowing = true;       // 判断当前数据产生时，画面是否要自动跟踪到最新数据（80%吸附）
        private Point _lastMousePos;            // 记录上一帧鼠标的拖拽位置，用于计算位移距离

        public event Action? DataUpdated;
        public event Action<string>? ChartCaptured;

        public int DataCount => _valueBuffer.Count;
        public double AverageValue => _valueBuffer.Count > 0 ? _valueBuffer.Average() : 0;
        public double LatestValue => _valueBuffer.Count > 0 ? _valueBuffer[^1] : 0;
        public bool IsFollowing => _isFollowing;

        public MyLiveChart()
        {
            InitializeComponent();
            this.SizeChanged += (s, e) => RedrawChart();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

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
                _canvas.Background = Brushes.Transparent; // Make canvas hit-testable!
            }

            UpdateBadge();
            UpdateAutoYBtnState();
            RedrawChart();
        }

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

        public string YUnit { get => yUnit; set { SetAndRaise(YUnitProperty, ref yUnit, value); RedrawChart(); } }
        public string ChartTitle { get => chartTitle; set { SetAndRaise(ChartTitleProperty, ref chartTitle, value); } }
        public IBrush LineColor { get => lineColor; set { SetAndRaise(LineColorProperty, ref lineColor, value); if (_polyline != null) _polyline.Stroke = lineColor; } }
        public double YRangeMax { get => yRangeMax; set { SetAndRaise(YRangeMaxProperty, ref yRangeMax, value); RedrawChart(); } }

        public double UpperLimit { get => upperLimit; set { SetAndRaise(UpperLimitProperty, ref upperLimit, value); RedrawChart(); } }
        public double LowerLimit { get => lowerLimit; set { SetAndRaise(LowerLimitProperty, ref lowerLimit, value); RedrawChart(); } }
        public double StandardValue { get => standardValue; set { SetAndRaise(StandardValueProperty, ref standardValue, value); RedrawChart(); } }

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

        private void UpdateAutoYBtnState()
        {
            if (_autoYBtn != null)
            {
                if (_isAutoYScale)
                {
                    _autoYBtn.Content = "🎯 微观聚焦";
                    _autoYBtn.Background = new SolidColorBrush(Color.Parse("#0288D1"));
                    _autoYBtn.Foreground = Brushes.White;
                }
                else
                {
                    _autoYBtn.Content = "📐 固定量程";
                    _autoYBtn.Background = new SolidColorBrush(Color.Parse("#424242"));
                    _autoYBtn.Foreground = Brushes.LightGray;
                }
            }
        }

        public void OnToggleAutoYClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            IsAutoYScale = !IsAutoYScale;
        }

        public void PushValue(double val)
        {
            UpdateChart(val.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }

        public void ClearData()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _valueBuffer.Clear();
                _timeBuffer.Clear();
                _panX = 0;
                _isFollowing = true;
                UpdateBadge();
                RedrawChart();
                DataUpdated?.Invoke();
            });
        }

        public void ResetView()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _xStep = 25.0;
                _isFollowing = true;
                UpdateFollowPanX();
                UpdateBadge();
                RedrawChart();
            });
        }

        private void UpdateBadge()
        {
            if (_followStatusBadge != null)
            {
                _followStatusBadge.Text = _isFollowing ? "[跟随中]" : "[手动视角]";
                _followStatusBadge.Foreground = _isFollowing ? Brushes.LimeGreen : Brushes.Orange;
            }
        }

        private void OnResetViewClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ResetView();
        }

        private void OnClearDataClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ClearData();
        }

        /// <summary>
        /// 核心数据入口：接收外部传入的数据字符串（支持 127.0.0.1 回环网络流式数据，如 "1500.2" 或多值 "12.5, 30.1"）。
        /// 支持单点实时跳变与连续数据流追加，包含 10 万点内存防爆保护与 80% 黄金视口自动跟随。
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

                    var parts = currentStr.Split(new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    var newValues = new List<double>();
                    foreach (var part in parts)
                    {
                        if (double.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ||
                            double.TryParse(part.Trim(), out d))
                        {
                            newValues.Add(d);
                        }
                    }

                    if (newValues.Count == 0) return;

                    // 流式增量注入：回环网络（127.0.0.1）或通信模块传来的每个点均实时追加
                    var incomingTimes = new List<DateTime>();
                    foreach (var val in newValues)
                    {
                        var t = DateTime.Now;
                        _valueBuffer.Add(val);
                        _timeBuffer.Add(t);
                        incomingTimes.Add(t);
                    }

                    // 自动流式追加保存至当天的线缆 CSV 文件
                    CSVApi.AppendPoints(ChartTitle, newValues, incomingTimes);

                    // 【内存防爆保护】：当内存积累到十万个数据时，FIFO 切除最老旧数据
                    if (_valueBuffer.Count > 100000)
                    {
                        int removeCount = _valueBuffer.Count - 100000;
                        _valueBuffer.RemoveRange(0, removeCount);
                        _timeBuffer.RemoveRange(0, removeCount);
                    }

                    if (!_isDragging && _isFollowing)
                    {
                        UpdateFollowPanX();
                    }

                    RedrawChart();
                    DataUpdated?.Invoke();
                }
                catch { }
            });
        }

        private double GetCanvasWidth()
        {
            if (_canvas != null && _canvas.Bounds.Width > 10) return _canvas.Bounds.Width;
            if (this.Bounds.Width > 100) return this.Bounds.Width - 80;
            return 800;
        }

        /// <summary>
        /// 跟随模式的摇杆控制算法：自动计算最新数据是否应该平移画布，
        /// 确保新生成的数据永远粘在屏幕 80% 相对位置的最佳视觉观察点。
        /// </summary>
        private void UpdateFollowPanX()
        {
            double w = GetCanvasWidth();
            if (w < 10) return;

            double lastPointX = Math.Max(0, (_valueBuffer.Count - 1) * _xStep);
            double targetX = w * 0.8;

            if (lastPointX < targetX)
            {
                _panX = 0;
            }
            else
            {
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

            // 同步刷新左侧 Y 轴顶底刻度文本
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

            // 如果撞壁，锁死_panX不让他继续滑行
            if (_panX < minPanX && !_isFollowing) _panX = minPanX;
            if (_panX > maxPanX) _panX = maxPanX;

            double spanY = currentMax - currentMin;
            if (spanY < 0.000001) spanY = 1.0;

            var points = new List<Point>();
            var polylinePoints = new List<Point>();
            for (int i = 0; i < _valueBuffer.Count; i++)
            {
                double px = i * _xStep + _panX;
                double safeVal = _valueBuffer[i];
                double py = (h - padding) - ((safeVal - currentMin) / spanY * actualH);
                py = Math.Clamp(py, padding, h - padding);

                var pt = new Point(px, py);
                points.Add(pt);

                // 性能剪裁策略：只把将会在屏幕内或屏幕边缘（左右一定缓冲距）的点，加入 PolyLine 渲染几何体中
                if (px >= -w && px <= w * 2)
                {
                    polylinePoints.Add(pt);
                }
            }
            _polyline.Points = polylinePoints;

            // ========== 动态隐藏标签与密集恐惧症预防 ==========
            _canvas.Children.RemoveAll(_canvas.Children.Where(c => c is Ellipse || c is TextBlock).ToList());
            bool showDetails = _xStep >= 10.0;
            if (showDetails)
            {
                for (int i = 0; i < _valueBuffer.Count; i++)
                {
                    double px = points[i].X;
                    if (px >= -50 && px <= w + 50)
                    {
                        double py = points[i].Y;
                        IBrush dotColor = i % 2 == 0 ? Brushes.Red : Brushes.Orange;
                        var dot = new Ellipse { Width = 6, Height = 6, Fill = dotColor, StrokeThickness = 1, Stroke = Brushes.White };
                        Canvas.SetLeft(dot, px - 3);
                        Canvas.SetTop(dot, py - 3);
                        _canvas.Children.Add(dot);

                        var label = new TextBlock 
                        { 
                            Text = _valueBuffer[i].ToString("F3"), 
                            Foreground = Brushes.Yellow, 
                            FontSize = 10,
                            Width = 70,
                            TextAlignment = TextAlignment.Center
                        };
                        Canvas.SetLeft(label, px - 35);
                        Canvas.SetTop(label, i % 2 == 0 ? py - 22 : py + 8);
                        _canvas.Children.Add(label);
                    }
                }
            }

            _xAxisCanvas.Children.Clear();
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

            if (_totalCountLabel != null)
            {
                _totalCountLabel.Text = $"Total: {_valueBuffer.Count}";
            }

            UpdateLimitLines(h, padding, currentMin, currentMax, actualH, _valueBuffer.Count > 0 ? _valueBuffer.Average() : 0, _valueBuffer.Count > 0);
        }

        /// <summary>
        /// 精准截取折线图核心区域图像，并智能锚定当前屏幕显示的数据真实时间戳（彻底杜绝回看时截图的时间错位 Bug）
        /// </summary>
        public (bool Success, string FilePath, string Message) CaptureChartImage()
        {
            try
            {
                int w = (int)Math.Max(100, this.Bounds.Width);
                int h = (int)Math.Max(100, this.Bounds.Height);

                DateTime visibleDataTime;
                bool isReview = !_isFollowing;

                if (_valueBuffer.Count > 0 && _timeBuffer.Count > 0)
                {
                    if (_isFollowing)
                    {
                        visibleDataTime = _timeBuffer[^1];
                    }
                    else
                    {
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

                var pixelSize = new PixelSize(w, h);
                var dpi = new Vector(96, 96);
                using var rtb = new RenderTargetBitmap(pixelSize, dpi);
                rtb.Render(this);
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

        public void OnCaptureChartClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var res = CaptureChartImage();
            ChartCaptured?.Invoke(res.Message);
        }

        private class LabelCollisionItem
        {
            public TextBlock Label { get; set; } = null!;
            public double ExactY { get; set; }
            public double AdjustedY { get; set; }
            public string Text { get; set; } = "";
        }

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
                double y = (h - padding) - ((val - currentMin) / span * actualH);
                y = Math.Clamp(y, padding, h - padding);
                line.IsVisible = true;
                line.StartPoint = new Point(-1000000, y);
                line.EndPoint = new Point(5000000, y);
            }

            UpdateLineOnly(_upperLine, UpperLimit, true);
            UpdateLineOnly(_lowerLine, LowerLimit, true);
            UpdateLineOnly(_stdLine, StandardValue, true);
            UpdateLineOnly(_avgLine, avgValue, hasData);

            // 2. 收集需要显示的文本标签，用于智能避让重叠
            var activeLabels = new List<LabelCollisionItem>();

            void CollectLabel(TextBlock? label, double val, string prefix, bool show)
            {
                if (label == null) return;
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

            const double MinSpacing = 16.0; // 11.5号字舒适行高避让阈值

            // 迭代多轮进行双向挤压避让
            for (int pass = 0; pass < 6; pass++)
            {
                bool adjusted = false;
                for (int i = 0; i < activeLabels.Count - 1; i++)
                {
                    double overlap = MinSpacing - (activeLabels[i + 1].AdjustedY - activeLabels[i].AdjustedY);
                    if (overlap > 0)
                    {
                        adjusted = true;
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

            // 5. 应用计算出的错开坐标至 Canvas
            foreach (var item in activeLabels)
            {
                item.Label.IsVisible = true;
                item.Label.Text = item.Text;
                Canvas.SetTop(item.Label, item.AdjustedY - 7);
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (_canvas == null) return;

            var currentPos = e.GetPosition(_canvas);
            double zoomFactor = e.Delta.Y > 0 ? ZoomSpeed : 1.0 / ZoomSpeed;
            double oldXStep = _xStep;
            double newXStep = Math.Clamp(_xStep * zoomFactor, MinXStep, MaxXStep);

            if (oldXStep == newXStep) return;
            _xStep = newXStep;

            // Zoom centered on mouse
            _panX = currentPos.X - (currentPos.X - _panX) * (_xStep / oldXStep);
            _isFollowing = false; // Pause follow when user manually zooms

            UpdateBadge();
            RedrawChart();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _isDragging = true;
                _lastMousePos = e.GetPosition(this);
                Cursor = new Cursor(StandardCursorType.Hand);
                e.Pointer.Capture(this);
                e.Handled = true;
                _isFollowing = false;
                UpdateBadge();
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_isDragging && _canvas != null)
            {
                var currentPos = e.GetPosition(this);
                _panX += currentPos.X - _lastMousePos.X;
                _lastMousePos = currentPos;
                RedrawChart();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_isDragging)
            {
                _isDragging = false;
                Cursor = Cursor.Default;
                e.Pointer.Capture(null);

                double w = GetCanvasWidth();
                double lastPointX = Math.Max(0, (_valueBuffer.Count - 1) * _xStep);
                double targetPanX = (w * 0.8) - lastPointX;
                if (targetPanX > 0) targetPanX = 0;

                // 检测是否拖拽到了最末尾(距跟踪目标小于50px)，是的话就恢复跟踪
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
