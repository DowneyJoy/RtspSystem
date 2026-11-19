using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RtspSubsystem
{
    /// <summary>
    /// 单路 RTSP 音频通道：独立 ffmpeg 进程解码音频 → s16le PCM 管道 → 无锁环形缓冲，
    /// 由 <see cref="StreamAudioPlayer"/> 在 Unity 音频线程里取数混音。
    /// 视频与音频各走一路，不做严格同步（监控场景只要求低延迟连贯）。
    /// </summary>
    public sealed class RtspAudioChannel : IDisposable
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;

        public string Name { get; private set; }
        public string Url { get; private set; }
        public bool Running { get; private set; }

        private readonly string _ffmpeg;
        private readonly object _gate = new object();
        private float[] _ring;              // 交错双声道 float
        private int _head;                  // 写位置（生产者）
        private int _tail;                  // 读位置（消费者）
        private int _count;

        private Process _proc;
        private Thread _readThread;
        private volatile bool _alive;
        private bool _started;
        private float _nextRestartAt;
        private float _restartDelay = 1f;
        private int _restartCount;
        private float _startedAt;
        private bool _gotData;
        private string _lastStderr = string.Empty;

        public int RestartCount => _restartCount;

        public RtspAudioChannel(string name, string url, string ffmpegPath)
        {
            Name = name;
            Url = url;
            _ffmpeg = ffmpegPath;
            _ring = new float[SampleRate * Channels]; // 1 秒
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _restartDelay = 1f;
            StartProcess();
        }

        public void Tick()
        {
            if (!_started) return;
            if (_alive)
            {
                float now = Time.realtimeSinceStartup;
                bool dead;
                try { dead = _proc == null || _proc.HasExited; }
                catch { dead = true; }

                if (dead)
                {
                    StopProcess();
                    ScheduleRestart("进程退出");
                }
                else if (!_gotData && now - _startedAt > 15f)
                {
                    StopProcess();
                    ScheduleRestart("无音频");
                }
            }
            else if (Time.realtimeSinceStartup >= _nextRestartAt)
            {
                StartProcess();
            }
        }

        private void StartProcess()
        {
            if (!_started || string.IsNullOrEmpty(_ffmpeg) || !File.Exists(_ffmpeg))
            {
                if (_started) Debug.LogError($"[RTSP-AUDIO:{Name}] ffmpeg 不可用");
                return;
            }

            lock (_gate) { _head = _tail = _count = 0; }
            _gotData = false;
            _startedAt = Time.realtimeSinceStartup;
            _alive = true;
            Running = true;

            try
            {
                string args =
                    "-hide_banner -nostdin -loglevel warning " +
                    "-rtsp_transport tcp -fflags nobuffer -probesize 500000 -analyzeduration 0 " +
                    $"-i \"{Url}\" " +
                    "-vn -sn -dn " +
                    $"-ac {Channels} -ar {SampleRate} " +
                    "-f s16le pipe:1";

                var psi = new ProcessStartInfo(_ffmpeg, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                _proc = new Process { StartInfo = psi };
                _proc.Start();

                _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "rtsp-audio-" + Name };
                _readThread.Start(_proc.StandardOutput.BaseStream);

                var errThread = new Thread(ErrorLoop) { IsBackground = true, Name = "rtsp-audio-log-" + Name };
                errThread.Start(_proc.StandardError);
            }
            catch (Exception e)
            {
                Debug.LogError($"[RTSP-AUDIO:{Name}] 启动失败: {e.Message}");
                StopProcess();
                ScheduleRestart("启动失败");
            }
        }

        private void StopProcess()
        {
            _alive = false;
            Running = false;
            var proc = _proc;
            _proc = null;
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                try { proc.StandardOutput.BaseStream?.Close(); } catch { }
                try { proc.StandardError?.Close(); } catch { }
                try { proc.Dispose(); } catch { }
            }
            var t = _readThread;
            _readThread = null;
            try { t?.Join(200); } catch { }
        }

        private void ScheduleRestart(string reason)
        {
            _restartCount++;
            _nextRestartAt = Time.realtimeSinceStartup + _restartDelay;
            _restartDelay = Mathf.Min(_restartDelay * 1.5f, 8f);
            Debug.LogWarning($"[RTSP-AUDIO:{Name}] {reason}，{_restartDelay:0.0}s 后重连（第 {_restartCount} 次）");
        }

        // ------------------------------------------------------------------ 后台线程

        private void ReadLoop(object arg)
        {
            var stream = (Stream)arg;
            var raw = new byte[4096]; // 1024 帧 × 2ch × 2byte

            while (_alive)
            {
                int got;
                try { got = stream.Read(raw, 0, raw.Length); }
                catch { break; }
                if (got <= 0) break;

                int frames = got / (Channels * 2);
                if (frames <= 0) continue;

                _gotData = true;
                lock (_gate)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        for (int c = 0; c < Channels; c++)
                        {
                            int idx = f * Channels * 2 + c * 2;
                            short s = (short)(raw[idx] | (raw[idx + 1] << 8));
                            // 环满：丢最老的（保持低延迟，不让缓冲越积越大）
                            if (_count >= _ring.Length)
                            {
                                _tail = (_tail + 1) % _ring.Length;
                                _count--;
                            }
                            _ring[_head] = s / 32768f;
                            _head = (_head + 1) % _ring.Length;
                            _count++;
                        }
                    }
                }
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
                    if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                        Debug.LogWarning($"[RTSP-AUDIO:{Name}] ffmpeg: {_lastStderr}");
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ 音频线程

        /// <summary>由 Unity 音频线程调用：取 frames 帧交错双声道数据，不足补零。</summary>
        public int Read(float[] dst, int frames)
        {
            int need = frames * Channels;
            int copied = 0;
            lock (_gate)
            {
                copied = Mathf.Min(need, _count);
                for (int i = 0; i < copied; i++)
                {
                    dst[i] = _ring[_tail];
                    _tail = (_tail + 1) % _ring.Length;
                    _count--;
                }
            }
            for (int i = copied; i < need; i++) dst[i] = 0f;
            return copied / Channels;
        }

        public void Dispose()
        {
            _started = false;
            StopProcess();
        }
    }

    /// <summary>
    /// 挂在带 AudioListener 的对象上（一般是 Main Camera），把各路解码出的 PCM 混进最终输出。
    /// 不需要 AudioSource —— OnAudioFilterRead 挂在 AudioListener 同一物体上时处理主混音。
    /// </summary>
    public sealed class StreamAudioPlayer : MonoBehaviour
    {
        [Range(0f, 1f)] public float volume = 1f;

        private readonly List<RtspAudioChannel> _channels = new List<RtspAudioChannel>();
        private float[] _scratch = new float[4096];

        public void AddChannel(RtspAudioChannel channel)
        {
            if (channel != null && !_channels.Contains(channel)) _channels.Add(channel);
        }

        public void RemoveChannel(RtspAudioChannel channel)
        {
            if (channel != null) _channels.Remove(channel);
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (_channels.Count == 0) return;
            if (volume <= 0.0001f) return;

            int frames = data.Length / channels;
            if (_scratch.Length < frames * RtspAudioChannel.Channels)
                _scratch = new float[frames * RtspAudioChannel.Channels];

            foreach (var ch in _channels)
            {
                if (ch == null || !ch.Running) continue;
                ch.Read(_scratch, frames);
                int srcChannels = RtspAudioChannel.Channels;
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        float v = _scratch[f * srcChannels + Mathf.Min(c, srcChannels - 1)];
                        data[f * channels + c] = Mathf.Clamp(data[f * channels + c] + v * volume, -1f, 1f);
                    }
                }
            }
        }

        private void OnDestroy()
        {
            foreach (var ch in _channels) ch?.Dispose();
            _channels.Clear();
        }
    }
}
