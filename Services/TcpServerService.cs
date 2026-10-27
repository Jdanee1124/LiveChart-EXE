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
    /// 标准 TCP 服务端：监听指定 IP 和端口，接收 TCP 客户端连入。
    /// 按空格/逗号分隔、CR+LF 结尾流式接收测量数据。
    /// </summary>
    public class TcpServerService : IDisposable
    {
        public event Action<string>? DataReceived;
        public event Action<string, bool>? StatusChanged;
        public event Action<bool>? ClientConnectedChanged;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private int _connectedClients = 0;

        public bool IsRunning => _listener != null;
        public bool HasClients => _connectedClients > 0;

        public string BindIp { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 3000;

        public Task<bool> StartAsync()
        {
            if (IsRunning) return Task.FromResult(true);

            try
            {
                IPAddress ip = BindIp == "0.0.0.0"
                    ? IPAddress.Any
                    : IPAddress.TryParse(BindIp, out var parsed) ? parsed : IPAddress.Any;

                _cts = new CancellationTokenSource();
                _listener = new TcpListener(ip, Port);
                _listener.Start();

                StatusChanged?.Invoke($"TCP 服务已启动 (监听 {BindIp}:{Port})，等待客户端连接...", false);

                _ = Task.Run(() => AcceptClientsLoopAsync(_cts.Token));
                return Task.FromResult(true);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                StatusChanged?.Invoke($"启动失败: 端口 {Port} 已被其他程序占用(请关闭占用该端口的软件后再试)！", false);
                Stop();
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"启动 TCP 服务失败: {ex.Message}", false);
                Stop();
                return Task.FromResult(false);
            }
        }

        private async Task AcceptClientsLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(token);
                    string remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "未知客户端";

                    Interlocked.Increment(ref _connectedClients);
                    ClientConnectedChanged?.Invoke(true);
                    StatusChanged?.Invoke($"客户端已连接 [{remoteEp}]", true);

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

        private async Task HandleClientAsync(TcpClient client, string remoteEp, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            using (client)
            using (var stream = client.GetStream())
            {
                while (!token.IsCancellationRequested && client.Connected)
                {
                    try
                    {
                        // 异步读取任意到达的网络数据块（同时兼容带换行CR+LF与不带换行的所有客户端输入）
                        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                        if (bytesRead == 0) break;

                        string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            try
                            {
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

            Interlocked.Decrement(ref _connectedClients);
            bool hasRemaining = _connectedClients > 0;
            ClientConnectedChanged?.Invoke(hasRemaining);

            if (IsRunning)
            {
                if (hasRemaining)
                {
                    StatusChanged?.Invoke($"客户端断开 [{remoteEp}]，剩余 {_connectedClients} 个连接", true);
                }
                else
                {
                    StatusChanged?.Invoke($"客户端已断开 [{remoteEp}]，等待重新连接...", false);
                }
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try
            {
                _listener?.Stop();
            }
            catch { }

            _listener = null;
            _cts = null;
            _connectedClients = 0;

            ClientConnectedChanged?.Invoke(false);
            StatusChanged?.Invoke("TCP 服务已停止", false);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
