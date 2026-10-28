using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LineChart.Services
{
    /// <summary>
    /// 标准工业 TCP 服务端通讯服务类（Socket Server）
    /// 核心职责：充当“数据接收站”。在本机开放一个网络端口（默认 3000），
    /// 等待外部视觉软件（如台达 DMV）、外部工控机或模拟脚本作为客户端主动连接进来，并实时推流送入测量数据。
    /// 支持多客户端并发连接、端口占用防呆检测、以及异常掉线状态自适应。
    /// </summary>
    public class TcpServerService : IDisposable
    {
        // 核心事件1：任意连入的客户端推送新数据到达时触发
        public event Action<string>? DataReceived;

        // 核心事件2：服务端运行状态文本变更时触发
        public event Action<string, bool>? StatusChanged;

        // 核心事件3：客户端接入/离开状态变更时触发（用于联动指示灯变绿/变黄）
        public event Action<bool>? ClientConnectedChanged;

        // 底层封装的 .NET 网络监听器套接字
        private TcpListener? _listener;

        // 任务取消控制令箭：用于通知主监听死循环安全停机
        private CancellationTokenSource? _cts;

        // 当前在线连接的客户端物理数量统计（线程安全计数）
        private int _connectedClients = 0;

        // 快捷属性：判断 TCP 服务端是否正处于物理监听运行中
        public bool IsRunning => _listener != null;

        // 快捷属性：判断当前是否有外部客户端连入
        public bool HasClients => _connectedClients > 0;

        // 服务端绑定的本地监听 IP（127.0.0.1 仅允许本机内部通信；0.0.0.0 允许局域网外部所有网卡通信）
        public string BindIp { get; set; } = "127.0.0.1";

        // 服务端开放的通信端口号（默认 3000）
        public int Port { get; set; } = 3000;

        /// <summary>
        /// 启动 TCP 服务端端口监听
        /// </summary>
        public Task<bool> StartAsync()
        {
            // 如果已经在运行中，直接返回
            if (IsRunning) return Task.FromResult(true);

            try
            {
                // 解析待绑定的 IP 地址对象
                IPAddress ip = BindIp == "0.0.0.0"
                    ? IPAddress.Any
                    : IPAddress.TryParse(BindIp, out var parsed) ? parsed : IPAddress.Any;

                _cts = new CancellationTokenSource();

                // 实例化底层 TCP 监听器并绑定 IP 与端口
                _listener = new TcpListener(ip, Port);

                // 真正发起 Windows Socket 系统的 bind 和 listen 系统调用，开启网络监听
                _listener.Start();

                // 触发通知：服务已成功挂起，正在等待客户端连入
                StatusChanged?.Invoke($"TCP 服务已启动 (监听 {BindIp}:{Port})，等待客户端连接...", false);

                // 在后台独立线程池任务中启动客户端接入监听大循环
                _ = Task.Run(() => AcceptClientsLoopAsync(_cts.Token));
                return Task.FromResult(true);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // 【现场常见故障】：端口 3000 已被其它软件（或者双开的自己）占用了
                StatusChanged?.Invoke($"启动失败: 端口 {Port} 已被其他程序占用(请关闭占用该端口的软件后再试)！", false);
                Stop();
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                // 捕获其余网络启动异常
                StatusChanged?.Invoke($"启动 TCP 服务失败: {ex.Message}", false);
                Stop();
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// 客户端接入等待大循环（门卫角色）
        /// 持续监听是否有新的外部客户端敲门连入。
        /// </summary>
        private async Task AcceptClientsLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                try
                {
                    // 异步挂起等待客户端接入。无客户端接入时线程完全休眠，0 CPU 消耗
                    TcpClient client = await _listener.AcceptTcpClientAsync(token);

                    // 获取连入客户端的远程 IP 和端口字符串（如 "127.0.0.1:54321"）
                    string remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "未知客户端";

                    // 使用 Interlocked.Increment 进行多线程原子安全加 1
                    Interlocked.Increment(ref _connectedClients);

                    // 状态变绿：已有对端设备连入
                    ClientConnectedChanged?.Invoke(true);
                    StatusChanged?.Invoke($"客户端已连接 [{remoteEp}]", true);

                    // 为每一个连入的客户端开辟一个专属的独立工作线程去处理数据，互不阻塞干扰
                    _ = Task.Run(() => HandleClientAsync(client, remoteEp, token), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        StatusChanged?.Invoke($"客户端接入异常: {ex.Message}", false);
                    }
                }
            }
        }

        /// <summary>
        /// 针对单个已连接客户端的数据收发流水线
        /// </summary>
        private async Task HandleClientAsync(TcpClient client, string remoteEp, CancellationToken token)
        {
            // 分配 8KB 临时内存接收数据包
            byte[] buffer = new byte[8192];
            using (client)
            using (var stream = client.GetStream())
            {
                // 只要该客户端没有断开，循环读取数据
                while (!token.IsCancellationRequested && client.Connected)
                {
                    try
                    {
                        // 异步读取从客户端冲入的网络数据
                        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                        if (bytesRead == 0) break; // 读取到 0 字节代表对端已主动挂断

                        // 将收到的字节流还原为文本
                        string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            try
                            {
                                // 抛给订阅事件，进入折线图实时绘制通道
                                DataReceived?.Invoke(text.Trim());
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"数据处理异常: {ex.Message}");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"数据接收中断 [{remoteEp}]: {ex.Message}");
                        break;
                    }
                }
            }

            // 离开循环代表该客户端已断开，原子减 1
            Interlocked.Decrement(ref _connectedClients);
            bool hasRemaining = _connectedClients > 0;
            ClientConnectedChanged?.Invoke(hasRemaining);

            if (IsRunning)
            {
                if (hasRemaining)
                {
                    // 若还有其它客户端在线
                    StatusChanged?.Invoke($"客户端断开 [{remoteEp}]，剩余 {_connectedClients} 个连接", true);
                }
                else
                {
                    // 最后一个客户端也断开了，指示灯转为等待中的黄色
                    StatusChanged?.Invoke($"客户端已断开 [{remoteEp}]，等待重新连接...", false);
                }
            }
        }

        /// <summary>
        /// 停止服务端监听并断开所有连接
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            try
            {
                // 停止底层 Socket 端口绑定，释放端口给其他程序使用
                _listener?.Stop();
            }
            catch { }

            _listener = null;
            _cts = null;
            _connectedClients = 0;

            ClientConnectedChanged?.Invoke(false);
            StatusChanged?.Invoke("TCP 服务已停止", false);
        }

        /// <summary>
        /// 垃圾回收释放规范
        /// </summary>
        public void Dispose()
        {
            Stop();
        }
    }
}
