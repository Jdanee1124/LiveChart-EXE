using System;
using System.IO;
using System.Text.Json;

namespace LineChart.Services
{
    /// <summary>
    /// 应用程序运行与界面参数持久化数据模型
    /// </summary>
    public class AppConfig
    {
        public string ChartTitle { get; set; } = "线缆1";
        public string YUnit { get; set; } = "mm";
        public double YRangeMax { get; set; } = 3000.0;
        public double UpperLimit { get; set; } = 2500.0;
        public double StandardValue { get; set; } = 1500.0;
        public double LowerLimit { get; set; } = 500.0;

        // 通信模式选择: "网络通讯 (服务端)", "网络通讯 (客户端)", "串口通讯", "不使用"
        public string SelectedCommType { get; set; } = "网络通讯 (服务端)";

        // 网络通讯（服务端）参数
        public string ServerIp { get; set; } = "127.0.0.1";
        public int ServerPort { get; set; } = 3000;

        // 网络通讯（客户端）参数
        public string ClientTargetIp { get; set; } = "127.0.0.1";
        public int ClientTargetPort { get; set; } = 3000;

        // 串口通讯参数
        public string SerialPortName { get; set; } = "COM1";
        public int SerialBaudRate { get; set; } = 9600;
    }

    /// <summary>
    /// 参数持久化与自动记忆管理服务
    /// </summary>
    public static class ConfigService
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private static readonly object FileLock = new();

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg != null)
                    {
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取配置异常: {ex.Message}");
            }

            return new AppConfig();
        }

        public static void Save(AppConfig config)
        {
            try
            {
                lock (FileLock)
                {
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    string json = JsonSerializer.Serialize(config, options);
                    File.WriteAllText(ConfigPath, json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存配置异常: {ex.Message}");
            }
        }
    }
}
