using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace QuickLook.Plugin.SaiViewer
{
    /// <summary>
    /// Minimal diagnostics: appends to a log file so a user can report why preview
    /// did or did not show. Never throws.
    /// </summary>
    internal static class Diag
    {
        private static readonly object Gate = new object();
        private static readonly string[] Targets =
        {
            Path.Combine(Path.GetTempPath(), "QuickLook.Plugin.SaiViewer.log")
        };

        public static void Log(string message)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                       + " [pid " + Process.GetCurrentProcess().Id + "] " + message
                       + Environment.NewLine;

            lock (Gate)
            {
                foreach (var target in Targets)
                {
                    try
                    {
                        File.AppendAllText(target, line, Encoding.UTF8);
                    }
                    catch
                    {
                        // diagnostics must never break previewing
                    }
                }
            }
        }
    }
}
