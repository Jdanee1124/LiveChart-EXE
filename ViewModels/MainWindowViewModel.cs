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
    public partial class MainWindowViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string _chartTitle = "线缆1";

        [ObservableProperty]
        private string _yUnit = "mm";

        [ObservableProperty]
        private double _yRangeMax = 3000.0;

        [ObservableProperty]
        private double _upperLimit = 2500.0;

        [ObservableProperty]
        private double _lowerLimit = 500.0;

        [ObservableProperty]
        private double _standardValue = 1500.0;

        // 通信方式列表与当前选中的模式
        public List<string> CommTypes { get; } = new()
        {
            "网络通讯 (服务端)",
            "网络通讯 (客户端)",
            "串口通讯",
            "不使用"
        };

        [ObservableProperty]
        private string _selectedCommType = "网络通讯 (服务端)";

        public bool IsServerMode => SelectedCommType == "网络通讯 (服务端)";
        public bool IsClientMode => SelectedCommType == "网络通讯 (客户端)";
        public bool IsSerialMode => SelectedCommType == "串口通讯";
        public bool IsNoneMode => SelectedCommType == "不使用";
        public bool CanToggleComm => !IsNoneMode;

        // 服务端参数
        [ObservableProperty]
        private string _serverIp = "127.0.0.1";

        [ObservableProperty]
        private int _serverPort = 3000;

        // 客户端参数
        [ObservableProperty]
        private string _clientTargetIp = "127.0.0.1";

        [ObservableProperty]
        private int _clientTargetPort = 3000;

        // 串口参数
        public ObservableCollection<string> AvailableSerialPorts { get; } = new();
        public List<int> AvailableBaudRates { get; } = new() { 9600, 19200, 38400, 57600, 115200 };

        [ObservableProperty]
        private string _selectedSerialPort = "COM1";

        [ObservableProperty]
        private int _selectedBaudRate = 9600;

        // 状态信息
        [ObservableProperty]
        private string _statusMessage = "服务就绪，点击【启动服务】等待客户端连接";

        [ObservableProperty]
        private bool _isCommActive = false;

        [ObservableProperty]
        private bool _isClientConnected = false;

        [ObservableProperty]
        private string _connectionBadgeText = "[未启动]";

        [ObservableProperty]
        private string _connectionBadgeColor = "#757575";

        [ObservableProperty]
        private string _connectButtonText = "启动服务";

        [ObservableProperty]
        private bool _isSettingsOpen = false;

        [ObservableProperty]
        private string _latestValueText = "--";

        [ObservableProperty]
        private string _averageValueText = "--";

        [ObservableProperty]
        private int _totalPoints = 0;

        private TcpServerService? _tcpServer;
        private TcpClientService? _tcpClient;
        private SerialPortService? _serialPortService;
        private bool _isInitializing = false;

        // 回调给 View 的数据推送事件
        public event Action<string>? OnDataReceived;
        public event Action? OnResetViewRequested;
        public event Action? OnClearDataRequested;

        public MainWindowViewModel()
        {
            RefreshSerialPortsList();
            LoadSavedConfig();
            UpdateCommButtonAndBadge();

            this.PropertyChanged += OnSettingPropertyChanged;
        }

        partial void OnSelectedCommTypeChanged(string value)
        {
            // 切换通信模式时，若前一个通信正在运行，先安全关闭
            StopAllCommunications();

            OnPropertyChanged(nameof(IsServerMode));
            OnPropertyChanged(nameof(IsClientMode));
            OnPropertyChanged(nameof(IsSerialMode));
            OnPropertyChanged(nameof(IsNoneMode));
            OnPropertyChanged(nameof(CanToggleComm));

            UpdateCommButtonAndBadge();

            if (IsNoneMode)
            {
                StatusMessage = "通信已设置为【不使用】";
            }
            else
            {
                StatusMessage = $"通信模式已切换为：{value}";
            }

            if (!_isInitializing)
            {
                SaveCurrentConfig();
            }
        }

        private void UpdateCommButtonAndBadge()
        {
            if (IsNoneMode)
            {
                ConnectButtonText = "未启用";
                ConnectionBadgeText = "[未启用]";
                ConnectionBadgeColor = "#757575";
                return;
            }

            if (IsServerMode)
            {
                ConnectButtonText = IsCommActive ? "停止服务" : "启动服务";
                if (!IsCommActive)
                {
                    ConnectionBadgeText = "[未启动]";
                    ConnectionBadgeColor = "#757575";
                }
                else if (IsClientConnected)
                {
                    ConnectionBadgeText = "[已连接]";
                    ConnectionBadgeColor = "#4CAF50";
                }
                else
                {
                    ConnectionBadgeText = "[监听中]";
                    ConnectionBadgeColor = "#FFA726";
                }
            }
            else if (IsClientMode)
            {
                ConnectButtonText = IsCommActive ? "断开连接" : "连接服务";
                if (!IsCommActive)
                {
                    ConnectionBadgeText = "[未连接]";
                    ConnectionBadgeColor = "#757575";
                }
                else if (IsClientConnected)
                {
                    ConnectionBadgeText = "[已连接]";
                    ConnectionBadgeColor = "#4CAF50";
                }
                else
                {
                    ConnectionBadgeText = "[连接中]";
                    ConnectionBadgeColor = "#FFA726";
                }
            }
            else if (IsSerialMode)
            {
                ConnectButtonText = IsCommActive ? "关闭串口" : "打开串口";
                if (!IsCommActive)
                {
                    ConnectionBadgeText = "[已关闭]";
                    ConnectionBadgeColor = "#757575";
                }
                else
                {
                    ConnectionBadgeText = $"[已打开]";
                    ConnectionBadgeColor = "#4CAF50";
                }
            }
        }

        public void RefreshSerialPortsList()
        {
            var ports = SerialPortService.GetAvailablePorts();
            AvailableSerialPorts.Clear();
            foreach (var p in ports)
            {
                AvailableSerialPorts.Add(p);
            }

            if (!AvailableSerialPorts.Contains(SelectedSerialPort))
            {
                SelectedSerialPort = AvailableSerialPorts.FirstOrDefault() ?? "(未检测到串口)";
            }
        }

        [RelayCommand]
        public void RefreshSerialPorts()
        {
            RefreshSerialPortsList();
            StatusMessage = $"已刷新本机串口列表 (共检测到 {AvailableSerialPorts.Count} 个端口)";
        }

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

        private void OnSettingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_isInitializing) return;

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

        [RelayCommand]
        public async Task ToggleCommAsync()
        {
            if (IsNoneMode) return;

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

        private async Task ToggleServerModeAsync()
        {
            if (IsCommActive)
            {
                StopAllCommunications();
                StatusMessage = "TCP 服务已停止";
                UpdateCommButtonAndBadge();
            }
            else
            {
                StatusMessage = $"正在启动 TCP 服务 (监听 {ServerIp}:{ServerPort})...";
                _tcpServer = new TcpServerService
                {
                    BindIp = ServerIp,
                    Port = ServerPort
                };

                _tcpServer.DataReceived += HandleIncomingData;

                _tcpServer.StatusChanged += (msg, clientActive) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        StatusMessage = msg;
                        IsClientConnected = clientActive;
                        UpdateCommButtonAndBadge();
                    });
                };

                _tcpServer.ClientConnectedChanged += (connected) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        IsClientConnected = connected;
                        UpdateCommButtonAndBadge();
                    });
                };

                bool ok = await _tcpServer.StartAsync();
                IsCommActive = ok;
                UpdateCommButtonAndBadge();
            }
        }

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
                    AutoReconnect = true
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

        private void HandleIncomingData(string data)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                OnDataReceived?.Invoke(data);
            });
        }

        public void UpdateMetrics(double latest, double avg, int count)
        {
            LatestValueText = latest.ToString("F3") + " " + YUnit;
            AverageValueText = avg.ToString("F3") + " " + YUnit;
            TotalPoints = count;
        }

        [RelayCommand]
        public void ToggleSettings()
        {
            IsSettingsOpen = !IsSettingsOpen;
        }

        [RelayCommand]
        public void ResetView()
        {
            OnResetViewRequested?.Invoke();
        }

        [RelayCommand]
        public void ClearData()
        {
            OnClearDataRequested?.Invoke();
            LatestValueText = "--";
            AverageValueText = "--";
            TotalPoints = 0;
        }

        [RelayCommand]
        public void ViewData()
        {
            try
            {
                string csvPath = CSVApi.GetDailyCsvPath(ChartTitle);
                if (System.IO.File.Exists(csvPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = csvPath,
                        UseShellExecute = true
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
