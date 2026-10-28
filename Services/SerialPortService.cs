using System;
using System.IO.Ports;
using System.Linq;
using System.Text;

namespace LineChart.Services
{
    /// <summary>
    /// 标准工业串口通讯服务类（RS232 / RS485）
    /// 核心职责：负责连接传感器、激光测径仪、PLC 或扫码枪等通过串口（COM口）发送测量数据的硬件设备。
    /// 内置硬件热拔插自适应、端口防占用冲突检测、以及数据到达事件异步分发。
    /// </summary>
    public class SerialPortService : IDisposable
    {
        // 核心事件1：当串口收到下位机发来的新数据字符串时触发
        public event Action<string>? DataReceived;

        // 核心事件2：通信状态改变时触发（用于通知主界面状态栏更新文字和指示灯颜色）
        public event Action<string, bool>? StatusChanged;

        // 核心事件3：串口打开/关闭状态变更时触发（用于联动前台按钮文字变成【打开串口】或【关闭串口】）
        public event Action<bool>? IsOpenChanged;

        // 底层封装的 .NET 原生串口驱动对象
        private SerialPort? _serialPort;

        // 当前选中的串口号（例如 COM1, COM2）
        public string PortName { get; set; } = "COM1";

        // 波特率（数据传输速率，工业常见 9600, 115200 等，双方必须严格相同）
        public int BaudRate { get; set; } = 115200;

        // 数据位（默认 8 位）
        public int DataBits { get; set; } = 8;

        // 停止位（默认 1 位）
        public StopBits StopBits { get; set; } = StopBits.One;

        // 奇偶校验位（默认 None 无校验）
        public Parity Parity { get; set; } = Parity.None;

        // 快捷属性：判断当前底层物理串口是否正处于打开可用状态
        public bool IsOpen => _serialPort != null && _serialPort.IsOpen;

        /// <summary>
        /// 静态探测方法：自动扫描当前操作系统中所有现存的物理串口与虚拟串口
        /// </summary>
        /// <returns>返回排序好的可用端口数组，若电脑无任何串口则返回友情提示</returns>
        public static string[] GetAvailablePorts()
        {
            try
            {
                // 调用系统底层 API 获取所有登记在设备管理器中的 COM 端口
                var ports = SerialPort.GetPortNames();
                if (ports != null && ports.Length > 0)
                {
                    // 按自然数字字母升序排列，例如 COM1, COM2, COM10
                    return ports.OrderBy(p => p).ToArray();
                }
            }
            catch { }

            // 防呆兜底：如果一台工控机既没插USB转串口线也没装虚拟串口，返回明确提示
            return new[] { "(未检测到串口)" };
        }

        /// <summary>
        /// 打开串口物理通信链路
        /// </summary>
        /// <returns>成功返回 true，失败返回 false</returns>
        public bool Open()
        {
            // 如果已经是打开状态，无需重复打开，直接返回成功
            if (IsOpen) return true;

            // 防呆判断：如果当前选中的是未检测到硬件，禁止执行开启并弹出提示
            if (string.IsNullOrWhiteSpace(PortName) || PortName == "(未检测到串口)")
            {
                StatusChanged?.Invoke("打开失败: 当前电脑未检测到任何物理串口或虚拟串口硬件！", false);
                IsOpenChanged?.Invoke(false);
                return false;
            }

            try
            {
                // 先调用一次关闭，确保旧的端口资源被彻底释放，不留死锁句柄
                Close();

                // 实例化物理串口对象，传入端口名、波特率、校验位、数据位和停止位
                _serialPort = new SerialPort(PortName, BaudRate, Parity, DataBits, StopBits)
                {
                    ReadTimeout = 1000,         // 读取超时设为 1 秒，防止下位机掉电导致线程死等
                    WriteTimeout = 1000,        // 写入超时设为 1 秒
                    Encoding = Encoding.UTF8    // 统一以 UTF8 字符集解析字符
                };

                // 挂载硬件数据到达中断事件：当串口硬件缓冲区有数据到达时，操作系统自动回调此方法
                _serialPort.DataReceived += OnSerialDataReceived;

                // 真正发起 Windows 底层 CreateFile 硬件调用打开通信端口
                _serialPort.Open();

                // 触发状态改变通知：通知主界面变绿灯，并提示连接成功
                StatusChanged?.Invoke($"串口已打开 [{PortName}, {BaudRate} bps]", true);
                IsOpenChanged?.Invoke(true);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // 【工业常见异常1】：端口被占用（例如台达视觉软件、串口助手正在用该 COM1，尚未释放）
                StatusChanged?.Invoke($"打开失败: 串口 {PortName} 正被其他软件占用(请关闭占用该串口的台达视觉或调试助手)！", false);
                IsOpenChanged?.Invoke(false);
                return false;
            }
            catch (Exception ex) when (ex.Message.Contains("Could not find file") || ex is System.IO.FileNotFoundException)
            {
                // 【工业常见异常2】：端口号不存在（例如选了 COM3，但工控机只有 COM1）
                StatusChanged?.Invoke($"打开失败: 当前电脑中不存在物理串口 [{PortName}]！若在单机模拟请安装虚拟串口软件(如VSPD)；若连外设请插入USB串口线。", false);
                IsOpenChanged?.Invoke(false);
                return false;
            }
            catch (Exception ex)
            {
                // 捕获其余未知异常
                StatusChanged?.Invoke($"打开串口 {PortName} 失败: {ex.Message}", false);
                IsOpenChanged?.Invoke(false);
                return false;
            }
        }

        /// <summary>
        /// 串口硬件数据中断到达回调
        /// 由操作系统硬件中断在专属工作线程中触发，保证超低延迟响应。
        /// </summary>
        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                // 校验硬件对象合法性
                if (_serialPort == null || !_serialPort.IsOpen) return;
                
                // 将串口硬件缓冲区中现存的所有文本字符一次性读出
                string text = _serialPort.ReadExisting();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    // 裁剪掉首尾多余的换行空白符后，触发上层 DataReceived 事件将数据抛给 ViewModel
                    DataReceived?.Invoke(text.Trim());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"串口数据接收异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 安全关闭串口物理通信
        /// </summary>
        public void Close()
        {
            try
            {
                if (_serialPort != null)
                {
                    // 1. 先解绑数据接收事件，防止关闭瞬间又有数据冲入导致空指针异常
                    _serialPort.DataReceived -= OnSerialDataReceived;
                    if (_serialPort.IsOpen)
                    {
                        // 2. 关闭底层端口
                        _serialPort.Close();
                    }
                    // 3. 释放非托管 Windows 句柄资源
                    _serialPort.Dispose();
                }
            }
            catch { }
            finally
            {
                _serialPort = null;
                IsOpenChanged?.Invoke(false);
            }

            StatusChanged?.Invoke($"串口 [{PortName}] 已关闭", false);
        }

        /// <summary>
        /// 遵循 IDisposable 工业垃圾回收规范：当对象销毁时确保关闭串口硬件
        /// </summary>
        public void Dispose()
        {
            Close();
        }
    }
}
