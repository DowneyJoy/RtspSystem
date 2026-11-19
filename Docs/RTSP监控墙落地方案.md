# RTSP 监控墙落地方案（Unity 桌面端 + Android 端）

> 项目：RtspSubsystem（Unity 2022.3.62f2c1）
> 状态：桌面端与 Android 真机均已验证通过（4/4 路在线）
> 更新日期：2026-11-19

---

## 1. 需求与测试环境

### 1.1 目标

在 Unity 中同时显示 4 路 RTSP 监控画面（2×2 监控墙），支持点击放大、状态指示、断流自动重连，并可发布到 Windows 桌面与 Android 真机。

### 1.2 测试流（MediaMTX @ 127.0.0.1:8554）

| 通道 | 分辨率 | 内容 | 音频 |
|---|---|---|---|
| cam1 | 1280×720@25 | 彩条 testsrc | 无 |
| cam2 | 1280×720@25 | 渐变噪点 | 无 |
| cam3 | 640×480 | SMPTE 彩条 | 无 |
| cam4 | 1280×720@25 | 彩条 | AAC 44.1kHz 单声道（440Hz 正弦） |

- 视频编码均为 H.264；服务端 MediaMTX 开放 RTSP(8554) 与 WebRTC(8889)。
- MediaMTX 日志：`C:/Users/dh/Doubao/chats/2026-11-19/new-chat/RTSP测试/logs/mediamtx.log`

### 1.3 方案选型结论

| 方案 | 延迟 | 依赖 | 结论 |
|---|---|---|---|
| Unity VideoPlayer 直连 RTSP | — | 无 | ❌ Windows/Android 均不支持 RTSP |
| **ffmpeg 子进程 → 管道 → Texture2D** | 150~300ms | 单个 ffmpeg.exe | ✅ 桌面端采用 |
| **ExoPlayer 原生 RTSP（Java 插件）** | 0.3~1s | Gradle 拉包 | ✅ Android 端采用 |
| MediaMTX WebRTC (WHEP) + com.unity.webrtc | <100ms | Unity 预览包 | 备选（二期低延迟方向，AAC 不支持） |
| libav 原生插件 P/Invoke | 最低 | ffmpeg DLL | 备选（性能最优但开发量大） |

---

## 2. 总体架构

```
┌─────────────────────── 桌面端 (Windows) ───────────────────────┐
│  MediaMTX ─RTSP─► ffmpeg.exe 子进程（每路 1 个，TCP 拉流）        │
│                     │ rawvideo rgb24 管道 (stdout)              │
│                     ▼                                          │
│  RtspStreamChannel：双缓冲 + 看门狗 + 指数退避重连               │
│                     │                                          │
│                     ▼                                          │
│  RtspScreenView → RawImage(Texture2D) → RtspWallApp UI          │
│  音频：RtspAudioChannel（独立 ffmpeg 管道）→ StreamAudioPlayer   │
│         → OnAudioFilterRead 混音输出                             │
└────────────────────────────────────────────────────────────────┘

┌─────────────────────── Android 端 ─────────────────────────────┐
│  MediaMTX ─RTSP/TCP─► ExoPlayer 2.18.7 (Java, exoplayer-rtsp)   │
│                     │ MediaCodec 硬解 H.264 + AAC               │
│                     ▼                                          │
│  RtspPlayerView(SurfaceView 独立 surface) 叠加在 Unity 之上      │
│  Unity：AndroidRtspView(JNI 封装) + AndroidRtspViewBinder       │
│         （每帧同步视频区域矩形 → 原生视图跟随布局/点击放大）       │
│  Unity 只绘制：边框、标签、状态灯、提示栏                         │
└────────────────────────────────────────────────────────────────┘
```

**通道配置**：`Assets/StreamingAssets/rtsp_channels.json`（改地址不用动代码）：

```json
{
  "channels": [
    { "name": "CAM1 · 彩条 1280×720",      "url": "rtsp://127.0.0.1:8554/cam1" },
    { "name": "CAM2 · 渐变噪点 1280×720",  "url": "rtsp://127.0.0.1:8554/cam2" },
    { "name": "CAM3 · SMPTE 彩条 640×480", "url": "rtsp://127.0.0.1:8554/cam3" },
    { "name": "CAM4 · 彩条 + 440Hz AAC",   "url": "rtsp://127.0.0.1:8554/cam4", "audio": true }
  ],
  "androidHost": "192.168.0.71"
}
```

Android 上自动把 `127.0.0.1` 替换为 `androidHost`（手机与电脑须同局域网）。

---

## 3. 桌面端实现（ffmpeg 子进程管道）

### 3.1 代码清单（`Assets/Scripts/Rtsp/`）

| 文件 | 职责 |
|---|---|
| `FfmpegLocator.cs` | 自动定位 ffmpeg.exe（环境变量 FFMPEG_PATH → StreamingAssets → 常见路径） |
| `RtspStreamChannel.cs` | 每路一个 ffmpeg 子进程，stdout rgb24 管道 → 双缓冲 Texture2D；看门狗 + 指数退避重连 |
| `RtspScreenView.cs` | 通道 → RawImage 绑定，首帧回调 |
| `RtspAudioChannel.cs` + `StreamAudioPlayer` | cam4 AAC 音频独立解码管道 → 环形缓冲 → `OnAudioFilterRead` 混音 |
| `RtspWallApp.cs` | 纯代码 UI：中文标题、状态灯、点击放大、Esc 还原、R 重连 |
| `RtspWallBootstrap.cs` | 进 Play 自动创建监控墙，无需改场景 |

### 3.2 关键设计

- **小窗解码 960×540** 省 CPU；点击放大自动切 1920×1080 重新拉流。
- **管道协议**：`ffmpeg -rtsp_transport tcp -i <url> -an -f rawvideo -pix_fmt rgb24 -`
- **崩溃隔离**：ffmpeg 子进程崩溃不影响 Unity 主进程，通道自动重连。
- **cam3 4:3 变形处理**：桌面端自动补边不变形（pillarbox）。
- 实测性能：4 路 26~28fps，0 错误 0 警告。

### 3.3 ffmpeg 依赖

- 本机位置：`F:\DowneyTools\ffmpeg\bin`
- ⚠️ 当前为 gyan essentials（**GPL 构建**），商业发行建议换 BtbN 的 **LGPL 构建**，避免开源义务传染。

---

## 4. Android 端实现（ExoPlayer 原生插件）

### 4.1 代码清单

| 文件 | 职责 |
|---|---|
| `Assets/Plugins/Android/com/rtspunity/RtspPlayerView.java` | ExoPlayer RTSP 播放器 + SurfaceView 覆盖层 + letterbox + 25s 连接看门狗 |
| `Assets/Plugins/Android/mainTemplate.gradle` | 自定义主 Gradle 模板（compileSdk 33，ExoPlayer 2.18.7） |
| `Assets/Plugins/Android/launcherTemplate.gradle` | 自定义 launcher 模板（compileSdk 33，必须同步改） |
| `Assets/Plugins/Android/gradleTemplate.properties` | Gradle 属性模板 |
| `Assets/Plugins/Android/AndroidManifest.xml` | 权限 + 完整启动 Activity 声明 |
| `Assets/Scripts/Rtsp/AndroidRtspView.cs` | JNI 封装（反射取 currentActivity，创建/控制原生视图） |
| `Assets/Scripts/Rtsp/AndroidRtspViewBinder.cs` | 每帧把 Video RectTransform 世界角点换算屏幕像素同步给原生视图 |
| `Assets/Editor/AndroidBuildConfig.cs` | 菜单 `Tools/RtspSubsystem/Configure Android Player` 一键配置打包参数 |

### 4.2 为什么用 SurfaceView 覆盖层（而不是 SurfaceTexture / TextureView）

1. **SurfaceTexture 挂 Unity 纹理不可行**：`GL_TEXTURE_EXTERNAL_OES` + `updateTexImage` 需要 Unity GL 线程配合，外部插件无法安全接入。
2. **TextureView 不可行**：Unity 的 Manifest 重映射器**无条件**把 `UnityPlayerActivity` 的 `hardwareAccelerated` 强制写成 `false`（activity 级覆盖 application 级，显式声明也无效），非硬件加速窗口中 TextureView 永远拿不到 SurfaceTexture（症状：解码器正常解码但 `Render:0, Drop:125`，画面全黑）。
3. **SurfaceView** 用独立 surface 合成，不依赖窗口硬件加速（Unity 自己的画面就是 SurfaceView），`setZOrderMediaOverlay(true)` 叠加，letterbox 用布局参数实现。

### 4.3 关键设计

- ExoPlayer **强制 RTP over TCP**（与桌面 `-rtsp_transport tcp` 一致）。
- H.264 MediaCodec 硬解 + AAC 音频（cam4）。
- **25 秒连接看门狗**：connecting/buffering 超时、STATE_ENDED/IDLE 自动重连。
- 布局跟随：Unity 每帧同步矩形 → 点击放大、分辨率变化、cam3 4:3 letterbox 自动正确。
- 状态与错误上屏（`ProbeError` 显示在 UI），无需 logcat 即可定位。

### 4.4 打包参数（`Tools/RtspSubsystem/Configure Android Player`）

| 项 | 值 |
|---|---|
| Scripting Backend | IL2CPP（Mono 不支持 ARM64） |
| Target Architectures | ARMv7 + ARM64 |
| Min / Target SDK | 24 / 32（compileSdk 33） |
| 包名 | com.downey.rtspwall |
| 方向 | 横屏锁定 |
| 权限 | INTERNET（Manifest 含 `usesCleartextTraffic=true` 支持 rtsp:// 明文） |

产物：`Build/RtspWall.apk`（约 25MB，已校验 dex 含 RtspPlayerView + ExoPlayer RTSP 类、launchable-activity 正常）。

---

## 5. 真机调试踩坑记录（重要）

按实际排障顺序，每一条都是独立踩过的坑：

1. **桌面无启动图标**：自定义 Manifest 只写权限不写 Activity → Unity "写什么合并什么"，不会自动补 launcher。修复：补全 `UnityPlayerActivity` 的 MAIN/LAUNCHER intent-filter。
2. **配置 JSON 读不到**：Android 上 StreamingAssets 是 jar: 路径，`File.ReadAllText` 失效，回退默认 127.0.0.1 → 四路 ECONNREFUSED。修复：改 `UnityWebRequest` 协程读取。
3. **画面全黑（texReady=false）**：见 4.2，Unity 强制 `hardwareAccelerated=false` → 换 SurfaceView。
4. **cam3 永远 connecting（最深的坑）**：
   - 排查链：单路隔离 + ExoPlayer EventLogger → 解码器初始化成功但**零输入** → 服务端推流正常（ffprobe 有帧）→ 抓 interleaved RTP 原始包对比。
   - **根因**：cam3 发布端 ffmpeg `-tune zerolatency` 开启 x264 sliced-threads，640×480 小帧被切成 30 个 7 字节 slice，且**每个 RTP 包 marker=1**；ExoPlayer 严格按 marker 切帧 → 解码器收到的都是不完整 slice → 零输出。ffmpeg/VLC 会攒齐分片所以显示正常。
   - **修复**：cam3 发布端加 `-threads 1 -x264-params sliced-threads=0`（每帧单包）。cam1/2/4 帧大走 FU-A 聚合侥幸正常。
5. **ExoPlayer 2.18.7 要求 compileSdk≥33**：需安装 SDK platform android-33；`mainTemplate.gradle` 和 `launcherTemplate.gradle` **都要**固定 compileSdkVersion 33（错误出现在 `:launcher:checkReleaseAarMetadata`）。
6. **PlayerSettings.SetGraphicsAPIs 会弹确认框**，把 UnitySkills bridge 卡死在 reloading → 重启编辑器恢复；Gradle 模板序列化字段名是 `useCustomMainGradleTemplate`（不是 custom*）。
7. **EventLogger 构造歧义**：`new EventLogger(null)` 有 String/MappingTrackSelector 两个重载，需显式 `(MappingTrackSelector)` cast。

---

## 6. 验证结果

| 平台 | 结果 |
|---|---|
| Windows 编辑器 Play | 4/4 路 26~28fps，0 错误 0 警告，中文 UI 正常 |
| Android 真机（Xiaomi M098FE，Android 11，adb 序列号 b978e9f6） | 4/4 路首帧渲染，cam3 letterbox 正确居中，音频正常 |

---

## 7. 运维与部署要点

### 7.1 cam3 发布流重启命令（电脑重启后需拉起）

```bash
ffmpeg -re -f lavfi -i smptebars=size=640x480:rate=25 \
  -c:v libx264 -preset ultrafast -tune zerolatency \
  -threads 1 -x264-params sliced-threads=0 \
  -g 50 -b:v 800k -pix_fmt yuv420p \
  -f rtsp rtsp://127.0.0.1:8554/cam3
```

⚠️ 对 Android 客户端的任何流，**都建议统一加 `-threads 1 -x264-params sliced-threads=0`**，避免 slice 切分 + marker 问题。桌面端无所谓（ffmpeg 解码器兼容）。

### 7.2 网络要求

- 手机与电脑同一局域网；IP 变化只需改 `rtsp_channels.json` 的 `androidHost` 重新打包。
- 电脑防火墙放行 8554 端口入站。

### 7.3 常用 adb 命令

```bash
adb devices                     # 确认连接
adb install -r Build/RtspWall.apk
adb logcat -s RtspPlayerView    # 只看播放日志（诊断/首帧/错误均在此 tag）
adb shell am start -n com.downey.rtspwall/com.unity3d.player.UnityPlayerActivity
```

adb 已加入用户 PATH（Unity 自带 `F:\...\AndroidPlayer\SDK\platform-tools`），新开终端生效。

### 7.4 交互说明

- 桌面：点击画面放大 / Esc 还原 / R 重连全部；顶部显示“X 路在线”。
- Android：同 UI（Unity 绘制），右上角操作按钮。

---

## 8. 目录与产物索引

```
RtspSubsystem/
├── Assets/Scripts/Rtsp/          # 桌面 ffmpeg 管道 + Android JNI 封装（见 §3.1/§4.1）
├── Assets/Plugins/Android/       # Java 插件 + Gradle 模板 + Manifest
├── Assets/Editor/AndroidBuildConfig.cs
├── Assets/StreamingAssets/rtsp_channels.json
├── Assets/Screenshots/           # rtsp_wall_audio.png 等验证截图
└── Build/RtspWall.apk            # 最终 Android 产物
```

## 9. 后续可选方向

- **WebRTC 低延迟二期**：MediaMTX 8889 已开 WHEP，桌面/Android 接 com.unity.webrtc，延迟可压到 <100ms（音频需转 Opus）。
- **正式签名发布**：keystore 签名、关闭 development build、混淆。
- **多实例动态增减**：按需动态增减通道窗口。
- **开机自启 / 常驻**：Android 盒子场景的 kiosk 模式。
