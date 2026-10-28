using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LineChart.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace LineChart.ViewModels
{
    /// <summary>
    /// 主界面业务逻辑控制器（ViewModel 核心大管家）
    /// 核心职责：承载整个软件的业务中枢。
    /// 负责通信启停（TCP服务端/客户端/串口）、参数双向绑定、历史配方读写、
    /// 以及与底层折线图控件（MyLiveChart）之间的数据事件分发。
    /// </summary>
    public partial class MainWindowViewModel : ViewModelBase
    {
        // ========== 1. 界面与质量公差数据属性 ==========

        // 当前检测的线缆规格名称（默认“线缆1”，对应按天存盘的 CSV 文件名）
        [ObservableProperty]
        private string _chartTitle = "线缆1";

        // Y 轴物理测量单位（默认“mm”，在左上角与右下角数值后展示）
        [ObservableProperty]
        private string _yUnit = "mm";

        // 固定量程模式下的 Y 轴刻度最大值（默认 3000.0）
        [ObservableProperty]
        private double _yRangeMax = 3000.0;

        // 上限超差报警红线阈值（默认 2500.0）
        [ObservableProperty]
        private double _upperLimit = 2500.0;

        // 下限超差报警红线阈值（默认 500.0）
        [ObservableProperty]
        private double _lowerLimit = 500.0;

        // 工艺标准参考中位线（默认 1500.0，绿色辅助线）
        [ObservableProperty]
        private double _standardValue = 1500.0;

        // ========== 2. 通信模式与动态面板联动 ==========

        // 通信方式下拉框的可选数据源列表（共 4 种模式）
        public List<string> CommTypes { get; } = new()
        {
            "网络通讯 (服务端)",
            "网络通讯 (客户端)",
            "串口通讯",
            "不使用"
        };

        // 当前选中的通信方式（默认第一项“网络通讯 (服务端)”）
        [ObservableProperty]
        private string _selectedCommType = "网络通讯 (服务端)";

        // 界面动态显隐布尔计算属性：当选中模式一（服务端）时，前台仅显示服务端 IP/端口输入框
        public bool IsServerMode => SelectedCommType == "网络通讯 (服务端)";

        // 界面动态显隐布尔计算属性：当选中模式二（客户端）时，前台仅显示目标 IP/端口输入框
        public bool IsClientMode => SelectedCommType == "网络通讯 (客户端)";

        // 界面动态显隐布尔计算属性：当选中模式三（串口）时，前台仅显示串口下拉列表与波特率选择
        public bool IsSerialMode => SelectedCommType == "串口通讯";

        // 界面动态显隐布尔计算属性：当选择“不使用”时，前台显示“(通信已关闭)”灰色提示
        public bool IsNoneMode => SelectedCommType == "不使用";

        // 按钮启用状态：只有在非“不使用”模式下，通信启停大按钮才允许点击
        public bool CanToggleComm => !IsNoneMode;

        // ========== 3. 各通信专属参数属性 ==========

        // 服务端模式专属：本地绑定的服务 IP（默认 127.0.0.1）
        [ObservableProperty]
        private string _serverIp = "127.0.0.1";

        // 服务端模式专属：本地监听的服务端口号（默认 3000）
        [ObservableProperty]
        private int _serverPort = 3000;

        // 客户端模式专属：要连接的目标设备 IP（默认 127.0.0.1）
        [ObservableProperty]
        private string _clientTargetIp = "127.0.0.1";

        // 客户端模式专属：要连接的目标设备端口（默认 3000）
        [ObservableProperty]
        private int _clientTargetPort = 3000;

        // 串口模式专属：动态绑定的当前可用串口下拉列表（动态响应式集合 ObservableCollection）
        public ObservableCollection<string> AvailableSerialPorts { get; } = new();

        // 串口模式专属：支持的常用工业波特率列表
        public List<int> AvailableBaudRates { get; } = new() { 9600, 19200, 38400, 57600, 115200 };

        // 串口模式专属：当前选中的串口号（默认 COM1）
        [ObservableProperty]
        private string _selectedSerialPort = "COM1";

        // 串口模式专属：当前选中的波特率（默认 9600）
        [ObservableProperty]
        private int _selectedBaudRate = 9600;

        // ========== 4. 状态反馈与指示灯属性 ==========

        // 窗口左下角常驻状态消息（提供保姆级操作提示与故障说明）
        [ObservableProperty]
        private string _statusMessage = "服务就绪，点击【启动服务】等待客户端连接";

        // 当前底层通信链路是否处于激活运行状态（true=已启动，false=已停止）
        [ObservableProperty]
        private bool _isCommActive = false;

        // 当前是否有对端设备连入（true=对端在线，false=无连接）
        [ObservableProperty]
        private bool _isClientConnected = false;

        // 顶部通信状态圆点右侧的文本（如 "[已连接]"、"[监听中]"、"[已关闭]"）
        [ObservableProperty]
        private string _connectionBadgeText = "[未启动]";

        // 顶部通信指示圆点的颜色十六进制字符串（绿色 #4CAF50、橙色 #FFA726、灰色 #757575）
        [ObservableProperty]
        private string _connectionBadgeColor = "#757575";

        // 通信主控制按钮的文本（根据当前状态动态切换为“启动服务/停止服务”或“打开串口/关闭串口”）
        [ObservableProperty]
        private string _connectButtonText = "启动服务";

        // 顶部右侧参数配置折叠抽屉面板是否展开
        [ObservableProperty]
        private bool _isSettingsOpen = false;

        // 右下角统计栏：最新一次测量值文本（如 "30.123 mm"）
        [ObservableProperty]
        private string _latestValueText = "--";

        // 右下角统计栏：全天累计平均值文本（如 "30.005 mm"）
        [ObservableProperty]
        private string _averageValueText = "--";

        // 右下角统计栏：全天累计测量点总数计数（如 1024）
        [ObservableProperty]
        private int _totalPoints = 0;

        // ========== 5. 底层服务实例与事件管道 ==========

        // TCP 服务端服务底层对象
        private TcpServerService? _tcpServer;

        // TCP 客户端服务底层对象
        private TcpClientService? _tcpClient;

        // 串口服务底层对象
        private SerialPortService? _serialPortService;

        // 初始化保护标记：在软件刚开机从 JSON 加载参数时设为 true，防止开机瞬间触发重复存盘
        private bool _isInitializing = false;

        // 回调给 View 界面层（MainWindow）的事件通道
        public event Action<string>? OnDataReceived;        // 收到原始数据时触发
        public event Action? OnResetViewRequested;          // 点击复位视角时触发
        public event Action? OnClearDataRequested;          // 点击清空数据时触发

        /// <summary>
        /// ViewModel 构造函数：开机流水线配置
        /// </summary>
        public MainWindowViewModel()
        {
            // 1. 物理探测本机存在的串口硬件，填充下拉列表
            RefreshSerialPortsList();

            // 2. 从本地 config.json 读取上次关机时记忆的参数
            LoadSavedConfig();

            // 3. 根据当前模式初始化启停按钮文字和指示灯颜色
            UpdateCommButtonAndBadge();

            // 4. 挂载属性变化自动存档监听：任何参数被修改，自动触发持久化
            this.PropertyChanged += OnSettingPropertyChanged;
        }

        /// <summary>
        /// 当用户在下拉框切换了通信方式时触发
        /// </summary>
        /// <param name="value">新选中的模式名称</param>
        partial void OnSelectedCommTypeChanged(string value)
        {
            // 1. 安全第一：切换模式前，必须先把上一种正在运行的通信服务彻底关死释放
            StopAllCommunications();

            // 2. 通知界面更新各个显隐控制属性（让不相干的输入框隐藏，相干的输入框显示）
            OnPropertyChanged(nameof(IsServerMode));
            OnPropertyChanged(nameof(IsClientMode));
            OnPropertyChanged(nameof(IsSerialMode));
            OnPropertyChanged(nameof(IsNoneMode));
            OnPropertyChanged(nameof(CanToggleComm));

            // 3. 刷新按钮文字和指示灯
            UpdateCommButtonAndBadge();

            // 4. 更新底部状态栏说明
            if (IsNoneMode)
            {
                StatusMessage = "通信已设置为【不使用】";
            }
            else
            {
                StatusMessage = $"通信模式已切换为：{value}";
            }

            // 5. 将用户的最新模式选择存盘记忆
            if (!_isInitializing)
            {
                SaveCurrentConfig();
            }
        }

        /// <summary>
        /// 状态计算中枢：根据当前选中的通信模式和实际连接状态，精确计算按钮文字和指示灯颜色
        /// </summary>
        private void UpdateCommButtonAndBadge()
        {
            // 模式0：不使用通信
            if (IsNoneMode)
            {
                ConnectButtonText = "未启用";
                ConnectionBadgeText = "[未启用]";
                ConnectionBadgeColor = "#757575"; // 灰色
                return;
            }

            // 模式1：服务端
            if (IsServerMode)
            {
                ConnectButtonText = IsCommActive ? "停止服务" : "启动服务";
                if (!IsCommActive)
                {
                    ConnectionBadgeText = "[未启动]";
                    ConnectionBadgeColor = "#757575"; // 灰色
                }
                else if (IsClientConnected)
                {
                    ConnectionBadgeText = "[已连接]";
                    ConnectionBadgeColor = "#4CAF50"; // 绿色（有下位机已连入）
                }
                else
                {
                    ConnectionBadgeText = "[监听中]";
                    ConnectionBadgeColor = "#FFA726"; // 橙色（服务已开，等待连入）
                }
            }
            // 模式2：客户端
            else if (IsClientMode)
            {
                ConnectButtonText = IsCommActive ? "断开连接" : "连接服务";
                if (!IsCommActive)
                {
                    ConnectionBadgeText = "[未连接]";
                    ConnectionBadgeColor = "#757575"; // 灰色
                }
                else if (IsClientConnected)
                {
                    ConnectionBadgeText = "[已连接]";
                    ConnectionBadgeColor = "#4CAF50"; // 绿色（握手成功）
                }
                else
                {
                    ConnectionBadgeText = "[连接中]";
                    ConnectionBadgeColor = "#FFA726"; // 橙色（正在握手重试中）
                }
            }
            // 模式3：串口通讯
            else if (IsSerialMode)
            {
                ConnectButtonText = IsCommActive ? "关闭串口" : "打开串口";
                if (!IsCommActive)
                {
                    ConnectionBadgeText = "[已关闭]";
                    ConnectionBadgeColor = "#757575"; // 灰色
                }
                else
                {
                    ConnectionBadgeText = $"[已打开]";
                    ConnectionBadgeColor = "#4CAF50"; // 绿色（串口物理占用成功）
                }
            }
        }

        /// <summary>
        /// 重新扫描本机所有串口并更新下拉框
        /// </summary>
        public void RefreshSerialPortsList()
        {
            var ports = SerialPortService.GetAvailablePorts();
            AvailableSerialPorts.Clear();
            foreach (var p in ports)
            {
                AvailableSerialPorts.Add(p);
            }

            // 如果此前选中的串口拔掉了，安全切回第一项
            if (!AvailableSerialPorts.Contains(SelectedSerialPort))
            {
                SelectedSerialPort = AvailableSerialPorts.FirstOrDefault() ?? "(未检测到串口)";
            }
        }

        /// <summary>
        /// 界面上串口旁边的【🔄】刷新按钮绑定的命令
        /// </summary>
        [RelayCommand]
        public void RefreshSerialPorts()
        {
            RefreshSerialPortsList();
            StatusMessage = $"已刷新本机串口列表 (共检测到 {AvailableSerialPorts.Count} 个端口)";
        }

        /// <summary>
        /// 从磁盘 config.json 读取配方参数并覆盖内存属性
        /// </summary>
        private void LoadSavedConfig()
        {
            _isInitializing = true;
            try
            {
                var cfg = ConfigService.Load();
                if (!string.IsNullOrWhiteSpace(cfg.ChartTitle)) ChartTitle = cfg.ChartTitle;
                if (!string.IsNullOrWhiteSpace(cfg.YUnit)) YUnit = cfg.YUnit;
                if (cfg.YRangeMax > 0) YRangeMax = cfg.YRangeMax;
                UpperLimit = cfg.UpperLimit;
                StandardValue = cfg.StandardValue;
                LowerLimit = cfg.LowerLimit;

                if (!string.IsNullOrWhiteSpace(cfg.SelectedCommType) && CommTypes.Contains(cfg.SelectedCommType))
                {
                    SelectedCommType = cfg.SelectedCommType;
                }

                if (!string.IsNullOrWhiteSpace(cfg.ServerIp)) ServerIp = cfg.ServerIp;
                if (cfg.ServerPort > 0) ServerPort = cfg.ServerPort;

                if (!string.IsNullOrWhiteSpace(cfg.ClientTargetIp)) ClientTargetIp = cfg.ClientTargetIp;
                if (cfg.ClientTargetPort > 0) ClientTargetPort = cfg.ClientTargetPort;

                if (!string.IsNullOrWhiteSpace(cfg.SerialPortName)) SelectedSerialPort = cfg.SerialPortName;
                if (cfg.SerialBaudRate > 0) SelectedBaudRate = cfg.SerialBaudRate;
            }
            finally
            {
                _isInitializing = false;
            }
        }

        /// <summary>
        /// 属性变化拦截器：任何用户在界面输入的文字发生变化时被调用
        /// </summary>
        private void OnSettingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // 如果处于开机初始化阶段，直接跳过，防止开机产生无意义的覆写
            if (_isInitializing) return;

            // 过滤判断：只有当属于配方参数的属性发生变化时，才触发写入 config.json
            if (e.PropertyName is nameof(ChartTitle) or nameof(YUnit) or nameof(YRangeMax)
                or nameof(UpperLimit) or nameof(StandardValue) or nameof(LowerLimit)
                or nameof(SelectedCommType)
                or nameof(ServerIp) or nameof(ServerPort)
                or nameof(ClientTargetIp) or nameof(ClientTargetPort)
                or nameof(SelectedSerialPort) or nameof(SelectedBaudRate))
            {
                SaveCurrentConfig();
            }
        }

        /// <summary>
        /// 抓取当前最新的属性状态并保存至磁盘
        /// </summary>
        private void SaveCurrentConfig()
        {
            var cfg = new AppConfig
            {
                ChartTitle = this.ChartTitle,
                YUnit = this.YUnit,
                YRangeMax = this.YRangeMax,
                UpperLimit = this.UpperLimit,
                StandardValue = this.StandardValue,
                LowerLimit = this.LowerLimit,
                SelectedCommType = this.SelectedCommType,
                ServerIp = this.ServerIp,
                ServerPort = this.ServerPort,
                ClientTargetIp = this.ClientTargetIp,
                ClientTargetPort = this.ClientTargetPort,
                SerialPortName = this.SelectedSerialPort,
                SerialBaudRate = this.SelectedBaudRate
            };
            ConfigService.Save(cfg);
        }

        /// <summary>
        /// 核心总开关命令：界面顶部蓝色/红色大按钮绑定的切换通信执行方法
        /// </summary>
        [RelayCommand]
        public async Task ToggleCommAsync()
        {
            if (IsNoneMode) return;

            // 分流调用各自模式的启停方法
            if (IsServerMode)
            {
                await ToggleServerModeAsync();
            }
            else if (IsClientMode)
            {
                await ToggleClientModeAsync();
            }
            else if (IsSerialMode)
            {
                ToggleSerialMode();
            }
        }

        /// <summary>
        /// 模式一：切换 TCP 服务端的开启与停止
        /// </summary>
        private async Task ToggleServerModeAsync()
        {
            if (IsCommActive)
            {
                // 若正在运行，执行停机
                StopAllCommunications();
                StatusMessage = "TCP 服务已停止";
                UpdateCommButtonAndBadge();
            }
            else
            {
                // 若处于停止，执行开机
                StatusMessage = $"正在启动 TCP 服务 (监听 {ServerIp}:{ServerPort})...";
                _tcpServer = new TcpServerService
                {
                    BindIp = ServerIp,
                    Port = ServerPort
                };

                // 挂载数据接收委托：收到数据后送入主界面
                _tcpServer.DataReceived += HandleIncomingData;

                // 挂载状态变更委托
                _tcpServer.StatusChanged += (msg, clientActive) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        StatusMessage = msg;
                        IsClientConnected = clientActive;
                        UpdateCommButtonAndBadge();
                    });
                };

                // 挂载客户端在线状态委托
                _tcpServer.ClientConnectedChanged += (connected) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        IsClientConnected = connected;
                        UpdateCommButtonAndBadge();
                    });
                };

                // 异步启动端口监听
                bool ok = await _tcpServer.StartAsync();
                IsCommActive = ok;
                UpdateCommButtonAndBadge();
            }
        }

        /// <summary>
        /// 模式二：切换 TCP 客户端的连接与断开
        /// </summary>
        private async Task ToggleClientModeAsync()
        {
            if (IsCommActive)
            {
                StopAllCommunications();
                StatusMessage = "已断开 TCP 客户端连接";
                UpdateCommButtonAndBadge();
            }
            else
            {
                StatusMessage = $"正在连接目标 TCP 服务端 [{ClientTargetIp}:{ClientTargetPort}]...";
                _tcpClient = new TcpClientService
                {
                    TargetIp = ClientTargetIp,
                    TargetPort = ClientTargetPort,
                    AutoReconnect = true // 开启断线自动重试重连
                };

                _tcpClient.DataReceived += HandleIncomingData;

                _tcpClient.StatusChanged += (msg, connected) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        StatusMessage = msg;
                        IsClientConnected = connected;
                        UpdateCommButtonAndBadge();
                    });
                };

                _tcpClient.ConnectedChanged += (connected) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        IsClientConnected = connected;
                        UpdateCommButtonAndBadge();
                    });
                };

                bool ok = await _tcpClient.StartAsync();
                IsCommActive = ok;
                UpdateCommButtonAndBadge();
            }
        }

        /// <summary>
        /// 模式三：切换物理串口的打开与关闭
        /// </summary>
        private void ToggleSerialMode()
        {
            if (IsCommActive)
            {
                StopAllCommunications();
                StatusMessage = $"串口 [{SelectedSerialPort}] 已关闭";
                UpdateCommButtonAndBadge();
            }
            else
            {
                StatusMessage = $"正在打开串口 [{SelectedSerialPort}, {SelectedBaudRate} bps]...";
                _serialPortService = new SerialPortService
                {
                    PortName = SelectedSerialPort,
                    BaudRate = SelectedBaudRate
                };

                _serialPortService.DataReceived += HandleIncomingData;

                _serialPortService.StatusChanged += (msg, isOpen) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        StatusMessage = msg;
                        IsCommActive = isOpen;
                        UpdateCommButtonAndBadge();
                    });
                };

                _serialPortService.IsOpenChanged += (isOpen) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        IsCommActive = isOpen;
                        UpdateCommButtonAndBadge();
                    });
                };

                bool ok = _serialPortService.Open();
                IsCommActive = ok;
                UpdateCommButtonAndBadge();
            }
        }

        /// <summary>
        /// 全局停止并销毁当前运行的所有通信服务，确保无孤儿线程残留
        /// </summary>
        private void StopAllCommunications()
        {
            if (_tcpServer != null)
            {
                try { _tcpServer.Stop(); _tcpServer.Dispose(); } catch { }
                _tcpServer = null;
            }

            if (_tcpClient != null)
            {
                try { _tcpClient.Stop(); _tcpClient.Dispose(); } catch { }
                _tcpClient = null;
            }

            if (_serialPortService != null)
            {
                try { _serialPortService.Close(); _serialPortService.Dispose(); } catch { }
                _serialPortService = null;
            }

            IsCommActive = false;
            IsClientConnected = false;
        }

        /// <summary>
        /// 数据到达总汇聚方法：将各个通信线程抛出的数据统一封送至 UI 主线程并对外触发 OnDataReceived
        /// </summary>
        private void HandleIncomingData(string data)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                OnDataReceived?.Invoke(data);
            });
        }

        /// <summary>
        /// 供界面控件调用的指标统计更新公共方法
        /// </summary>
        /// <param name="latest">最新单点测量值</param>
        /// <param name="avg">当前所有点的平均值</param>
        /// <param name="count">累计数据点总数</param>
        public void UpdateMetrics(double latest, double avg, int count)
        {
            LatestValueText = latest.ToString("F3") + " " + YUnit;
            AverageValueText = avg.ToString("F3") + " " + YUnit;
            TotalPoints = count;
        }

        // ========== 6. 快捷按钮绑定的各种用户操作命令 ==========

        /// <summary>
        /// 展开或收起“⚙️ 参数配置”抽屉面板
        /// </summary>
        [RelayCommand]
        public void ToggleSettings()
        {
            IsSettingsOpen = !IsSettingsOpen;
        }

        /// <summary>
        /// 复位视角命令：将缩放倍率和位置还原
        /// </summary>
        [RelayCommand]
        public void ResetView()
        {
            OnResetViewRequested?.Invoke();
        }

        /// <summary>
        /// 清空图表数据命令：重置内存计数器，清除画面波形
        /// </summary>
        [RelayCommand]
        public void ClearData()
        {
            OnClearDataRequested?.Invoke();
            LatestValueText = "--";
            AverageValueText = "--";
            TotalPoints = 0;
        }

        /// <summary>
        /// 📊 查看数据命令：自动寻找今天对应的 CSV 文件，并调用 Windows 默认程序（如 Excel）打开
        /// </summary>
        [RelayCommand]
        public void ViewData()
        {
            try
            {
                // 计算获取今天该线缆对应的 CSV 文件路径
                string csvPath = CSVApi.GetDailyCsvPath(ChartTitle);
                if (System.IO.File.Exists(csvPath))
                {
                    // 启动外部关联进程打开该文件
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = csvPath,
                        UseShellExecute = true // 使用操作系统的文件类型关联
                    });
                    StatusMessage = $"[查看数据] 已打开今日线缆记录: {System.IO.Path.GetFileName(csvPath)}";
                }
                else
                {
                    StatusMessage = $"[暂无数据] 今日尚未产生该线缆数据文件: {System.IO.Path.GetFileName(csvPath)}";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"打开数据文件失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 📂 打开目录命令：在 Windows 资源管理器中打开 D:\MyData 数据存盘文件夹
        /// </summary>
        [RelayCommand]
        public void OpenFolder()
        {
            try
            {
                string path = @"D:\MyData";
                if (!System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.CreateDirectory(path);
                }
                // 调用系统 explorer.exe 浏览该文件夹
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = path,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
