using UnityEditor;
using UnityEngine;

namespace Rtsp.Editor
{
    /// <summary>
    /// 防止 Editor.log 无限增长（2026-11-19 事故：一夜 35.3GB）。
    /// 根因：本工程内嵌 com.besty.unity-skills 大包 + IL2CPP 构建产生海量文件变更，
    /// Windows 目录监视缓冲区反复溢出（"Directory monitor buffer overflow"），
    /// 溢出 → 全量枚举 → 枚举再产生事件 → 死循环刷日志（Unity 日志无大小上限）。
    /// 官方处理：关闭 Directory Monitoring（EditorPrefs "DirectoryMonitoring"）。
    /// 关闭后变更检测退化为轮询扫描；本工程的自动化链路均显式调用 asset_refresh，不受影响。
    /// </summary>
    [InitializeOnLoad]
    internal static class DirectoryMonitorGuard
    {
        private const string PrefKey = "DirectoryMonitoring";

        static DirectoryMonitorGuard()
        {
            if (EditorPrefs.GetBool(PrefKey, true))
            {
                EditorPrefs.SetBool(PrefKey, false);
                Debug.LogWarning("[DirectoryMonitorGuard] 已关闭目录监视（防止 Editor.log 因缓冲区溢出死循环而撑爆磁盘）。" +
                                 "变更检测退化为轮询扫描，行为不变但稍慢；可用菜单 Tools/RtspSubsystem/Toggle Directory Monitoring 重新开启。");
            }
        }

        [MenuItem("Tools/RtspSubsystem/Toggle Directory Monitoring")]
        private static void Toggle()
        {
            bool now = !EditorPrefs.GetBool(PrefKey, true);
            EditorPrefs.SetBool(PrefKey, now);
            Debug.Log($"[DirectoryMonitorGuard] Directory Monitoring = {now}" +
                      (now ? "（注意：内嵌大包 + 频繁构建时可能触发监视缓冲区溢出循环）" : "（已关闭）"));
        }
    }
}
