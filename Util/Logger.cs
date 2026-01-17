using System;
using System.IO;
using System.Linq;
using vatsys;

namespace BARS.Util
{
    public class Logger
    {
        private static readonly string dirPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BARS",
            "vatsys"
        );
        private static readonly string logFilePath;
        private static readonly object logLock = new object();
        private const int MaxLogFiles = 10;

        private string name;

        static Logger()
        {
            try
            {
                // Ensure directory exists
                if (!Directory.Exists(dirPath))
                {
                    Directory.CreateDirectory(dirPath);
                }

                // Create log filename with datetime
                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
                logFilePath = Path.Combine(dirPath, $"BARS-vatSys-{timestamp}.log");

                // Clean up old log files, keep only the most recent ones
                CleanupOldLogs();
            }
            catch
            {
                // Fallback if initialization fails
                logFilePath = Path.Combine(dirPath, "BARS-vatSys.log");
            }
        }

        public Logger(string Name)
        {
            name = Name;
        }

        private static void CleanupOldLogs()
        {
            try
            {
                var logFiles = Directory.GetFiles(dirPath, "BARS-vatSys-*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Skip(MaxLogFiles - 1) // Keep MaxLogFiles - 1 (current one will be the 10th)
                    .ToList();

                foreach (var file in logFiles)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch
                    {
                        // Ignore deletion errors
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        public void Error(string msg)
        {
            try
            {
                // Log to file
                lock (logLock)
                {
                    using (StreamWriter w = File.AppendText(logFilePath))
                    {
                        w.WriteLine("{0} [ERROR] [{1}]: {2}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), name, msg);
                    }
                }

                Errors.Add(new Exception(msg), "BARS");
            }
            catch
            {
            }
        }

        public void Log(string msg)
        {
            try
            {
                lock (logLock)
                {
                    using (StreamWriter w = File.AppendText(logFilePath))
                    {
                        w.WriteLine("{0} [{1}]: {2}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), name, msg);
                    }
                }
            }
            catch
            {
            }
        }
    }
}