using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RtspSubsystem
{
    public enum RtspChannelState
    {
        Idle,
        Connecting,
        Playing,
        Reconnecting,
        Error
    }

    /// <summary>
    /// 单路 RTSP 通道：拉起一个 ffmpeg 子进程做硬解/软解，把解码后的原始 RGB 帧
    /// 从标准输出管道搬到内存双缓冲，主线程再上传到 Texture2D。
    ///
    /// 为什么用子进程而不是原生插件：
    ///   - 不依赖任何 native 插件 / 授权，换机器只要带一个 ffmpeg.exe；
    ///   - ffmpeg 自带 RTSP(TCP/UDP)、H264、AAC 全套实现，兼容性等同于 VLC；
    ///   - 崩溃隔离：单路 ffmpeg 挂掉不影响 Unity 进程。
    /// 代价：每路多一次进程间拷贝 + sws 转换，所以默认在解码端就缩放到 960x540 给小窗用。
    /// </summary>
    public sealed class RtspStreamChannel : IDisposable
    {
        private const int FrameStallMs = 5000;      // 多久没新帧算卡死
        private const float ConnectTimeoutSec = 15f; // 首帧超时
        private const float MaxRestartDelay = 8f;

        public string Name { get; private set; }
        public string Url { get; private set; }
        public int OutputWidth { get; private set; }
        public int OutputHeight { get; private set; }
        public int TargetFps { get; private set; }

        public RtspChannelState State { get; private set; } = RtspChannelState.Idle;
        public string StatusMessage { get; private set; } = "未启动";
        public float MeasuredFps => _measuredFps;
        public int RestartCount => _restartCount;
        public string LastError => _lastError;

        private readonly string _ffmpeg;
        private readonly object _sync = new object();

        private Process _proc;
        private Thread _readThread;
        private Thread _errThread;

        private byte[] _front;
        private byte[] _back;
        private int _frameBytes;
        private int _frameId;
        private int _appliedId;
        private long _lastFrameTicks;
        private bool _gotFirstFrame;

        private float _measuredFps;
        private int _fpsCounter;
        private float _fpsTimer;

        private bool _started;
        private bool _alive;
        private volatile string _lastError = string.Empty;
        private int _restartCount;
        private float _restartDelay = 1f;
        private float _nextRestartAt;
        private float _startedAt;
        private string _lastStderr = string.Empty;

        public RtspStreamChannel(string name, string url, int width, int height, int targetFps, string ffmpegPath)
        {
            Name = name;
            Url = url;
            OutputWidth = Mathf.Max(64, width);
            OutputHeight = Mathf.Max(64, height);
            TargetFps = Mathf.Clamp(targetFps, 0, 60);
            _ffmpeg = ffmpegPath;
        }

        // ------------------------------------------------------------------ 生命周期

        public void Start()
        {
            if (_started) return;
            _started = true;
            _restartDelay = 1f;
            StartProcess();
        }

        public void RequestRestart(string reason)
        {
            if (!_started) return;
            StopProcess();
            ScheduleRestart(reason);
        }

        /// <summary>切换输出分辨率（小窗 / 全屏切换时用），会重启解码进程。</summary>
        public void SetOutputSize(int width, int height)
        {
            if (OutputWidth == width && OutputHeight == height) return;
            OutputWidth = Mathf.Max(64, width);
            OutputHeight = Mathf.Max(64, height);
            lock (_sync)
            {
                _front = null;
                _back = null;
                _frameId = 0;
                _appliedId = 0;
            }
            if (_started) RequestRestart("切换分辨率");
        }

        public void Dispose()
        {
            _started = false;
            StopProcess();
        }

        // ------------------------------------------------------------------ 进程管理

        private void StartProcess()
        {
            if (!_started) return;
            if (string.IsNullOrEmpty(_ffmpeg) || !File.Exists(_ffmpeg))
            {
                State = RtspChannelState.Error;
                StatusMessage = "找不到 ffmpeg";
                _lastError = "ffmpeg 路径无效: " + (_ffmpeg ?? "<null>");
                return;
            }

            _frameBytes = OutputWidth * OutputHeight * 3;
            lock (_sync)
            {
                _front = new byte[_frameBytes];
                _back = new byte[_frameBytes];
                _frameId = 0;
                _appliedId = 0;
            }
            _gotFirstFrame = false;
            _lastFrameTicks = 0;
            _measuredFps = 0f;
            _fpsCounter = 0;
            _fpsTimer = 0f;
            _alive = true;
            _startedAt = Time.realtimeSinceStartup;
            State = RtspChannelState.Connecting;
            StatusMessage = "连接中…";

            try
            {
                var psi = new ProcessStartInfo(_ffmpeg, BuildArguments())
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(_ffmpeg) ?? Environment.CurrentDirectory
                };

                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.Start();

                _readThread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "rtsp-read-" + Name
                };
                _readThread.Start(_proc.StandardOutput.BaseStream);

                _errThread = new Thread(ErrorLoop)
                {
                    IsBackground = true,
                    Name = "rtsp-log-" + Name
                };
                _errThread.Start(_proc.StandardError);
            }
            catch (Exception e)
            {
                _lastError = e.Message;
                Debug.LogError($"[RTSP:{Name}] 启动 ffmpeg 失败: {e.Message}");
                StopProcess();
                ScheduleRestart("启动失败");
            }
        }

        private void StopProcess()
        {
            _alive = false;

            var proc = _proc;
            _proc = null;
            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited) proc.Kill();
                }
                catch { }
                try { proc.StandardOutput.BaseStream?.Close(); } catch { }
                try { proc.StandardError?.Close(); } catch { }
                try { proc.Dispose(); } catch { }
            }

            JoinThread(_readThread);
            JoinThread(_errThread);
            _readThread = null;
            _errThread = null;
        }

        private static void JoinThread(Thread t)
        {
            if (t == null) return;
            try
            {
                if (!t.Join(300)) { /* 读阻塞的线程会在管道关闭后自然退出 */ }
            }
            catch { }
        }

        private void ScheduleRestart(string reason)
        {
            if (!_started) return;
            _restartCount++;
            State = RtspChannelState.Reconnecting;
            string detail = string.IsNullOrEmpty(_lastStderr) ? string.Empty : " · " + Trim(_lastStderr, 60);
            StatusMessage = $"重连中({reason}) {_restartDelay:0.0}s{detail}";
            _nextRestartAt = Time.realtimeSinceStartup + _restartDelay;
            _restartDelay = Mathf.Min(_restartDelay * 1.5f, MaxRestartDelay);
        }

        // ------------------------------------------------------------------ 后台线程

        private void ReadLoop(object arg)
        {
            var stream = (Stream)arg;
            var buf = new byte[_frameBytes];

            while (_alive)
            {
                if (!ReadExact(stream, buf, _frameBytes)) break;

                lock (_sync)
                {
                    var tmp = _front;
                    _front = buf;
                    buf = tmp;
                    _frameId++;
                }
                _lastFrameTicks = Stopwatch.GetTimestamp();
                _gotFirstFrame = true;
            }

            if (_alive)
            {
                // 管道断 = ffmpeg 退出（流中断 / 参数错误 / 被 kill）
                _lastFrameTicks = 0;
            }
        }

        private void ErrorLoop(object arg)
        {
            var reader = arg as StreamReader;
            if (reader == null) return;
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    _lastStderr = line.Trim();
                    if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _lastError = _lastStderr;
                        Debug.LogWarning($"[RTSP:{Name}] ffmpeg: {_lastStderr}");
                    }
                }
            }
            catch { }
        }

        private static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n;
                try { n = s.Read(buf, off, count - off); }
                catch { return false; }
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        // ------------------------------------------------------------------ 主线程

        /// <summary>主线程每帧调用：刷新状态、看门狗、按退避重连。</summary>
        public void Tick()
        {
            if (!_started) return;

            // fps 统计
            _fpsTimer += Time.unscaledDeltaTime;
            if (_fpsTimer >= 0.5f)
            {
                float frames = _frameId - _countedFramesAtTimer;
                _measuredFps = frames / _fpsTimer;
                _countedFramesAtTimer = _frameId;
                _fpsTimer = 0f;
            }

            if (_alive)
            {
                float now = Time.realtimeSinceStartup;
                long lastMs = _lastFrameTicks == 0 ? 0 : (long)((Stopwatch.GetTimestamp() - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency);
                bool processDead;
                try { processDead = _proc == null || _proc.HasExited; }
                catch { processDead = true; }

                if (_gotFirstFrame && lastMs < FrameStallMs && !processDead)
                {
                    State = RtspChannelState.Playing;
                    StatusMessage = $"{OutputWidth}×{OutputHeight} · {_measuredFps:0} fps";
                    _restartDelay = 1f;
                }
                else if (processDead)
                {
                    StopProcess();
                    ScheduleRestart("进程退出");
                }
                else if (!_gotFirstFrame && now - _startedAt > ConnectTimeoutSec)
                {
                    StopProcess();
                    ScheduleRestart("无画面");
                }
                else if (_gotFirstFrame && lastMs >= FrameStallMs)
                {
                    StopProcess();
                    ScheduleRestart("画面卡住");
                }
                else if (!_gotFirstFrame)
                {
                    State = RtspChannelState.Connecting;
                    StatusMessage = "连接中…";
                }
            }
            else if (Time.realtimeSinceStartup >= _nextRestartAt)
            {
                StartProcess();
            }
        }

        private int _countedFramesAtTimer;

        /// <summary>
        /// 把最新一帧上传到纹理。返回 true 表示这一帧是新的。
        /// 全程在锁内完成：生产者不会在同一刻改写这块内存。
        /// </summary>
        public bool TryApplyLatest(Texture2D target)
        {
            if (target == null) return false;
            if (target.width != OutputWidth || target.height != OutputHeight) return false;

            lock (_sync)
            {
                if (_frameId == _appliedId || _front == null) return false;
                try
                {
                    target.LoadRawTextureData(_front);
                    target.Apply(false, false);
                }
                catch (Exception e)
                {
                    _lastError = e.Message;
                    return false;
                }
                _appliedId = _frameId;
                return true;
            }
        }

        public bool HasFrame => _frameId > 0;

        // ------------------------------------------------------------------ 参数

        private string BuildArguments()
        {
            // scale+pad：把任意分辨率的流装进固定大小的 16:9 画布（4:3 的 cam3 会自动上下留黑），
            // 这样解码侧只输出一种帧尺寸，C# 侧缓冲和纹理尺寸恒定，避免尺寸变化导致的错帧。
            string vf = $"scale={OutputWidth}:{OutputHeight}:force_original_aspect_ratio=decrease," +
                        $"pad={OutputWidth}:{OutputHeight}:(ow-iw)/2:(oh-ih)/2,setsar=1";
            if (TargetFps > 0) vf += $",fps={TargetFps}";

            var sb = new StringBuilder();
            sb.Append("-hide_banner -nostdin -loglevel warning ");
            // 低延迟 RTSP 参数：不做多余缓冲、TCP 传输避免 UDP 丢包花屏
            sb.Append("-rtsp_transport tcp -fflags nobuffer -flags low_delay ");
            sb.Append("-probesize 500000 -analyzeduration 0 ");
            sb.Append($"-i \"{Url}\" ");
            sb.Append("-an -sn -dn ");                     // 音频/字幕/数据全部丢弃（音频单独一路进程）
            sb.Append($"-vf \"{vf}\" ");
            sb.Append("-f rawvideo -pix_fmt rgb24 pipe:1");
            return sb.ToString();
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
