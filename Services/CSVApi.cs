using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LineChart.Services
{
    /// <summary>
    /// CSV 数据持久化服务：按天按线缆名称自动流式追加存盘，精细记录毫秒级时间戳与测量值。
    /// </summary>
    public class CSVApi
    {
        public static string SaveDirectory { get; set; } = @"D:\MyData";
        private static readonly object FileLock = new();

        /// <summary>
        /// 过滤文件名非法字符
        /// </summary>
        public static string GetSafeFileName(string? rawName)
        {
            string safe = string.IsNullOrWhiteSpace(rawName) ? "线缆" : rawName.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(c, '_');
            }
            return safe;
        }

        /// <summary>
        /// 获取当天对应的 CSV 文件全路径: D:\MyData\yyyy-MM-dd_线缆Name.csv
        /// </summary>
        public static string GetDailyCsvPath(string? cableName)
        {
            string safeName = GetSafeFileName(cableName);
            string dir = SaveDirectory;
            return Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}_{safeName}.csv");
        }

        /// <summary>
        /// 实时追加数据点至当天的线缆 CSV 文件中（精简格式：时间戳+实测值）
        /// </summary>
        public static void AppendPoints(string? cableName, IReadOnlyList<double> values, IReadOnlyList<DateTime> times)
        {
            if (values == null || values.Count == 0) return;

            try
            {
                string dir = SaveDirectory;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                CleanUpOldFiles(dir);

                string filePath = GetDailyCsvPath(cableName);
                lock (FileLock)
                {
                    bool existed = File.Exists(filePath);
                    using var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var sw = new StreamWriter(fs, Encoding.UTF8);

                    if (!existed)
                    {
                        sw.WriteLine("时间戳,实测值");
                    }

                    int count = Math.Min(values.Count, times.Count);
                    for (int i = 0; i < count; i++)
                    {
                        sw.WriteLine($"{times[i]:yyyy-MM-dd HH:mm:ss.fff},{values[i]:F3}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"自动保存CSV异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 滚动清理 30 天前的超期旧数据文件
        /// </summary>
        private static void CleanUpOldFiles(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var files = Directory.GetFiles(path, "*.csv");
                    DateTime threshold = DateTime.Now.AddDays(-30);
                    foreach (string file in files)
                    {
                        var info = new FileInfo(file);
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
