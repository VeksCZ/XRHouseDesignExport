using UnityEngine;

/// <summary>Development builds only: logs frame-time stats every two seconds ("PERF ...") so hitches can be read from logcat.</summary>
public class XRPerfLog : MonoBehaviour
{
    float elapsed, maxFrame, sumFrame;
    int frames, gcBase;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        if (!Debug.isDebugBuild) return;
        var go = new GameObject("XRPerfLog");
        DontDestroyOnLoad(go);
        go.AddComponent<XRPerfLog>();
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        elapsed += dt; sumFrame += dt; frames++;
        if (dt > maxFrame) maxFrame = dt;
        if (elapsed < 2f) return;

        int gc = System.GC.CollectionCount(0);
        Debug.Log($"PERF fps={frames / elapsed:0.0} avg={sumFrame / frames * 1000f:0.0}ms max={maxFrame * 1000f:0}ms gc={gc - gcBase} mem={System.GC.GetTotalMemory(false) / 1048576}MB");
        elapsed = sumFrame = maxFrame = 0f; frames = 0; gcBase = gc;
    }
}
