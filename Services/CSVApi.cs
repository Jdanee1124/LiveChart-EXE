using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LineChart.Services
{
    /// <summary>
    /// CSV 数据持久化服务类（工业数据存储核心）
    /// 核心职责：按天、按线缆型号自动分表，将接收到的每一个测量点流式追加保存到 D:\MyData 中。
    /// 具备毫秒级时间戳、防跨进程占用冲突锁、以及 30 天超期滚动清理机制。
    /// </summary>
    public class CSVApi
    {
        // 数据统一保存的主目录：锁定在 D 盘的 MyData 文件夹，方便现场直接寻找
        public static string SaveDirectory { get; set; } = @"D:\MyData";

        // 多线程文件并发互斥锁：确保在多线程高速写入时，文件流不会发生写入冲突或字节错位
        private static readonly object FileLock = new();

        /// <summary>
        /// 过滤文件名中的非法字符
        /// 防止用户在“线缆名称”中输入如 \ / : * ? " < > | 等 Windows 禁用的特殊字符导致崩溃
        /// </summary>
        /// <param name="rawName">用户输入的原始线缆名称</param>
        /// <returns>清洗替换后的合法文件名字符</returns>
        public static string GetSafeFileName(string? rawName)
        {
            // 如果线缆名称为空，默认使用“线缆”二字兜底
            string safe = string.IsNullOrWhiteSpace(rawName) ? "线缆" : rawName.Trim();

            // 遍历 Windows 操作系统规定的所有非法字符，逐一替换为下划线 '_'
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(c, '_');
            }
            return safe;
        }

        /// <summary>
        /// 获取当天对应的 CSV 文件全路径
        /// 规则：D:\MyData\yyyy-MM-dd_线缆名称.csv（例如：D:\MyData\2026-10-28_线缆1.csv）
        /// </summary>
        /// <param name="cableName">当前线缆名称</param>
        /// <returns>磁盘上的绝对文件物理路径</returns>
        public static string GetDailyCsvPath(string? cableName)
        {
            string safeName = GetSafeFileName(cableName);
            string dir = SaveDirectory;
            // 按当前系统日期的年月日动态拼接生成文件名
            return Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}_{safeName}.csv");
        }

        /// <summary>
        /// 实时追加数据点至当天的线缆 CSV 文件中
        /// 【核心设计】：采用 Append 模式流式追加，不把大文件吃进内存，每次收到几条写几条，写完立即刷盘。
        /// </summary>
        /// <param name="cableName">线缆名称</param>
        /// <param name="values">本次收到的测量数值列表</param>
        /// <param name="times">每个数值到达时记录的精准时间戳列表</param>
        public static void AppendPoints(string? cableName, IReadOnlyList<double> values, IReadOnlyList<DateTime> times)
        {
            // 防呆校验：如果没有有效数据，直接返回
            if (values == null || values.Count == 0) return;

            try
            {
                string dir = SaveDirectory;
                // 1. 检查 D:\MyData 目录是否存在，若不存在则自动递归创建
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // 2. 写入前顺便触发一次旧文件检查，自动清理 30 天前的历史归档文件
                CleanUpOldFiles(dir);

                // 3. 计算获取今天专属的 CSV 文件完整路径
                string filePath = GetDailyCsvPath(cableName);

                // 4. 进入独占线程锁
                lock (FileLock)
                {
                    // 判断该文件此前是否已经存在（用于决定是否需要补写首行表头）
                    bool existed = File.Exists(filePath);

                    // 【核心重点】：
                    // FileMode.Append：以“末尾追加”方式打开，指针直接移到文件末尾，绝不覆写老数据；
                    // FileAccess.Write：本软件拥有写权限；
                    // FileShare.ReadWrite：关键参数！允许外部 Excel 或用户在软件写数据的同时双击打开查看，互不锁死！
                    using var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var sw = new StreamWriter(fs, Encoding.UTF8);

                    // 如果是今天新创建的空文件，第一行必须写上标准 CSV 表头
                    if (!existed)
                    {
                        sw.WriteLine("时间戳,实测值");
                    }

                    // 计算需要写入的数据量（取数值和时间戳集合的较小值，防止下标越界）
                    int count = Math.Min(values.Count, times.Count);
                    for (int i = 0; i < count; i++)
                    {
                        // 格式化输出：精细到毫秒级的时间戳（yyyy-MM-dd HH:mm:ss.fff）+ 逗号 + 3位小数测量值
                        sw.WriteLine($"{times[i]:yyyy-MM-dd HH:mm:ss.fff},{values[i]:F3}");
                    }
                }
            }
            catch (Exception ex)
            {
                // 捕获可能发生的磁盘权限或IO异常，输出调试信息，绝不允许存盘失败导致上位机崩溃
                System.Diagnostics.Debug.WriteLine($"自动保存CSV异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 滚动清理 30 天前的超期旧数据文件
        /// 核心职责：工控机硬盘保护神，防止产线 365 天无休运行把系统盘直接塞满爆死。
        /// </summary>
        /// <param name="path">数据保存的主目录路径</param>
        private static void CleanUpOldFiles(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    // 检索该目录下所有的 .csv 数据文件
                    var files = Directory.GetFiles(path, "*.csv");

                    // 计算出 30 天前的时间阈值点
                    DateTime threshold = DateTime.Now.AddDays(-30);

                    foreach (string file in files)
                    {
                        var info = new FileInfo(file);
                        // 如果文件的最后修改时间早于 30 天前，认定为超期过期文件，直接删除
                        if (info.LastWriteTime < threshold)
                        {
                            try { info.Delete(); } catch { }
                        }
                    }
                }
            }
            catch { }
        }
    }
}
