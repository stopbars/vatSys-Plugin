using System;
using System.IO;
using vatsys;

namespace BARS.Util
{
    public class Logger
    {
        private string dirPath = $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\BARS";
        private string name;

        public Logger(string Name)
        {
            name = Name;
        }

        public void Error(string msg)
        {
            try
            {
                // Log to file
                using (StreamWriter w = File.AppendText($"{dirPath}\\BARS-V2.log"))
                {
                    w.WriteLine("{0} [ERROR] [{1}]: {2}", DateTime.UtcNow.ToLongTimeString(), name, msg);
                }

                // Report to vatsys error window
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
                using (StreamWriter w = File.AppendText($"{dirPath}\\BARS-V2.log"))
                {
                    w.WriteLine("{0} [{1}]: {2}", DateTime.UtcNow.ToLongTimeString(), name, msg);
                }
            }
            catch
            {
            }
        }
    }
}