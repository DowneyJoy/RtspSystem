package com.rtspunity;

import android.app.Activity;
import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.Gravity;
import android.view.SurfaceView;
import android.view.ViewGroup;
import android.widget.FrameLayout;

import com.google.android.exoplayer2.DefaultLoadControl;
import com.google.android.exoplayer2.ExoPlayer;
import com.google.android.exoplayer2.MediaItem;
import com.google.android.exoplayer2.PlaybackException;
import com.google.android.exoplayer2.Player;
import com.google.android.exoplayer2.source.rtsp.RtspMediaSource;
import com.google.android.exoplayer2.video.VideoSize;

/**
 * Android 端 RTSP 播放视图（ExoPlayer + SurfaceView 独立 surface）。
 *
 * 为什么不用 TextureView：Unity 的 Manifest 重映射器会强制把
 * UnityPlayerActivity 的 hardwareAccelerated 写成 false，非硬件加速窗口里
 * TextureView 永远拿不到 SurfaceTexture（isAvailable() 恒为 false），画面全黑。
 * SurfaceView 的 surface 由系统合成器独立管理，不依赖窗口硬件加速，
 * Unity 自己的画面同样是 SurfaceView 渲染的。
 *
 * 画面矩形由 Unity 每帧同步（setRect，屏幕像素）；4:3 等宽比流（如 cam3）
 * 通过重设 SurfaceView 的布局参数做 letterbox，避免拉伸。
 *
 * 依赖：com.google.android.exoplayer:exoplayer-core / exoplayer-rtsp 2.17.1
 */
public final class RtspPlayerView {

    private static final String TAG = "RtspPlayerView";

    private static Handler sMain;
    private static FrameLayout sRoot;

    private final String url;
    private final boolean audioEnabled;

    private FrameLayout box;        // 覆盖层容器（黑色背景）
    private SurfaceView surface;    // 画面（独立 surface，Z 序在 Unity 画面之上）
    private ExoPlayer player;

    private volatile String state = "idle";
    private volatile String lastError = "";

    private float rx, ry, rw, rh;
    private boolean visible = true;

    /** 连接看门狗：握手后长时间停在 connecting/buffering 就自动重连（兜底 RTSP 信令卡死）。 */
    private final Runnable connectWatchdog = new Runnable() {
        @Override public void run() {
            if (player == null || box == null) return;
            if ("connecting".equals(state) || "buffering".equals(state)) {
                Log.w(TAG, "连接超时，自动重连 url=" + url + " state=" + state);
                restart();
            }
        }
    };

    private void armWatchdog() {
        if (box == null) return;
        box.removeCallbacks(connectWatchdog);
        box.postDelayed(connectWatchdog, 25000);
    }

    private void disarmWatchdog() {
        if (box != null) box.removeCallbacks(connectWatchdog);
    }

    public RtspPlayerView(String url, boolean audioEnabled) {
        this.url = url;
        this.audioEnabled = audioEnabled;
        runOnUi(() -> init());
    }

    // ------------------------------------------------------------------ 初始化

    private static void ensureHandler() {
        if (sMain == null) sMain = new Handler(Looper.getMainLooper());
    }

    private static void runOnUi(Runnable r) {
        ensureHandler();
        if (Looper.myLooper() == Looper.getMainLooper()) r.run();
        else sMain.post(r);
    }

    private static Context activityContext() {
        // 反射拿 Unity 的当前 Activity，避免编译期依赖 UnityPlayer
        try {
            Class<?> cls = Class.forName("com.unity3d.player.UnityPlayer");
            Object activity = cls.getField("currentActivity").get(null);
            if (activity instanceof Context) return (Context) activity;
        } catch (Throwable t) {
            Log.e(TAG, "无法获取 Unity Activity: " + t);
        }
        return null;
    }

    private static FrameLayout rootView() {
        if (sRoot != null && sRoot.isAttachedToWindow()) return sRoot;
        Context ctx = activityContext();
        if (!(ctx instanceof Activity)) return null;
        ViewGroup content = ((Activity) ctx).findViewById(android.R.id.content);
        if (content == null) return null;
        sRoot = new FrameLayout(ctx);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
        sRoot.setLayoutParams(lp);
        // 覆盖层本身不拦截触摸，点击仍然交给 Unity 的 UI
        sRoot.setClickable(false);
        sRoot.setFocusable(false);
        content.addView(sRoot);
        sRoot.bringToFront();
        return sRoot;
    }

    private void init() {
        Context ctx = activityContext();
        FrameLayout root = rootView();
        if (ctx == null || root == null) {
            state = "error";
            lastError = "no-root";
            Log.e(TAG, "初始化失败：拿不到 Unity Activity/root view");
            return;
        }

        box = new FrameLayout(ctx);
        box.setBackgroundColor(0xFF000000);
        box.setClickable(false);
        box.setFocusable(false);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(
                Math.max(1, (int) rw), Math.max(1, (int) rh), Gravity.TOP | Gravity.LEFT);
        lp.leftMargin = (int) rx;
        lp.topMargin = (int) ry;
        box.setLayoutParams(lp);

        surface = new SurfaceView(ctx);
        surface.setClickable(false);
        surface.setFocusable(false);
        // 视频独立 surface 合成在 Unity 主 surface 之上（但仍在窗口 UI 事件之下）
        surface.setZOrderMediaOverlay(true);
        box.addView(surface, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        // 容器尺寸变化（点击放大/旋转/同步矩形）时重算 letterbox
        box.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, orr, ob) -> applyLetterbox());

        root.addView(box);
        root.bringToFront();

        try {
            player = new ExoPlayer.Builder(ctx)
                    .setLoadControl(new DefaultLoadControl.Builder()
                            .setBufferDurationsMs(1200, 6000, 300, 800)
                            .build())
                    .build();

            player.setVideoSurfaceView(surface);
            player.setVolume(audioEnabled ? 1f : 0f);
            // RTSP 内部事件诊断日志（logcat tag: EventLogger）
            player.addAnalyticsListener(new com.google.android.exoplayer2.util.EventLogger(
                    (com.google.android.exoplayer2.trackselection.MappingTrackSelector) null));
            player.addListener(new Player.Listener() {
                @Override
                public void onPlaybackStateChanged(int playbackState) {
                    switch (playbackState) {
                        case Player.STATE_READY:
                            state = "playing";
                            disarmWatchdog();
                            break;
                        case Player.STATE_BUFFERING: state = "buffering"; break;
                        case Player.STATE_ENDED:
                            state = "ended";
                            armWatchdog();   // 流结束自动重连
                            break;
                        case Player.STATE_IDLE:
                            state = "idle";
                            armWatchdog();
                            break;
                        default: state = "unknown"; break;
                    }
                }

                @Override
                public void onPlayerError(PlaybackException error) {
                    state = "error";
                    lastError = error.getErrorCodeName();
                    Log.e(TAG, "播放错误 " + lastError + " url=" + url,
                            error.getCause() != null ? error.getCause() : error);
                }

                @Override
                public void onVideoSizeChanged(VideoSize size) {
                    applyLetterbox();
                    Log.i(TAG, "videoSize " + size.width + "x" + size.height + " url=" + url);
                }

                @Override
                public void onRenderedFirstFrame() {
                    Log.i(TAG, "首帧已渲染 url=" + url);
                }
            });

            RtspMediaSource.Factory factory = new RtspMediaSource.Factory()
                    .setForceUseRtpTcp(true)   // 与 ffmpeg -rtsp_transport tcp 保持一致，避免 UDP 丢包花屏
                    .setTimeoutMs(8000);
            MediaItem item = MediaItem.fromUri(url);
            player.setMediaSource(factory.createMediaSource(item));
            player.prepare();
            player.setPlayWhenReady(true);

            state = "connecting";
            Log.i(TAG, "开始播放 " + url + " audio=" + audioEnabled);
            armWatchdog();
        } catch (Throwable t) {
            state = "error";
            lastError = String.valueOf(t.getMessage());
            Log.e(TAG, "创建 ExoPlayer 失败: " + t);
        }

        applyVisibility();
        applyLetterbox();

        // 诊断：3 秒后输出覆盖层实际状态
        if (box != null) {
            box.postDelayed(() -> {
                if (box == null) return;
                boolean surfOk = surface != null && surface.getHolder() != null
                        && surface.getHolder().getSurface() != null
                        && surface.getHolder().getSurface().isValid();
                Log.i(TAG, "诊断 box=" + box.getWidth() + "x" + box.getHeight()
                        + " vis=" + (box.getVisibility() == android.view.View.VISIBLE)
                        + " attached=" + box.isAttachedToWindow()
                        + " surfOk=" + surfOk
                        + " surfSize=" + (surface == null ? "-" : surface.getWidth() + "x" + surface.getHeight())
                        + " url=" + url);
            }, 3000);
        }
    }

    // ------------------------------------------------------------------ Unity 调用

    /** 更新画面矩形（单位：像素，原点左上角，已是屏幕像素）。 */
    public void setRect(float left, float top, float width, float height) {
        rx = left;
        ry = top;
        rw = width;
        rh = height;
        runOnUi(() -> {
            if (box == null) return;
            FrameLayout.LayoutParams lp = (FrameLayout.LayoutParams) box.getLayoutParams();
            lp.width = Math.max(1, (int) rw);
            lp.height = Math.max(1, (int) rh);
            lp.leftMargin = (int) rx;
            lp.topMargin = (int) ry;
            box.setLayoutParams(lp);
            applyLetterbox();
        });
    }

    public void setVisible(boolean v) {
        visible = v;
        runOnUi(this::applyVisibility);
    }

    private void applyVisibility() {
        if (box == null) return;
        box.setVisibility(visible ? android.view.View.VISIBLE : android.view.View.GONE);
    }

    public String getState() {
        if ("error".equals(state) && lastError != null && lastError.length() > 0)
            return "error:" + lastError;
        return state;
    }

    public void restart() {
        runOnUi(() -> {
            if (player == null) {
                init();
                return;
            }
            try {
                state = "connecting";
                lastError = "";
                player.seekToDefaultPosition();
                player.prepare();
                player.setPlayWhenReady(true);
                armWatchdog();
            } catch (Throwable t) {
                Log.e(TAG, "重连失败: " + t);
            }
        });
    }

    public void setVolume(float v) {
        runOnUi(() -> {
            if (player != null) player.setVolume(Math.max(0f, Math.min(1f, v)));
        });
    }

    public void release() {
        runOnUi(() -> {
            disarmWatchdog();
            try {
                if (player != null) {
                    player.stop();
                    player.release();
                    player = null;
                }
            } catch (Throwable t) {
                Log.w(TAG, "release player: " + t.getMessage());
            }
            try {
                if (box != null && box.getParent() instanceof ViewGroup) {
                    ((ViewGroup) box.getParent()).removeView(box);
                }
            } catch (Throwable t) {
                Log.w(TAG, "release view: " + t.getMessage());
            }
            box = null;
            surface = null;
            state = "released";
        });
    }

    // ------------------------------------------------------------------ 等比缩放

    /** 4:3 的 cam3 在 16:9 窗口里 letterbox：把 SurfaceView 尺寸缩到容器内最大等比矩形并居中。 */
    private void applyLetterbox() {
        if (surface == null || player == null || box == null) return;
        int vw = player.getVideoSize().width;
        int vh = player.getVideoSize().height;
        int bw = box.getWidth();
        int bh = box.getHeight();
        if (vw <= 0 || vh <= 0 || bw <= 0 || bh <= 0) return;

        float scale = Math.min(bw / (float) vw, bh / (float) vh);
        int w = Math.max(1, (int) (vw * scale));
        int h = Math.max(1, (int) (vh * scale));

        ViewGroup.LayoutParams lp = surface.getLayoutParams();
        if (lp == null || lp.width != w || lp.height != h) {
            FrameLayout.LayoutParams flp = new FrameLayout.LayoutParams(w, h, Gravity.CENTER);
            surface.setLayoutParams(flp);
        }
    }
}
