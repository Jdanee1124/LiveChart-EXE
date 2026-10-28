using System;
using System.IO;
using System.Text.Json;

namespace LineChart.Services
{
    /// <summary>
    /// 应用程序运行与界面参数持久化数据模型（DTO）
    /// 核心职责：充当“配方底稿”。
    /// 将操作工在界面配置的所有公差、上限、下限、线缆型号、串口与IP参数集中收纳为纯粹的数据对象，
    /// 准备序列化存储到本地硬盘的 config.json 文件中。
    /// </summary>
    public class AppConfig
    {
        // 当前检测线缆型号规格名称（默认“线缆1”，对应生成的 CSV 文件命名）
        public string ChartTitle { get; set; } = "线缆1";

        // Y 轴物理测量单位（默认“mm”，显示在坐标轴左上方）
        public string YUnit { get; set; } = "mm";

        // 固定量程模式下的 Y 轴全量程最大值（默认 3000.0）
        public double YRangeMax { get; set; } = 3000.0;

        // 质量控制上限红线阈值（默认 2500.0，超限预警）
        public double UpperLimit { get; set; } = 2500.0;

        // 工艺标准参考基准线（默认 1500.0，绿色虚线）
        public double StandardValue { get; set; } = 1500.0;

        // 质量控制下限红线阈值（默认 500.0，低于此值不合格）
        public double LowerLimit { get; set; } = 500.0;

        // 当前选中的通信方式，共 4 种模式：
        // 1. "网络通讯 (服务端)" / 2. "网络通讯 (客户端)" / 3. "串口通讯" / 4. "不使用"
        public string SelectedCommType { get; set; } = "网络通讯 (服务端)";

        // 模式一：作为 TCP 服务端时绑定的本地监听 IP（默认 127.0.0.1）
        public string ServerIp { get; set; } = "127.0.0.1";

        // 模式一：作为 TCP 服务端时开放的监听端口号（默认 3000）
        public int ServerPort { get; set; } = 3000;

        // 模式二：作为 TCP 客户端时主动连接的外部目标设备 IP
        public string ClientTargetIp { get; set; } = "127.0.0.1";

        // 模式二：作为 TCP 客户端时主动连接的外部目标端口号
        public int ClientTargetPort { get; set; } = 3000;

        // 模式三：串口通讯使用的物理/虚拟 COM 端口号（例如 COM1, COM2）
        public string SerialPortName { get; set; } = "COM1";

        // 模式三：串口通讯波特率（默认 9600，常见还有 115200 等）
        public int SerialBaudRate { get; set; } = 9600;
    }

    /// <summary>
    /// 参数持久化与自动记忆管理服务（静态工具类）
    /// 核心职责：软件关闭时自动将参数记忆落盘，软件下次启动时自动读取还原，
    /// 避免现场工人每次开机都要重新设置一遍参数。
    /// </summary>
    public static class ConfigService
    {
        // 配置文件存放路径：锁定在软件当前 EXE 同级目录下的 config.json 文件，保证软件移动到哪配方跟到哪
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        // 独占文件互斥线程锁：防止高频多线程同时读写 config.json 导致文件损坏
        private static readonly object FileLock = new();

        /// <summary>
        /// 从磁盘加载历史配置参数
        /// </summary>
        /// <returns>若文件存在则返回反序列化好的配置对象；若第一次运行或损坏则返回安全默认值</returns>
        public static AppConfig Load()
        {
            try
            {
                // 1. 检查磁盘根目录下是否存在 config.json 历史配置文件
                if (File.Exists(ConfigPath))
                {
                    // 2. 读取文件中的全部纯文本 JSON 字符串
                    string json = File.ReadAllText(ConfigPath);

                    // 3. 将 JSON 文本反序列化还原为 AppConfig 内存对象
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg != null)
                    {
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                // 若读取过程发生异常（如格式损坏），在调试窗口记录日志，但不抛出异常中断软件
                System.Diagnostics.Debug.WriteLine($"读取配置异常: {ex.Message}");
            }

            // 防呆保底：若文件不存在或读取失败，创建一个全新的默认配置对象，确保系统永远能正常启动
            return new AppConfig();
        }

        /// <summary>
        /// 将当前内存中的最新参数持久化保存到磁盘
        /// </summary>
        /// <param name="config">当前界面最新的配置对象</param>
        public static void Save(AppConfig config)
        {
            try
            {
                // 进入独占线程锁，确保同一时刻只有一个写入操作
                lock (FileLock)
                {
                    // 设置 JSON 序列化选项：开启排版缩进（WriteIndented = true），生成人类一眼看得懂的工整 JSON
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };

                    // 将配置对象序列化为格式化好的字符串
                    string json = JsonSerializer.Serialize(config, options);

                    // 安全覆写到磁盘文件中
                    File.WriteAllText(ConfigPath, json);
                }
            }
            catch (Exception ex)
            {
                // 捕获可能的文件权限不足或磁盘已满异常，打印调试信息
                System.Diagnostics.Debug.WriteLine($"保存配置异常: {ex.Message}");
            }
        }
    }
}
