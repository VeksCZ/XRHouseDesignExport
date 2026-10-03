using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Management;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Windows desktop app (scene DesktopWalkScene): pick a saved scan from the data folder - the same Exports/RoomData
/// the Editor pulls the Quest's exports and saved scans into - and walk through it 1:1 with keyboard and mouse.
/// Everything below the input is the Quest app's own code: MRUK loads the scan JSON, HouseEditsStore/RoomNames bind
/// its edits and names, WalkThroughMode builds and walks the model (stairs, door leaves, floors, Anchor/Mesh/Raw).
/// Edits are read-only here; they are made in the headset (or the Editor) and show up after a reload.
/// </summary>
public class DesktopWalkApp : MonoBehaviour
{
    public WalkThroughMode walk;
    public DesktopWalkRig rig;

    const string PrefLastScan = "DesktopWalk.LastScan";

    List<string> scans = new List<string>();
    int selected = -1;
    string loadedScan;
    string status = "";
    bool menuOpen = true, busy, showHelp = true;
    Vector2 scroll;
    string floorInfo = "";

    void Awake()
    {
        StopXR();
        walk ??= FindAnyObjectByType<WalkThroughMode>() ?? gameObject.AddComponent<WalkThroughMode>();
        rig ??= FindAnyObjectByType<DesktopWalkRig>();
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 1;
        // Big flat-coloured surfaces: edges stair-step badly without MSAA. Not in the Editor (it'd be saved to the asset).
        if (!Application.isEditor && UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp)
        {
            urp.msaaSampleCount = 4;
            urp.renderScale = 1f;
        }
    }

    async void Start()
    {
        RefreshScans();
        string last = Arg("-scan") ?? PlayerPrefs.GetString(PrefLastScan, "");
        selected = Mathf.Max(0, scans.IndexOf(last));
        if (scans.Count == 0) status = $"No scans in {DataFolder}. Pull data from the Quest in the Editor (MRUK > 5. Pull data from Quest).";
        else if (scans.Contains(last)) await Load(last); // straight back into the house walked last time

        // Smoke test: XRHouseWalk.exe -scan <name> -shot <png> loads, captures one frame and quits.
        string shot = Arg("-shot");
        if (shot != null)
        {
            for (int i = 0; i < 30; i++) await Task.Yield();
            ScreenCapture.CaptureScreenshot(shot);
            for (int i = 0; i < 10; i++) await Task.Yield();
            Debug.Log($"[DesktopWalkApp] shot {shot}, walk on: {walk.IsOn}, status: {status}");
            Application.Quit(walk.IsOn ? 0 : 2);
        }
    }

    static string Arg(string name)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
        return null;
    }

    /// <summary>The Standalone XR settings (shared with Editor play mode for Link testing) start OpenXR on launch -
    /// the desktop app is a flat window, so shut it down if it came up.</summary>
    static void StopXR()
    {
        var settings = XRGeneralSettings.Instance;
        var mgr = settings ? settings.Manager : null;
        if (mgr == null || !mgr.isInitializationComplete) return;
        mgr.StopSubsystems();
        mgr.DeinitializeLoader();
        Debug.Log("[DesktopWalkApp] XR was running - stopped, desktop mode.");
    }

    static string DataFolder => System.IO.Path.GetFullPath(MRUKPathUtility.GetExportRoot());

    void RefreshScans()
    {
        try { scans = MRUKSceneCache.ListCachedScans(); }
        catch (Exception ex) { scans = new List<string>(); status = "Cannot read data folder: " + ex.Message; }
        if (selected >= scans.Count) selected = scans.Count - 1;
    }

    async Task Load(string scan)
    {
#if META_XR_SDK_INSTALLED
        if (busy || string.IsNullOrEmpty(scan)) return;
        busy = true;
        status = $"Loading {scan}...";
        try
        {
            if (MRUK.Instance == null) { status = "MRUK missing in the scene."; return; }
            walk.Exit(); // it holds the old scan's rooms - MRUK is about to replace them
            if (!await MRUKSceneCache.LoadCachedScene(MRUK.Instance, scan)) { status = $"Could not load {scan}."; return; }
            HouseEditsStore.Bind(scan);
            RoomNames.FilePath = MRUKSceneCache.RoomNamesPath(scan);
            rig?.ResetView();
            if (!await walk.Enter(null, null)) { status = $"{scan}: no walkable rooms."; return; }
            loadedScan = scan;
            floorInfo = "";
            PlayerPrefs.SetString(PrefLastScan, scan);
            status = "";
            SetMenu(false);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            status = "Error: " + ex.Message;
        }
        finally { busy = false; }
#else
        await Task.CompletedTask;
        status = "Meta XR SDK (MRUK) not installed.";
#endif
    }

    void SetMenu(bool open)
    {
        menuOpen = open;
        bool look = !open && walk && walk.IsOn;
        Cursor.lockState = look ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !look;
        if (rig) rig.InputEnabled = look;
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null) return;

        if (kb.escapeKey.wasPressedThisFrame) SetMenu(!(menuOpen && walk.IsOn)); // nothing loaded = the menu stays
        if (menuOpen || busy || !walk.IsOn) return;

        // Alt-tab etc. releases the cursor without opening the menu - a click takes it back.
        if (Cursor.lockState != CursorLockMode.Locked)
        {
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) SetMenu(false);
            return;
        }

        if (mouse != null && mouse.leftButton.wasPressedThisFrame) walk.TryToggleDoor();
        if (kb.fKey.wasPressedThisFrame || kb.pageUpKey.wasPressedThisFrame) floorInfo = walk.NextFloor() ?? "";
        if (kb.mKey.wasPressedThisFrame) _ = CycleModel();
        if (kb.hKey.wasPressedThisFrame) showHelp = !showHelp;
        if (kb.f5Key.wasPressedThisFrame && loadedScan != null) _ = Load(loadedScan);
    }

    async Task CycleModel()
    {
        if (busy) return;
        busy = true;
        try { await walk.CycleModel(); }
        finally { busy = false; }
    }

    // ---------- UI (IMGUI: no prefabs/canvas to set up, scales with the window) ----------

    GUIStyle box, label, small, button, title;

    void EnsureStyles()
    {
        if (box != null) return;
        var bg = new Texture2D(1, 1);
        bg.SetPixel(0, 0, new Color(0.08f, 0.09f, 0.11f, 0.88f));
        bg.Apply();
        box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(16, 16, 14, 14) };
        box.normal.background = bg;
        label = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true };
        label.normal.textColor = new Color(0.92f, 0.93f, 0.95f);
        small = new GUIStyle(label) { fontSize = 15 };
        small.normal.textColor = new Color(0.7f, 0.72f, 0.76f);
        title = new GUIStyle(label) { fontSize = 26, fontStyle = FontStyle.Bold };
        button = new GUIStyle(GUI.skin.button) { fontSize = 18, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(12, 12, 8, 8) };
    }

    void OnGUI()
    {
        EnsureStyles();
        float s = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        float w = Screen.width / s, h = Screen.height / s;

        if (menuOpen || !walk.IsOn) DrawMenu(w, h);
        else
        {
            DrawHud(h);
            // crosshair: shows where a click opens/closes a door
            GUI.Label(new Rect(w / 2 - 6, h / 2 - 13, 20, 26), "+", label);
        }
    }

    void DrawMenu(float w, float h)
    {
        float pw = Mathf.Min(720f, w - 40f), ph = Mathf.Min(760f, h - 40f);
        GUILayout.BeginArea(new Rect((w - pw) / 2, (h - ph) / 2, pw, ph), box);
        GUILayout.Label("XR House Design – walk-through", title);
        GUILayout.Label("Data: " + DataFolder, small);
        GUILayout.Space(8);

        scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
        for (int i = 0; i < scans.Count; i++)
        {
            string name = scans[i] + (scans[i] == loadedScan ? "   (loaded)" : "");
            bool on = GUILayout.Toggle(i == selected, name, button);
            if (on && i != selected) selected = i;
        }
        if (scans.Count == 0) GUILayout.Label("No scans found.", label);
        GUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(status)) GUILayout.Label(status, label);
        GUILayout.Space(6);
        GUI.enabled = !busy;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(busy ? "Loading..." : "Walk", button, GUILayout.Height(44)) && selected >= 0 && selected < scans.Count)
            _ = Load(scans[selected]);
        if (walk.IsOn && GUILayout.Button("Continue", button, GUILayout.Height(44))) SetMenu(false);
        if (GUILayout.Button("Refresh", button, GUILayout.Height(44))) RefreshScans();
        if (GUILayout.Button("Quit", button, GUILayout.Height(44))) Application.Quit();
        GUILayout.EndHorizontal();
        GUI.enabled = true;
        GUILayout.EndArea();
    }

    void DrawHud(float h)
    {
        string text = $"{loadedScan}  ·  {walk.ModelLabel.Replace("Walk: ", "")}" + (floorInfo != "" ? "  ·  " + floorInfo : "");
        if (showHelp)
            text += "\nWASD move · mouse look · Shift run · Ctrl slow · click door open/close · F next floor · M model · F5 reload · Esc menu · H hide";
        float bh = showHelp ? 74 : 44;
        GUI.Box(new Rect(12, h - bh - 12, 1100, bh), GUIContent.none, box);
        GUI.Label(new Rect(26, h - bh - 4, 1080, bh), text, small);
    }
}
