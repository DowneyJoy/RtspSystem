using System;
using UnityEngine;
using UnityEngine.UI;

namespace RtspSubsystem
{
    /// <summary>
    /// 把一路 <see cref="RtspStreamChannel"/> 的画面贴到 UI 的 RawImage 上。
    /// 每帧只做一次「有新帧就上传」，没有新帧不做任何事。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RtspScreenView : MonoBehaviour
    {
        public RtspStreamChannel Channel { get; private set; }
        public RawImage Target { get; private set; }

        private Texture2D _texture;
        private bool _dirty;

        public event Action<RtspScreenView> FirstFrameApplied;

        public void Bind(RtspStreamChannel channel, RawImage target)
        {
            Channel = channel;
            Target = target;
            _dirty = true;
        }

        private void Update()
        {
            if (Channel == null) return;

            Channel.Tick();
            EnsureTexture();
            if (_texture == null) return;

            if (Channel.TryApplyLatest(_texture) && !_dirtyFlagCleared)
            {
                _dirtyFlagCleared = true;
                FirstFrameApplied?.Invoke(this);
            }
        }

        private bool _dirtyFlagCleared;

        private void EnsureTexture()
        {
            int w = Channel.OutputWidth;
            int h = Channel.OutputHeight;

            if (_texture != null && _texture.width == w && _texture.height == h) return;

            if (_texture != null)
            {
                if (Target != null && Target.texture == _texture) Target.texture = null;
                Destroy(_texture);
                _texture = null;
            }

            _texture = new Texture2D(w, h, TextureFormat.RGB24, false, false)
            {
                name = $"RTSP_{Channel.Name}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            if (Target != null) Target.texture = _texture;
            _dirtyFlagCleared = false;
        }

        private void OnDestroy()
        {
            if (Target != null && Target.texture == _texture) Target.texture = null;
            if (_texture != null) Destroy(_texture);
            _texture = null;
        }
    }
}
