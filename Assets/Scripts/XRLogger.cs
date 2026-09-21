using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Writes the Unity console to a session file on the headset. Lines are queued and appended by a background
/// thread - the game thread only enqueues a string - the file is never read back, its size is capped and only
/// the newest few sessions are kept. (The earlier version re-read and re-wrote the whole, ever-growing log on
/// each flush; with 150-200 MB logs that froze the app for ~half a second every couple of seconds.)
/// </summary>
public static class XRLogger
{
    const int KeepSessions = 5;
    const long MaxBytes = 8L * 1024 * 1024;

    static readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
    static readonly AutoResetEvent wake = new AutoResetEvent(false);
    static Thread worker;
    static volatile bool running;
    static string logFilePath;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static void Initialize()
    {
        if (Application.isEditor || running) return;
        try
        {
            string root = MRUKPathUtility.GetLogRoot();
            try { Directory.CreateDirectory(root); }
            catch
            {
                root = Path.Combine(Application.persistentDataPath, "Logs");
                Directory.CreateDirectory(root);
            }

            DeleteOldSessions(root);
            logFilePath = Path.Combine(root, $"Session_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            running = true;
            worker = new Thread(WriteLoop) { IsBackground = true, Name = "XRLogger" };
            worker.Start();
            Application.logMessageReceivedThreaded += OnLog;
            Application.quitting += Stop;

            Debug.Log($"XRLogger: {logFilePath} | {Application.platform} | Unity {Application.version} | {SystemInfo.deviceModel}");
        }
        catch (Exception ex)
        {
            running = false;
            Debug.LogError($"[XRLogger] Failed to initialize: {ex.Message}");
        }
    }

    static void DeleteOldSessions(string root)
    {
        try
        {
            var files = new DirectoryInfo(root).GetFiles("Session_*.txt").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var f in files.Skip(KeepSessions - 1)) f.Delete(); // leave room for the session about to start
            string legacy = Path.Combine(root, "session_debug_log.txt");
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch { /* cleanup is best effort */ }
    }

    static void OnLog(string message, string stackTrace, LogType type)
    {
        if (!running) return;
        string level = type switch
        {
            LogType.Error => "ERROR", LogType.Assert => "ASSERT", LogType.Warning => "WARN", LogType.Exception => "EXCEPTION", _ => "INFO",
        };
        var line = new StringBuilder(message.Length + 32).Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] [").Append(level).Append("] ").Append(message);
        if (type != LogType.Log && type != LogType.Warning) line.Append('\n').Append(stackTrace);
        queue.Enqueue(line.ToString());
        wake.Set();
    }

    static void WriteLoop()
    {
        long written = 0;
        try
        {
            using var stream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            while (running || !queue.IsEmpty)
            {
                wake.WaitOne(1000);
                var batch = new StringBuilder();
                while (queue.TryDequeue(out var line)) batch.AppendLine(line);
                if (batch.Length == 0) continue;

                if (written >= MaxBytes) continue; // over the cap: drop the rest of the session
                byte[] bytes = Encoding.UTF8.GetBytes(batch.ToString());
                stream.Write(bytes, 0, bytes.Length);
                written += bytes.Length;
                if (written >= MaxBytes)
                {
                    byte[] note = Encoding.UTF8.GetBytes($"[log limit of {MaxBytes / 1024 / 1024} MB reached - the rest of this session is not written]\n");
                    stream.Write(note, 0, note.Length);
                }
                stream.Flush();
            }
        }
        catch { running = false; }
    }

    static void Stop()
    {
        running = false;
        wake.Set();
        worker?.Join(500);
    }

    /// <summary>Kept for callers that used to force a flush; the background thread writes continuously.</summary>
    public static void Flush() => wake.Set();
}
