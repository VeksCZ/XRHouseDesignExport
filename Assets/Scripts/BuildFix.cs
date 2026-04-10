#if !UNITY_EDITOR
namespace Unity.AI.Tracing {
    public static class TraceSinkConfigManager {
        public static int MaxFileSizeMB = 10;
        public static int TrimFileSizeMB = 15;
    }
}
#endif