using UnityEngine;

/// <summary>Frame-time stats into the session log ("PERF ...": every 2 s in development builds, every 10 s in release
/// builds) plus a "HITCH" line for any frame over 60 ms (at most one a second), so stalls can be matched to what the
/// app was doing (the surrounding log lines).</summary>
public class XRPerfLog : MonoBehaviour
{
    float elapsed, maxFrame, sumFrame;
    int frames, gcBase;
    float lastHitch = -10f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        var go = new GameObject("XRPerfLog");
        DontDestroyOnLoad(go);
        go.AddComponent<XRPerfLog>();
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        elapsed += dt; sumFrame += dt; frames++;
        if (dt > maxFrame) maxFrame = dt;
        if (dt > 0.06f && Time.unscaledTime - lastHitch > 1f)
        {
            lastHitch = Time.unscaledTime;
            Debug.Log($"HITCH {dt * 1000f:0}ms (frame {Time.frameCount})");
        }
        if (elapsed < (Debug.isDebugBuild ? 2f : 10f)) return;

        int gc = System.GC.CollectionCount(0);
        Debug.Log($"PERF fps={frames / elapsed:0.0} avg={sumFrame / frames * 1000f:0.0}ms max={maxFrame * 1000f:0}ms gc={gc - gcBase} mem={System.GC.GetTotalMemory(false) / 1048576}MB");
        elapsed = sumFrame = maxFrame = 0f; frames = 0; gcBase = gc;
    }
}
