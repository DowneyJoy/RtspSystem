using System;
using UnityEngine;

namespace RtspSubsystem
{
    /// <summary>
    /// Android 端 RTSP 视图（ExoPlayer + TextureView 原生覆盖层）的 C# 封装。
    /// 画面由原生视图绘制在 Unity 之上，Unity 只负责把布局矩形同步过去。
    /// 非 Android 平台上所有方法都是空操作，方便同一份 UI 代码跨平台。
    /// </summary>
    public sealed class AndroidRtspView : IDisposable
    {
        private const string JavaClass = "com.rtspunity.RtspPlayerView";

        private static bool _probed;
        private static bool _available;
        private static string _probeError;

        /// <summary>Supported 为 false 时的原因（显示在画面状态里，方便无 logcat 排查）。</summary>
        public static string ProbeError => _probeError;

        /// <summary>当前平台是否支持原生 RTSP 视图（Android 且 Java 插件已打进包）。</summary>
        public static bool Supported
        {
            get
            {
                if (Application.platform != RuntimePlatform.Android) return false;
                if (!_probed)
                {
                    _probed = true;
                    try
                    {
                        using (var cls = new AndroidJavaClass(JavaClass))
                        {
                            _available = cls != null;
                        }
                    }
                    catch (Exception e)
                    {
                        _available = false;
                        _probeError = e.Message;
                        Debug.LogError($"[RTSP-Android] 找不到 Java 类 {JavaClass}（插件没编进 APK？）：{e.Message}");
                    }
                }
                return _available;
            }
        }

        public string Url { get; }
        public bool AudioEnabled { get; }
        public bool IsValid => _view != null;
        public bool IsPlaying => string.Equals(State, "playing", StringComparison.OrdinalIgnoreCase);
        public string State { get; private set; } = "idle";

        private AndroidJavaObject _view;

        public AndroidRtspView(string url, bool audioEnabled)
        {
            Url = url;
            AudioEnabled = audioEnabled;

            if (!Supported) return;

            try
            {
                _view = new AndroidJavaObject(JavaClass, url, audioEnabled);
                Debug.Log($"[RTSP-Android] 创建视图 {url} audio={audioEnabled}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[RTSP-Android] 创建视图失败 {url}: {e.Message}");
                _view = null;
                State = "error:" + e.Message;
            }
        }

        /// <summary>矩形单位：屏幕像素，原点左上角。</summary>
        public void SetRect(float left, float top, float width, float height)
        {
            if (_view == null) return;
            try { _view.Call("setRect", left, top, width, height); }
            catch (Exception e) { Debug.LogWarning($"[RTSP-Android] setRect 失败: {e.Message}"); }
        }

        public void SetVisible(bool visible)
        {
            if (_view == null) return;
            try { _view.Call("setVisible", visible); }
            catch (Exception e) { Debug.LogWarning($"[RTSP-Android] setVisible 失败: {e.Message}"); }
        }

        public void SetVolume(float volume)
        {
            if (_view == null) return;
            try { _view.Call("setVolume", Mathf.Clamp01(volume)); } catch { }
        }

        public void Restart()
        {
            if (_view == null) return;
            try { _view.Call("restart"); } catch (Exception e) { Debug.LogWarning($"[RTSP-Android] restart 失败: {e.Message}"); }
        }

        /// <summary>刷新一次状态字符串（每帧调用，JNI 调用开销很小）。</summary>
        public void PollState()
        {
            if (_view == null) return;
            try { State = _view.Call<string>("getState"); }
            catch { State = "unknown"; }
        }

        public void Dispose()
        {
            if (_view == null) return;
            try { _view.Call("release"); } catch { }
            try { _view.Dispose(); } catch { }
            _view = null;
            State = "released";
        }
    }
}
