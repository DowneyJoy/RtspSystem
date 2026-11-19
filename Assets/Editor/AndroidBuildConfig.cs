using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace RtspSubsystem.EditorTools
{
    /// <summary>
    /// 一键把项目切换到 Android（横屏 / 局域网 RTSP / ExoPlayer 依赖）配置。
    /// 菜单：Tools/RtspSubsystem/Configure Android Player
    /// </summary>
    public static class AndroidBuildConfig
    {
        private const string MenuPath = "Tools/RtspSubsystem/Configure Android Player";

        [MenuItem(MenuPath)]
        public static void Configure()
        {
            PlayerSettings.companyName = "Downey";
            PlayerSettings.productName = "RTSP Wall";
            PlayerSettings.bundleVersion = "0.1.0";

            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.downey.rtspwall");

            // IL2CPP：Mono 后端在 Android 上只支持 ARMv7，ARM64 必须 IL2CPP
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);

            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARMv7 | AndroidArchitecture.ARM64;

            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.Android.forceSDCardPermission = false;

            // GLES3（原生视图覆盖层在 GLES3 下最稳，避免 Vulkan 驱动差异）
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });

            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;

            EnableGradleTemplates();

            AssetDatabase.SaveAssets();
            Debug.Log("[AndroidConfig] 已配置 Android：横屏 / minSdk24 / ARMv7+ARM64 / Mono / GLES3 / 自定义 Gradle 模板");
        }

        /// <summary>打开自定义 mainTemplate.gradle / gradleTemplate.properties（这两个开关没有公开 API，走序列化字段）。</summary>
        private static void EnableGradleTemplates()
        {
            try
            {
                var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
                if (assets == null || assets.Length == 0)
                {
                    Debug.LogWarning("[AndroidConfig] 找不到 ProjectSettings.asset，Gradle 模板开关需手动勾选");
                    return;
                }

                var so = new SerializedObject(assets[0]);
                foreach (var propName in new[] { "useCustomMainGradleTemplate", "useCustomGradlePropertiesTemplate" })
                {
                    var prop = so.FindProperty(propName);
                    if (prop == null)
                    {
                        Debug.LogWarning($"[AndroidConfig] 未找到属性 {propName}，请在 Player Settings 里手动勾选");
                        continue;
                    }
                    prop.boolValue = true;
                    Debug.Log($"[AndroidConfig] {propName} = true");
                }
                so.ApplyModifiedProperties();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AndroidConfig] 设置 Gradle 模板开关失败：" + e.Message);
            }
        }

        [MenuItem("Tools/RtspSubsystem/Report Android Settings")]
        public static void Report()
        {
            Debug.Log($"[AndroidConfig] backend={PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android)} " +
                      $"min={PlayerSettings.Android.minSdkVersion} target={PlayerSettings.Android.targetSdkVersion} " +
                      $"arch={PlayerSettings.Android.targetArchitectures} " +
                      $"bundle={PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android)} " +
                      $"orientation={PlayerSettings.defaultInterfaceOrientation}");
        }
    }
}
