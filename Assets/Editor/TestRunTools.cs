using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

/// <summary>
/// Starts the EditMode / PlayMode tests without the Test Runner window, so tooling (e.g. the Unity MCP bridge, which
/// can call static methods) can run them. Results are logged and end up in the usual TestResults.xml.
/// </summary>
public static class TestRunTools
{
    class Log : ICallbacks
    {
        public void RunStarted(ITestAdaptor testsToRun) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result)
        {
            if (!result.HasChildren) Debug.Log($"[TestRun] {result.TestStatus}: {result.Test.FullName} {result.Message}");
        }
        public void RunFinished(ITestResultAdaptor result) =>
            Debug.Log($"[TestRun] FINISHED {result.TestStatus}: passed {result.PassCount}, failed {result.FailCount}, skipped {result.SkipCount}");
    }

    static TestRunnerApi api;

    static void Run(TestMode mode)
    {
        api = ScriptableObject.CreateInstance<TestRunnerApi>();
        api.RegisterCallbacks(new Log());
        api.Execute(new ExecutionSettings(new Filter { testMode = mode }));
    }

    [MenuItem("MRUK/Tests/Run EditMode")] public static void RunEditMode() => Run(TestMode.EditMode);
    [MenuItem("MRUK/Tests/Run PlayMode")] public static void RunPlayMode() => Run(TestMode.PlayMode);

    // Adds a shader to Always Included Shaders through Unity's own SerializedObject API instead of hand-editing
    // ProjectSettings/GraphicsSettings.asset - a raw text edit to that file was found to get silently reverted by
    // Unity (once after AssetDatabase.Refresh(), once across an editor restart, which that time also corrupted
    // m_CustomRenderPipeline to null). Going through SerializedObject/ApplyModifiedProperties + SaveAssets writes
    // it the same way Unity's own Graphics settings UI would, so it persists reliably.
    [MenuItem("MRUK/Fix/Add URP Unlit To Always Included Shaders")]
    public static void AddUnlitToAlwaysIncludedShaders()
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) { Debug.LogError("[GraphicsFix] Could not find shader 'Universal Render Pipeline/Unlit'."); return; }

        var graphicsSettingsObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
        if (graphicsSettingsObj == null) { Debug.LogError("[GraphicsFix] Could not load ProjectSettings/GraphicsSettings.asset."); return; }

        var so = new SerializedObject(graphicsSettingsObj);
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        for (int i = 0; i < arr.arraySize; i++)
        {
            if (arr.GetArrayElementAtIndex(i).objectReferenceValue == shader)
            {
                Debug.Log("[GraphicsFix] Unlit shader is already in Always Included Shaders.");
                return;
            }
        }

        int idx = arr.arraySize;
        arr.InsertArrayElementAtIndex(idx);
        arr.GetArrayElementAtIndex(idx).objectReferenceValue = shader;
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log("[GraphicsFix] Added Universal Render Pipeline/Unlit to Always Included Shaders.");
    }
}
