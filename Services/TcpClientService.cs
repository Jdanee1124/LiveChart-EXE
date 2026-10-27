using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LineChart.Services
{
    /// <summary>
    /// 标准 TCP 客户端服务：主动连接指定的服务端 IP 和端口，流式接收测量数据。
    /// 支持连接断开后后台自动重试重连机制。
    /// </summary>
    public class TcpClientService : IDisposable
    {
        public event Action<string>? DataReceived;
        public event Action<string, bool>? StatusChanged;
        public event Action<bool>? ConnectedChanged;

        private TcpClient? _client;
        private CancellationTokenSource? _cts;
        private bool _isRunning = false;

        public bool IsRunning => _isRunning;
        public bool IsConnected => _client != null && _client.Connected;

        public string TargetIp { get; set; } = "127.0.0.1";
        public int TargetPort { get; set; } = 3000;
        public bool AutoReconnect { get; set; } = true;

        public Task<bool> StartAsync()
        {
            if (_isRunning) return Task.FromResult(true);

            _isRunning = true;
            _cts = new CancellationTokenSource();

            StatusChanged?.Invoke($"正在尝试连接目标服务端 [{TargetIp}:{TargetPort}]...", false);
            ConnectedChanged?.Invoke(false);

            _ = Task.Run(() => ConnectionSupervisorLoopAsync(_cts.Token));
            return Task.FromResult(true);
        }

        private async Task ConnectionSupervisorLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    _client = new TcpClient();
                    
                    // 异步连接，带 3 秒超时控制
                    var connectTask = _client.ConnectAsync(TargetIp, TargetPort);
                    var delayTask = Task.Delay(3000, token);

                    var completed = await Task.WhenAny(connectTask, delayTask);
                    if (completed == delayTask)
                    {
                        throw new TimeoutException("连接超时");
                    }

                    await connectTask; // 抛出真实异常（若有）

                    StatusChanged?.Invoke($"已连接至服务端 [{TargetIp}:{TargetPort}]", true);
                    ConnectedChanged?.Invoke(true);

                    // 进入数据接收循环，直到网络断开
                    await ReceiveLoopAsync(_client, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested || !_isRunning) break;

                    StatusChanged?.Invoke($"未能连接至 [{TargetIp}:{TargetPort}]: {ex.Message}，正在重试...", false);
                    ConnectedChanged?.Invoke(false);
                }
                finally
                {
                    try { _client?.Close(); } catch { }
                    _client = null;
                }

                if (!AutoReconnect || token.IsCancellationRequested || !_isRunning)
                {
                    break;
                }

                try
                {
                    await Task.Delay(2500, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            ConnectedChanged?.Invoke(false);
        }

        private async Task ReceiveLoopAsync(TcpClient client, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            using var stream = client.GetStream();

            while (!token.IsCancellationRequested && client.Connected && _isRunning)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead == 0)
                {
                    // 对端正常关闭了连接
                    StatusChanged?.Invoke($"服务端主动断开连接 [{TargetIp}:{TargetPort}]", false);
                    ConnectedChanged?.Invoke(false);
                    break;
                }

                string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        DataReceived?.Invoke(text.Trim());
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"客户端数据处理异常: {ex.Message}");
                    }
                }
            }
        }

        public void Stop()
        {
            _isRunning = false;
            try
            {
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

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
