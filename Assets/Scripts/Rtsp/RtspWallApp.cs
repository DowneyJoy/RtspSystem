using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace RtspSubsystem
{
    /// <summary>
    /// RTSP 监控墙：纯代码搭建 UI（2×2 分屏 + 点击放大），每格绑定一路
    /// ffmpeg 解码通道。挂上这个组件（或由 RtspWallBootstrap 自动创建）后按 Play 即可出画面。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RtspWallApp : MonoBehaviour
    {
        [Serializable]
        public class ChannelEntry
        {
            public string name;
            public string url;
            public bool audio;
        }

        [Serializable]
        public class ChannelList
        {
            public List<ChannelEntry> channels = new List<ChannelEntry>();

            [Tooltip("Android 端把 127.0.0.1 替换成这个地址（填运行 MediaMTX 的那台电脑的局域网 IP）")]
            public string androidHost = "";
        }

        [Header("解码")]
        [Tooltip("留空则自动查找 StreamingAssets/ffmpeg/ffmpeg.exe → FFMPEG_PATH → 本机安装路径 → PATH")]
        public string ffmpegPathOverride = "";

        [Tooltip("小窗解码尺寸（越小越省 CPU，画面按 16:9 补边不变形）")]
        public int gridWidth = 960;
        public int gridHeight = 540;

        [Tooltip("放大到全屏时切换到的解码尺寸")]
        public int fullscreenWidth = 1920;
        public int fullscreenHeight = 1080;

        [Range(0, 60)] public int targetFps = 25;

        [Header("通道（留空则读取 StreamingAssets/rtsp_channels.json，再退回到内置 cam1~cam4）")]
        public List<ChannelEntry> channels = new List<ChannelEntry>();

        // ------------------------------------------------------------------ 运行时

        private sealed class Cell
        {
            public RectTransform Slot;
            public RectTransform Root;
            public RectTransform VideoRect;
            public RawImage Raw;
            public Text Status;
            public Image Dot;
            public Image Border;
            public RtspScreenView View;
            public RtspStreamChannel Channel;
            public AndroidRtspView AndroidView;
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 OffsetMin;
            public Vector2 OffsetMax;
            public string DisplayName;
        }

        private readonly List<Cell> _cells = new List<Cell>();
        private Canvas _canvas;
        private RectTransform _grid;
        private Text _hint;
        private Font _font;
        private int _fullscreenIndex = -1;
        private StreamAudioPlayer _audioPlayer;
        private readonly List<RtspAudioChannel> _audioChannels = new List<RtspAudioChannel>();
        private bool _isAndroid;
        private bool _nativeAndroid;

        private static readonly Color ColBg = new Color32(11, 14, 18, 255);
        private static readonly Color ColPanel = new Color32(20, 26, 34, 255);
        private static readonly Color ColBorder = new Color32(44, 56, 72, 255);
        private static readonly Color ColBorderActive = new Color32(64, 200, 255, 255);
        private static readonly Color ColText = new Color32(214, 224, 236, 255);
        private static readonly Color ColTextDim = new Color32(122, 138, 156, 255);
        private static readonly Color ColOk = new Color32(64, 214, 128, 255);
        private static readonly Color ColWarn = new Color32(255, 186, 66, 255);
        private static readonly Color ColErr = new Color32(240, 86, 86, 255);

        private void Awake()
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;

            _isAndroid = Application.platform == RuntimePlatform.Android;
            if (_isAndroid)
            {
                Screen.autorotateToPortrait = false;
                Screen.autorotateToPortraitUpsideDown = false;
                Screen.autorotateToLandscapeLeft = true;
                Screen.autorotateToLandscapeRight = true;
                Screen.orientation = ScreenOrientation.LandscapeLeft;
                Screen.sleepTimeout = SleepTimeout.NeverSleep; // 监控墙不能息屏
            }
        }

        private IEnumerator Start()
        {
            _nativeAndroid = AndroidRtspView.Supported;

            string ffmpeg = null;
            if (!_nativeAndroid)
            {
                ffmpeg = FfmpegLocator.Resolve(string.IsNullOrEmpty(ffmpegPathOverride) ? null : ffmpegPathOverride);
                if (string.IsNullOrEmpty(ffmpeg))
                {
                    Debug.LogError("[RTSP] 未找到 ffmpeg.exe：请放到 Assets/StreamingAssets/ffmpeg/ffmpeg.exe，或设置环境变量 FFMPEG_PATH。");
                }
                else
                {
                    Debug.Log($"[RTSP] ffmpeg = {ffmpeg}");
                }
            }
            else
            {
                Debug.Log("[RTSP] Android 原生模式：ExoPlayer 直连 RTSP");
            }

            // Android 上 StreamingAssets 打进 APK（jar: 路径），必须用 UnityWebRequest 读
            List<ChannelEntry> defs = null;
            yield return LoadChannelDefs(r => defs = r);
            BuildUI(defs.Count);
            BuildChannels(defs, ffmpeg);
        }

        private void Update()
        {
            if (_cells.Count == 0) return;

            int playing = 0;
            for (int i = 0; i < _cells.Count; i++)
            {
                var cell = _cells[i];

                if (_nativeAndroid)
                {
                    var av = cell.AndroidView;
                    av?.PollState();
                    string s = av?.State
                        ?? (string.IsNullOrEmpty(AndroidRtspView.ProbeError)
                            ? "error:视图未创建"
                            : "error:" + AndroidRtspView.ProbeError);
                    bool ok = s == "playing";
                    bool err = s.StartsWith("error", StringComparison.OrdinalIgnoreCase);
                    if (ok) playing++;

                    cell.Status.text = DescribeAndroidState(s);
                    cell.Dot.color = ok ? ColOk : err ? ColErr : ColWarn;
                    continue;
                }

                var st = cell.Channel.State;
                if (st == RtspChannelState.Playing) playing++;

                cell.Status.text = DescribeState(cell.Channel);
                cell.Dot.color = st == RtspChannelState.Playing ? ColOk
                    : st == RtspChannelState.Reconnecting || st == RtspChannelState.Connecting ? ColWarn
                    : ColErr;
            }

            if (_hint != null)
            {
                _hint.text = $"{playing}/{_cells.Count} 路在线   ·   点击画面放大   ·   Esc 还原   ·   R 重连全部";
            }

            if (Input.GetKeyDown(KeyCode.Escape) && _fullscreenIndex >= 0) SetFullscreen(-1);
            if (Input.GetKeyDown(KeyCode.R)) RestartAll();

            for (int i = 0; i < _audioChannels.Count; i++) _audioChannels[i].Tick();
        }

        private void OnDestroy()
        {
            foreach (var c in _cells)
            {
                c.Channel?.Dispose();
                c.Channel = null;
                c.AndroidView?.Dispose();
                c.AndroidView = null;
            }
            _cells.Clear();

            foreach (var a in _audioChannels) a?.Dispose();
            _audioChannels.Clear();
        }

        private void OnApplicationQuit()
        {
            OnDestroy();
        }

        // ------------------------------------------------------------------ 通道

        private IEnumerator LoadChannelDefs(Action<List<ChannelEntry>> onDone)
        {
            string androidHost = "";
            ChannelList parsed = null;

            // Android 上 StreamingAssets 是 jar:file:///…!/assets/…，File API 读不到，只能走 UnityWebRequest
            string url = Path.Combine(Application.streamingAssetsPath, "rtsp_channels.json");
            using (var req = UnityWebRequest.Get(url))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try { parsed = JsonUtility.FromJson<ChannelList>(req.downloadHandler.text); }
                    catch (Exception e) { Debug.LogWarning("[RTSP] 解析 rtsp_channels.json 失败：" + e.Message); }
                }
                else
                {
                    Debug.LogWarning("[RTSP] 读取 rtsp_channels.json 失败(" + req.error + ")，使用内置配置");
                }
            }

            if (parsed != null && parsed.channels != null && parsed.channels.Count > 0)
            {
                androidHost = parsed.androidHost ?? "";
                onDone(ApplyHost(parsed.channels, androidHost));
            }
            else
            {
                onDone(ApplyHost(new List<ChannelEntry>
                {
                    new ChannelEntry { name = "CAM1 · 彩条",          url = "rtsp://127.0.0.1:8554/cam1" },
                    new ChannelEntry { name = "CAM2 · 渐变噪点",      url = "rtsp://127.0.0.1:8554/cam2" },
                    new ChannelEntry { name = "CAM3 · SMPTE 640×480", url = "rtsp://127.0.0.1:8554/cam3" },
                    new ChannelEntry { name = "CAM4 · 彩条+440Hz",    url = "rtsp://127.0.0.1:8554/cam4", audio = true },
                }, androidHost));
            }
        }

        /// <summary>Android 真机上 127.0.0.1 指向手机自己，必须换成服务端的局域网 IP。</summary>
        private List<ChannelEntry> ApplyHost(List<ChannelEntry> list, string host)
        {
            if (!_isAndroid || string.IsNullOrEmpty(host)) return list;
            foreach (var e in list)
            {
                if (!string.IsNullOrEmpty(e.url))
                    e.url = e.url.Replace("127.0.0.1", host).Replace("localhost", host);
            }
            Debug.Log($"[RTSP] Android 流地址已重定向到 {host}");
            return list;
        }

        private void BuildChannels(List<ChannelEntry> defs, string ffmpeg)
        {
            for (int i = 0; i < defs.Count && i < _cells.Count; i++)
            {
                var cell = _cells[i];
                var def = defs[i];
                cell.DisplayName = def.name;

                // 标签优先用配置里的名字
                var label = cell.Root.Find("Label")?.GetComponent<Text>();
                if (label != null) label.text = def.name;

                if (_nativeAndroid)
                {
                    // Android：代码里没有 ffmpeg，用 ExoPlayer 原生视图直接拉 RTSP
                    var view = new AndroidRtspView(def.url, def.audio);
                    cell.AndroidView = view;
                    if (view.IsValid && cell.VideoRect != null)
                    {
                        var binder = cell.VideoRect.gameObject.AddComponent<AndroidRtspViewBinder>();
                        binder.Bind(view);
                    }
                    continue;
                }

                var channel = new RtspStreamChannel(def.name, def.url, gridWidth, gridHeight, targetFps, ffmpeg);
                cell.Channel = channel;
                if (cell.View != null && cell.Raw != null)
                {
                    cell.View.Bind(channel, cell.Raw);
                    cell.View.FirstFrameApplied += OnFirstFrame;
                }

                if (def.audio && ffmpeg != null) SetupAudio(def, ffmpeg);

                channel.Start();
            }
        }

        private void OnFirstFrame(RtspScreenView view)
        {
            Debug.Log($"[RTSP] {view.Channel.Name} 首帧到达 {view.Channel.OutputWidth}×{view.Channel.OutputHeight}");
        }

        private void SetupAudio(ChannelEntry def, string ffmpeg)
        {
            if (_audioPlayer == null)
            {
                var listener = Camera.main != null ? Camera.main.gameObject : null;
                if (listener == null)
                {
                    var cam = FindObjectOfType<Camera>();
                    listener = cam != null ? cam.gameObject : new GameObject("AudioSink");
                }
                if (listener.GetComponent<AudioListener>() == null) listener.AddComponent<AudioListener>();
                _audioPlayer = listener.GetComponent<StreamAudioPlayer>();
                if (_audioPlayer == null) _audioPlayer = listener.AddComponent<StreamAudioPlayer>();
            }

            var audio = new RtspAudioChannel(def.name, def.url, ffmpeg);
            _audioChannels.Add(audio);
            _audioPlayer.AddChannel(audio);
            audio.Start();
            Debug.Log($"[RTSP-AUDIO] {def.name} 音频通道已启动");
        }

        private void RestartAll()
        {
            if (_nativeAndroid)
            {
                foreach (var c in _cells) c.AndroidView?.Restart();
                return;
            }
            foreach (var c in _cells) c.Channel?.RequestRestart("手动重连");
        }

        private static string DescribeAndroidState(string state)
        {
            if (string.IsNullOrEmpty(state)) return "初始化…";
            if (state.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                return "连接失败 · " + state.Substring(Mathf.Min(6, state.Length)).Trim(' ', ':');
            switch (state.ToLowerInvariant())
            {
                case "playing": return "ExoPlayer · RTSP";
                case "buffering": return "缓冲中…";
                case "connecting": return "连接中…";
                case "idle": return "未开始";
                case "ended": return "流已结束";
                case "released": return "已释放";
                default: return state;
            }
        }

        private static string DescribeState(RtspStreamChannel ch)
        {
            switch (ch.State)
            {
                case RtspChannelState.Playing: return ch.StatusMessage;
                case RtspChannelState.Connecting: return "连接中…";
                case RtspChannelState.Reconnecting: return ch.StatusMessage;
                case RtspChannelState.Error: return "错误 · " + ch.StatusMessage;
                default: return "未启动";
            }
        }

        // ------------------------------------------------------------------ 全屏

        private void SetFullscreen(int index)
        {
            if (index == _fullscreenIndex) return;

            for (int i = 0; i < _cells.Count; i++)
            {
                var cell = _cells[i];
                bool active = index < 0 || index == i;
                cell.Slot.gameObject.SetActive(active);
                if (!active) continue;

                if (index < 0)
                {
                    cell.Slot.anchorMin = cell.AnchorMin;
                    cell.Slot.anchorMax = cell.AnchorMax;
                    cell.Slot.offsetMin = cell.OffsetMin;
                    cell.Slot.offsetMax = cell.OffsetMax;
                }
                else
                {
                    cell.Slot.anchorMin = Vector2.zero;
                    cell.Slot.anchorMax = Vector2.one;
                    cell.Slot.offsetMin = Vector2.zero;
                    cell.Slot.offsetMax = Vector2.zero;
                }

                int w = index < 0 ? gridWidth : fullscreenWidth;
                int h = index < 0 ? gridHeight : fullscreenHeight;
                cell.Channel?.SetOutputSize(w, h);
                cell.Border.color = index == i ? ColBorderActive : ColBorder;
            }

            _fullscreenIndex = index;
        }

        // ------------------------------------------------------------------ UI 构建

        private void BuildUI(int count)
        {
            _font = CreateFont();

            var canvasGo = new GameObject("RTSP_Canvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            var bg = NewRect("Background", canvasGo.transform);
            Stretch(bg);
            AddImage(bg, ColBg);

            // 顶栏
            var header = NewRect("Header", canvasGo.transform);
            header.anchorMin = new Vector2(0, 1);
            header.anchorMax = new Vector2(1, 1);
            header.pivot = new Vector2(0.5f, 1);
            header.sizeDelta = new Vector2(0, 64);
            header.anchoredPosition = new Vector2(0, -8);

            var titleRt = NewRect("Title", header);
            titleRt.anchorMin = new Vector2(0, 0);
            titleRt.anchorMax = new Vector2(0, 1);
            titleRt.pivot = new Vector2(0, 0.5f);
            titleRt.sizeDelta = new Vector2(600, 0);
            titleRt.anchoredPosition = new Vector2(24, 0);
            var title = AddText(titleRt, "RTSP 监控墙", 30, TextAnchor.MiddleLeft, ColText);
            title.fontStyle = FontStyle.Bold;

            var subRt = NewRect("Subtitle", header);
            subRt.anchorMin = new Vector2(0, 0);
            subRt.anchorMax = new Vector2(0, 1);
            subRt.pivot = new Vector2(0, 0.5f);
            subRt.sizeDelta = new Vector2(900, 0);
            subRt.anchoredPosition = new Vector2(210, 0);
            var sub = AddText(subRt,
                _nativeAndroid ? "ExoPlayer 原生 RTSP · H.264/AAC 硬解 · TextureView 覆盖层"
                               : "ffmpeg 子进程解码 · rgb24 管道 · 无原生插件",
                20, TextAnchor.MiddleLeft, ColTextDim);

            var hintRt = NewRect("Hint", header);
            hintRt.anchorMin = new Vector2(1, 0);
            hintRt.anchorMax = new Vector2(1, 1);
            hintRt.pivot = new Vector2(1, 0.5f);
            hintRt.sizeDelta = new Vector2(760, 0);
            hintRt.anchoredPosition = new Vector2(-24, 0);
            _hint = AddText(hintRt, "", 20, TextAnchor.MiddleRight, ColTextDim);

            // 网格
            _grid = NewRect("Grid", canvasGo.transform);
            _grid.anchorMin = Vector2.zero;
            _grid.anchorMax = Vector2.one;
            _grid.offsetMin = new Vector2(20, 20);
            _grid.offsetMax = new Vector2(-20, -78);

            int cols = 2;
            int rows = Mathf.CeilToInt(count / (float)cols);
            for (int i = 0; i < count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                var cell = new Cell();

                var slot = NewRect("Slot" + (i + 1), _grid);
                float x0 = col / (float)cols;
                float x1 = (col + 1) / (float)cols;
                float y1 = 1f - row / (float)rows;
                float y0 = 1f - (row + 1) / (float)rows;
                slot.anchorMin = new Vector2(x0, y0);
                slot.anchorMax = new Vector2(x1, y1);
                slot.offsetMin = new Vector2(7, 7);
                slot.offsetMax = new Vector2(-7, -7);

                cell.Slot = slot;
                cell.AnchorMin = slot.anchorMin;
                cell.AnchorMax = slot.anchorMax;
                cell.OffsetMin = slot.offsetMin;
                cell.OffsetMax = slot.offsetMax;

                var root = NewRect("Cell", slot);
                Stretch(root);
                var fitter = root.gameObject.AddComponent<AspectRatioFitter>();
                fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fitter.aspectRatio = 16f / 9f;
                cell.Root = root;

                var border = AddImage(root, ColBorder);
                cell.Border = border;

                var inner = NewRect("Inner", root);
                Stretch(inner, 2);
                AddImage(inner, ColPanel);

                // 视频区域：Android 上这块由原生 TextureView 覆盖绘制，所以要给标签/状态条让出位置
                var videoRt = NewRect("Video", inner);
                videoRt.anchorMin = Vector2.zero;
                videoRt.anchorMax = Vector2.one;
                videoRt.offsetMin = new Vector2(1, _nativeAndroid ? 38 : 1);
                videoRt.offsetMax = new Vector2(-1, _nativeAndroid ? -42 : -1);
                cell.VideoRect = videoRt;

                if (!_nativeAndroid)
                {
                    var raw = videoRt.gameObject.AddComponent<RawImage>();
                    raw.color = Color.white;
                    raw.raycastTarget = true;
                    cell.Raw = raw;
                }

                // 名称
                var labelRt = NewRect("Label", root);
                labelRt.anchorMin = new Vector2(0, 1);
                labelRt.anchorMax = new Vector2(0, 1);
                labelRt.pivot = new Vector2(0, 1);
                labelRt.sizeDelta = new Vector2(520, 34);
                labelRt.anchoredPosition = new Vector2(14, -10);
                var labelText = AddText(labelRt, "CAM" + (i + 1), 22, TextAnchor.MiddleLeft, ColText);
                labelText.fontStyle = FontStyle.Bold;
                var shadow = labelRt.gameObject.AddComponent<Shadow>();
                shadow.effectColor = new Color(0, 0, 0, 0.85f);
                shadow.effectDistance = new Vector2(1.5f, -1.5f);

                // 状态点 + 状态文字
                var dotRt = NewRect("Dot", root);
                dotRt.anchorMin = new Vector2(0, 0);
                dotRt.anchorMax = new Vector2(0, 0);
                dotRt.pivot = new Vector2(0, 0);
                dotRt.sizeDelta = new Vector2(10, 10);
                dotRt.anchoredPosition = new Vector2(14, 14);
                cell.Dot = AddImage(dotRt, ColWarn);

                var statusRt = NewRect("Status", root);
                statusRt.anchorMin = new Vector2(0, 0);
                statusRt.anchorMax = new Vector2(1, 0);
                statusRt.pivot = new Vector2(0.5f, 0);
                statusRt.sizeDelta = new Vector2(0, 30);
                statusRt.anchoredPosition = new Vector2(14, 6);
                statusRt.offsetMin = new Vector2(32, 6);
                cell.Status = AddText(statusRt, "未启动", 18, TextAnchor.MiddleLeft, ColTextDim);
                var stShadow = statusRt.gameObject.AddComponent<Shadow>();
                stShadow.effectColor = new Color(0, 0, 0, 0.85f);
                stShadow.effectDistance = new Vector2(1.5f, -1.5f);

                // 点击放大
                var btn = root.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.targetGraphic = border;
                int captured = i;
                btn.onClick.AddListener(() => SetFullscreen(_fullscreenIndex == captured ? -1 : captured));

                if (!_nativeAndroid)
                {
                    cell.View = root.gameObject.AddComponent<RtspScreenView>();
                }
                _cells.Add(cell);
            }
        }

        private Font CreateFont()
        {
            // 直接用系统中文字体，避免 TMP 字体资产缺失导致方块；Android 用系统 Noto/Droid 字体
            string[] names = _isAndroid
                ? new[] { "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans CN", "Droid Sans Fallback", "Roboto" }
                : new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial" };

            Font f = null;
            try { f = Font.CreateDynamicFontFromOSFont(names, 28); }
            catch { }
            if (f == null) f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return f;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.layer = 5; // UI
            return (RectTransform)go.transform;
        }

        private static void Stretch(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }

        private static Image AddImage(RectTransform rt, Color color)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private Text AddText(RectTransform rt, string content, int size, TextAnchor anchor, Color color)
        {
            var t = rt.gameObject.AddComponent<Text>();
            t.font = _font;
            t.text = content;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = color;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }
    }
}
