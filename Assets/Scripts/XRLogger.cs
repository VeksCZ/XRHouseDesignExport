using UnityEngine;
using System;
using System.IO;
using System.Text;

public static class XRLogger
{
    private static string logFilePath;
    private static StreamWriter writer;
    private static StringBuilder sessionLog = new StringBuilder();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static void Initialize()
    {
        try
        {
            string root = Application.isEditor ? "Exports/Logs" : "/sdcard/Download/XRHouseExports/Logs";
            
            // Fallback for Android permissions
            try 
            { 
                if (!Directory.Exists(root)) Directory.CreateDirectory(root); 
            }
            catch 
            { 
                root = Path.Combine(Application.persistentDataPath, "Logs");
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            }

            string fileName = $"Session_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            logFilePath = Path.Combine(root, fileName);

            Application.logMessageReceived += HandleLog;
            Application.quitting += Flush;
            
            Debug.Log("==========================================");
            Debug.Log($"XRLogger Initialized");
            Debug.Log($"Time: {DateTime.Now}");
            Debug.Log($"Platform: {Application.platform}");
            Debug.Log($"Unity: {Application.version}");
            Debug.Log($"Path: {logFilePath}");
            Debug.Log("==========================================");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[XRLogger] Failed to initialize: {ex.Message}");
        }
    }

    private static void HandleLog(string logString, string stackTrace, LogType type)
    {
        string prefix = type switch
        {
            LogType.Error => "[ERROR]",
            LogType.Assert => "[ASSERT]",
            LogType.Warning => "[WARN]",
            LogType.Log => "[INFO]",
            LogType.Exception => "[EXCEPTION]",
            _ => "[UNKNOWN]"
        };

        string entry = $"[{DateTime.Now:HH:mm:ss.fff}] {prefix} {logString}\n";
        
        lock (sessionLog)
        {
            sessionLog.Append(entry);
            if (type == LogType.Error || type == LogType.Exception)
            {
                sessionLog.AppendLine(stackTrace);
            }

            if (type == LogType.Error || type == LogType.Exception || sessionLog.Length > 2000)
            {
                Flush();
            }
        }
    }

    public static void Flush()
    {
        if (string.IsNullOrEmpty(logFilePath)) return;

        try
        {
            lock (sessionLog)
            {
                if (sessionLog.Length == 0) return;
                File.AppendAllText(logFilePath, sessionLog.ToString());
                // Also try to copy to a generic 'session_debug_log.txt' in the same folder for the user
                string folder = Path.GetDirectoryName(logFilePath);
                string genericLog = Path.Combine(folder, "session_debug_log.txt");
                File.WriteAllText(genericLog, File.ReadAllText(logFilePath));
                
                sessionLog.Clear();
            }
        }
        catch { }
    }
}
