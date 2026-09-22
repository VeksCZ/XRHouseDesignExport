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
}
