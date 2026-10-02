using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Logs one "STARTUP ..." line per launch with the time (seconds since process start, where available, and since
/// Unity's own clock started) at which each startup phase was reached, so slow launches can be read from the log.
/// The early phases run before XRLogger is up, so their times are only collected and logged after the first frame.
/// </summary>
public class XRStartupTiming : MonoBehaviour
{
    static float tSubsystems = -1f, tBeforeSplash = -1f, tBeforeScene = -1f, tAfterScene = -1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void OnSubsystems() => tSubsystems = Time.realtimeSinceStartup;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
    static void OnBeforeSplash() => tBeforeSplash = Time.realtimeSinceStartup;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void OnBeforeScene() => tBeforeScene = Time.realtimeSinceStartup;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void OnAfterScene()
    {
        tAfterScene = Time.realtimeSinceStartup;
        var go = new GameObject("XRStartupTiming");
        DontDestroyOnLoad(go);
        go.AddComponent<XRStartupTiming>();
    }

    IEnumerator Start()
    {
        yield return null;                    // first frame has been rendered
        float tFirstFrame = Time.realtimeSinceStartup;
        yield return new WaitForEndOfFrame(); // XR frame loop running
        float tSecondFrame = Time.realtimeSinceStartup;

        // Time between OS process start and Unity's clock starting (Unity engine/player bootstrap). Best effort.
        string proc = "n/a";
        try
        {
            var start = System.Diagnostics.Process.GetCurrentProcess().StartTime;
            double sinceProc = (DateTime.Now - start).TotalSeconds;
            proc = $"{sinceProc - tSecondFrame:0.00}s";
        }
        catch { }

        Debug.Log($"STARTUP processToUnity={proc} subsystems={tSubsystems:0.00}s beforeSplash={tBeforeSplash:0.00}s " +
                  $"beforeScene={tBeforeScene:0.00}s afterScene={tAfterScene:0.00}s firstFrame={tFirstFrame:0.00}s " +
                  $"frameLoop={tSecondFrame:0.00}s (Unity clock)");
        Destroy(gameObject);
    }
}
