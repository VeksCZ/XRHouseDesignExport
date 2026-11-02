using System;
using System.IO;
using UnityEngine;

/// <summary>Free storage at a path (bytes), or -1 when it can't be told. Recording photos for a whole house is gigabytes; a full
/// headset makes half-written files, so the scan tool checks it.</summary>
public static class FreeSpace
{
    public static long Bytes(string path)
    {
        try
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var statFs = new AndroidJavaObject("android.os.StatFs", path)) return statFs.Call<long>("getAvailableBytes");
#else
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace;
#endif
        }
        catch (Exception) { return -1; }
    }
}
