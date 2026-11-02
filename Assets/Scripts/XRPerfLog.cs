using UnityEngine;

/// <summary>Frame-time stats into the session log ("PERF ...": every 2 s in development builds, every 10 s in release
/// builds) plus a "HITCH" line for any frame over 60 ms (at most one a second), so stalls can be matched to what the
/// app was doing (the surrounding log lines).</summary>
public class XRPerfLog : MonoBehaviour
{
    float elapsed, maxFrame, sumFrame;
    int frames, gcBase;
    float lastHitch = -10f;

    // counters other systems bump; each PERF line reports the change since the last one (only the non-zero ones), so
    // repeated work - a cache that keeps missing, views rebuilt too often - shows up next to the frame times
    public static int RoomsHit, RoomsMiss, RoomsUncached, OverlayRebuilds;
    public static float RoomsMs;
    int lastHit, lastMiss, lastUncached, lastRebuilds; float lastRoomsMs;

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
        // what is on screen: renderers drawn (each is at least one draw call, twice for stereo) and the wall scan's textures
        int renderers = 0;
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None)) if (r.enabled && r.gameObject.activeInHierarchy) renderers++;
        Debug.Log($"PERF fps={frames / elapsed:0.0} avg={sumFrame / frames * 1000f:0.0}ms max={maxFrame * 1000f:0}ms gc={gc - gcBase} mem={System.GC.GetTotalMemory(false) / 1048576}MB renderers={renderers} scanTex={WallScan.TextureMB:0}MB");
        string extra = "";
        if (RoomsHit != lastHit || RoomsMiss != lastMiss || RoomsUncached != lastUncached)
            extra += $" validRooms(hit/miss/loading)={RoomsHit - lastHit}/{RoomsMiss - lastMiss}/{RoomsUncached - lastUncached} computeMs={RoomsMs - lastRoomsMs:0}";
        if (OverlayRebuilds != lastRebuilds) extra += $" overlayRebuilds={OverlayRebuilds - lastRebuilds}";
        if (extra.Length > 0) Debug.Log("PERF calls:" + extra);
        lastHit = RoomsHit; lastMiss = RoomsMiss; lastUncached = RoomsUncached; lastRebuilds = OverlayRebuilds; lastRoomsMs = RoomsMs;
        elapsed = sumFrame = maxFrame = 0f; frames = 0; gcBase = gc;
    }
}
