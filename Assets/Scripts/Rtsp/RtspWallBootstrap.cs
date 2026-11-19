using UnityEngine;

namespace RtspSubsystem
{
    /// <summary>
    /// 免场景配置入口：进入 Play（或启动 Player）后自动创建一个承载监控墙的对象。
    /// 场景里已经有 RtspWallApp 就不会重复创建。
    /// </summary>
    public static class RtspWallBootstrap
    {
        public static bool Enabled = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!Enabled) return;
            if (Object.FindObjectOfType<RtspWallApp>() != null) return;

            var go = new GameObject("RTSP Wall");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<RtspWallApp>();
        }
    }
}
