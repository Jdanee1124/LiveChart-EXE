using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LineChart.Services
{
    /// <summary>
    /// 标准工业 TCP 客户端通讯服务类（Socket Client）
    /// 核心职责：主动出击，连接现场指定的服务端设备（如视觉主机、工控服务器或智能相机）。
    /// 内置【工业断线自愈守护看门狗】：在网线松脱、对端重启时，后台自动每隔 2.5 秒重试重连，恢复后无缝续传。
    /// </summary>
    public class TcpClientService : IDisposable
    {
        // 核心事件1：收到网络字节流解析出有效文本数据时触发
        public event Action<string>? DataReceived;

        // 核心事件2：网络状态文本改变时触发（如“正在连接...”、“已连接”、“已断开”）
        public event Action<string, bool>? StatusChanged;

        // 核心事件3：连接状态改变时触发（用于联动指示灯变绿/变灰）
        public event Action<bool>? ConnectedChanged;

        // 底层 .NET 网络客户端 Socket 对象
        private TcpClient? _client;

        // 异步任务取消令箭：用于通知后台重连看门狗和接收任务优雅退出
        private CancellationTokenSource? _cts;

        // 标记当前客户端服务是否处于主动运行中
        private bool _isRunning = false;

        public bool IsRunning => _isRunning;

        // 快捷属性：判断底层物理套接字是否真正握手成功并处于在线状态
        public bool IsConnected => _client != null && _client.Connected;

        // 目标服务端的 IP 地址（例如 127.0.0.1 或 192.168.1.100）
        public string TargetIp { get; set; } = "127.0.0.1";

        // 目标服务端的端口号（例如 3000）
        public int TargetPort { get; set; } = 3000;

        // 是否开启断线自动重连开关（工业现场强烈建议为 true）
        public bool AutoReconnect { get; set; } = true;

        /// <summary>
        /// 启动 TCP 客户端后台守护进程
        /// </summary>
        public Task<bool> StartAsync()
        {
            // 如果已经在运行中，不重复启动
            if (_isRunning) return Task.FromResult(true);

            _isRunning = true;
            _cts = new CancellationTokenSource();

            // 发出正在连接中的提示
            StatusChanged?.Invoke($"正在尝试连接目标服务端 [{TargetIp}:{TargetPort}]...", false);
            ConnectedChanged?.Invoke(false);

            // 在独立后台线程中启动连接看门狗死循环
            _ = Task.Run(() => ConnectionSupervisorLoopAsync(_cts.Token));
            return Task.FromResult(true);
        }

        /// <summary>
        /// 核心看门狗连接循环：负责连接建立、保活监视与断线自动重试
        /// </summary>
        private async Task ConnectionSupervisorLoopAsync(CancellationToken token)
        {
            // 只要用户没有主动点击【停止】，且令箭未被取消，死循环持续守护
            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    _client = new TcpClient();
                    
                    // 【带超时的非阻塞异步连接】：
                    // 直接调用 ConnectAsync 如果对方关机会死等很长时间；
                    // 这里配合 Task.Delay(3000) 构造 3 秒快速超时控制，3 秒连不上立即重试。
                    var connectTask = _client.ConnectAsync(TargetIp, TargetPort);
                    var delayTask = Task.Delay(3000, token);

                    var completed = await Task.WhenAny(connectTask, delayTask);
                    if (completed == delayTask)
                    {
                        // 3 秒计时器先到期，判定为连接超时
                        throw new TimeoutException("连接超时");
                    }

                    // 真正等待连接任务，如有网络不可达或拒绝连接错误，会在此抛出真实异常
                    await connectTask;

                    // 握手成功：状态变绿
                    StatusChanged?.Invoke($"已连接至服务端 [{TargetIp}:{TargetPort}]", true);
                    ConnectedChanged?.Invoke(true);

                    // 阻塞进入数据读取循环，程序会一直停留在下面这一行，直到网络物理断开才退出来
                    await ReceiveLoopAsync(_client, token);
                }
                catch (OperationCanceledException)
                {
                    // 正常用户点击停止，退出循环
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested || !_isRunning) break;

                    // 连接失败提示，并通知界面状态灯变灰
                    StatusChanged?.Invoke($"未能连接至 [{TargetIp}:{TargetPort}]: {ex.Message}，正在重试...", false);
                    ConnectedChanged?.Invoke(false);
                }
                finally
                {
                    // 每次连接断开后，彻底关闭释放旧的客户端对象
                    try { _client?.Close(); } catch { }
                    _client = null;
                }

                // 判断是否需要继续重试
                if (!AutoReconnect || token.IsCancellationRequested || !_isRunning)
                {
                    break;
                }

                try
                {
                    // 间隔 2.5 秒后再尝试下一次握手重试，避免狂刷 CPU
                    await Task.Delay(2500, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            ConnectedChanged?.Invoke(false);
        }

        /// <summary>
        /// 数据流接收专属工作循环
        /// 在长连接建立期间，全速异步监听网络数据流。
        /// </summary>
        private async Task ReceiveLoopAsync(TcpClient client, CancellationToken token)
        {
            // 准备 8KB 高速接收字节缓冲区
            byte[] buffer = new byte[8192];
            using var stream = client.GetStream();

            // 只要网线连着且服务未停，循环读取
            while (!token.IsCancellationRequested && client.Connected && _isRunning)
            {
                // 异步读取网络到达的二进制字节
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead == 0)
                {
                    // 【工业底层物理真相】：TCP 中 ReadAsync 返回 0，代表服务端主动发起了 FIN 包关闭了连接
                    StatusChanged?.Invoke($"服务端主动断开连接 [{TargetIp}:{TargetPort}]", false);
                    ConnectedChanged?.Invoke(false);
                    break;
                }

                // 将接收到的字节转换为 UTF-8 文本
                string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        // 抛给上层业务订阅者处理
                        DataReceived?.Invoke(text.Trim());
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"客户端数据处理异常: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 主动停止客户端通讯并释放所有网络句柄
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            try
            {
                // 触发取消令箭，通知所有挂起的异步网络任务立即安全退出
                _cts?.Cancel();
            }
            catch { }

            try
            {
                _client?.Close();
            }
            catch { }

            _client = null;
            StatusChanged?.Invoke("已断开客户端连接", false);
            ConnectedChanged?.Invoke(false);
        }

        /// <summary>
        /// 遵循 IDisposable 工业垃圾回收规范
        /// </summary>
        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
