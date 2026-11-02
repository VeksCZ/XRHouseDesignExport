using System;
using System.IO;

/// <summary>
/// Writing the user's own data (edits, routes, room names, scans) so that the app being killed halfway - the headset going
/// to sleep, a crash - never leaves a half-written file: the text goes to path.tmp first and replaces the file in one step,
/// the version before is kept as path.bak. Reading falls back to that .bak when the file itself is missing or unreadable.
/// </summary>
public static class SafeFile
{
    public static void WriteAllText(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");   // atomic swap, the old file becomes .bak
            else File.Move(tmp, path);
        }
        catch (Exception)
        {
            // a file system without Replace: copy over (the .tmp already holds the complete text)
            if (File.Exists(path)) { try { File.Copy(path, path + ".bak", true); } catch (Exception) { } }
            File.Copy(tmp, path, true);
            try { File.Delete(tmp); } catch (Exception) { }
        }
    }

    /// <summary>The file's text if 'parse' accepts it, else the .bak's (logged through 'warn'), else null. 'parse' returns false for
    /// a file that doesn't hold what it should (half-written, wrong format).</summary>
    public static string ReadAllTextOrBackup(string path, Func<string, bool> parse, Action<string> warn = null)
    {
        foreach (string p in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(p)) continue;
                string text = File.ReadAllText(p);
                if (parse == null || parse(text))
                {
                    if (p != path) warn?.Invoke($"{path} is missing or unreadable - using its backup ({p})");
                    return text;
                }
            }
            catch (Exception) { }
        }
        return null;
    }
}
