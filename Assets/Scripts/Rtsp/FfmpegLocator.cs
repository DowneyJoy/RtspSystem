using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RtspSubsystem
{
    /// <summary>
    /// 定位 ffmpeg 可执行文件：优先项目内 StreamingAssets/ffmpeg/ffmpeg.exe（发布时随包带上），
    /// 其次环境变量 FFMPEG_PATH，再次本机已装路径，最后扫 PATH。
    /// </summary>
    public static class FfmpegLocator
    {
        private static string _cached;

        public static string Resolve(string overridePath = null)
        {
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            {
                _cached = overridePath;
                return _cached;
            }
            if (!string.IsNullOrEmpty(_cached) && File.Exists(_cached)) return _cached;

            bool win = Application.platform == RuntimePlatform.WindowsEditor ||
                       Application.platform == RuntimePlatform.WindowsPlayer;
            string exe = win ? "ffmpeg.exe" : "ffmpeg";

            var candidates = new List<string>
            {
                Path.Combine(Application.streamingAssetsPath, "ffmpeg/" + exe),
                Path.Combine(Application.streamingAssetsPath, "ffmpeg/" + (win ? "bin/" : "") + exe),
                Path.Combine(Application.streamingAssetsPath, exe),
            };

            string env = Environment.GetEnvironmentVariable("FFMPEG_PATH");
            if (!string.IsNullOrEmpty(env)) candidates.Add(env);

            if (win)
            {
                candidates.Add(@"F:\DowneyTools\ffmpeg\bin\ffmpeg.exe");
                candidates.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
            }

            foreach (var c in candidates)
            {
                try
                {
                    if (!string.IsNullOrEmpty(c) && File.Exists(c))
                    {
                        _cached = c;
                        return _cached;
                    }
                }
                catch { /* 路径非法直接跳过 */ }
            }

            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                try
                {
                    string d = dir.Trim();
                    if (d.Length == 0) continue;
                    string p = Path.Combine(d, exe);
                    if (File.Exists(p))
                    {
                        _cached = p;
                        return _cached;
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
