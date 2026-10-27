using System;
using System.IO.Ports;
using System.Linq;
using System.Text;

namespace LineChart.Services
{
    /// <summary>
    /// 标准串口通信服务：支持指定 COM 端口与波特率，实时接收下位机/传感器传来的测量数据。
    /// </summary>
    public class SerialPortService : IDisposable
    {
        public event Action<string>? DataReceived;
        public event Action<string, bool>? StatusChanged;
        public event Action<bool>? IsOpenChanged;

        private SerialPort? _serialPort;

        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 115200;
        public int DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Parity Parity { get; set; } = Parity.None;

        public bool IsOpen => _serialPort != null && _serialPort.IsOpen;

        /// <summary>
        /// 自动获取本机现存的所有有效串口列表，若为空则提示未检测到硬件
        /// </summary>
        public static string[] GetAvailablePorts()
        {
            try
            {
                var ports = SerialPort.GetPortNames();
                if (ports != null && ports.Length > 0)
                {
                    return ports.OrderBy(p => p).ToArray();
                }
            }
            catch { }

            return new[] { "(未检测到串口)" };
        }

        public bool Open()
        {
            if (IsOpen) return true;

            if (string.IsNullOrWhiteSpace(PortName) || PortName == "(未检测到串口)")
            {
                StatusChanged?.Invoke("打开失败: 当前电脑未检测到任何物理串口或虚拟串口硬件！", false);
                IsOpenChanged?.Invoke(false);
                return false;
            }

            try
            {
                Close();

                _serialPort = new SerialPort(PortName, BaudRate, Parity, DataBits, StopBits)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    Encoding = Encoding.UTF8
                };

                _serialPort.DataReceived += OnSerialDataReceived;
                _serialPort.Open();

                StatusChanged?.Invoke($"串口已打开 [{PortName}, {BaudRate} bps]", true);
                IsOpenChanged?.Invoke(true);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                StatusChanged?.Invoke($"打开失败: 串口 {PortName} 正被其他软件占用(请关闭占用该串口的台达视觉或调试助手)！", false);
                IsOpenChanged?.Invoke(false);
                return false;
            }
            catch (Exception ex) when (ex.Message.Contains("Could not find file") || ex is System.IO.FileNotFoundException)
            {
                StatusChanged?.Invoke($"打开失败: 当前电脑中不存在物理串口 [{PortName}]！若在单机模拟请安装虚拟串口软件(如VSPD)；若连外设请插入USB串口线。", false);
                IsOpenChanged?.Invoke(false);
                return false;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"打开串口 {PortName} 失败: {ex.Message}", false);
                IsOpenChanged?.Invoke(false);
                return false;
            }
        }

        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_serialPort == null || !_serialPort.IsOpen) return;
                
                string text = _serialPort.ReadExisting();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    DataReceived?.Invoke(text.Trim());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"串口数据接收异常: {ex.Message}");
            }
        }

        public void Close()
        {
            try
            {
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= OnSerialDataReceived;
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }
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

        public void Dispose()
        {
            Close();
        }
    }
}
