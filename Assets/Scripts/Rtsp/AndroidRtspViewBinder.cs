using UnityEngine;

namespace RtspSubsystem
{
    /// <summary>
    /// 挂在 UI 的"视频区域"上，每帧把该 RectTransform 的屏幕像素矩形同步给 Android 原生视图，
    /// 这样窗口缩放、点击放大、分辨率变化时原生画面始终与 Unity 布局严丝合缝。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AndroidRtspViewBinder : MonoBehaviour
    {
        private AndroidRtspView _view;
        private RectTransform _rect;
        private readonly Vector3[] _corners = new Vector3[4];
        private Rect _last = new Rect(-1, -1, -1, -1);

        public void Bind(AndroidRtspView view)
        {
            _view = view;
            _rect = GetComponent<RectTransform>();
            _last = new Rect(-1, -1, -1, -1);
            Sync(true);
        }

        private void Awake()
        {
            _rect = GetComponent<RectTransform>();
        }

        private void LateUpdate()
        {
            Sync(false);
        }

        private void OnDisable()
        {
            _view?.SetVisible(false);
        }

        private void OnEnable()
        {
            Sync(true);
        }

        private void Sync(bool force)
        {
            if (_view == null || _rect == null) return;

            // Screen Space Overlay 画布下，RectTransform 的世界坐标就是屏幕像素（左下原点）
            _rect.GetWorldCorners(_corners);
            float left = _corners[0].x;
            float right = _corners[2].x;
            float bottom = _corners[0].y;
            float top = _corners[1].y;

            float width = right - left;
            float height = top - bottom;
            if (width < 1f || height < 1f) return;

            // 转成 Android 视图坐标：原点在左上
            var rect = new Rect(left, Screen.height - top, width, height);

            if (!force && Approximately(rect, _last)) return;
            _last = rect;

            _view.SetVisible(true);
            _view.SetRect(rect.x, rect.y, rect.width, rect.height);
        }

        private static bool Approximately(Rect a, Rect b)
        {
            return Mathf.Abs(a.x - b.x) < 0.6f && Mathf.Abs(a.y - b.y) < 0.6f &&
                   Mathf.Abs(a.width - b.width) < 0.6f && Mathf.Abs(a.height - b.height) < 0.6f;
        }
    }
}
