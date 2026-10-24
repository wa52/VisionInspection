# 视频源节点：比赛录像抽帧

适用仓库：`VisionInspection`（OpenCvSharp + .NET 8 WPF），不需要 HALCON。

## 用法

1. 从“添加模块”选择 **视频源（VideoSource）**，将其放到检测流程第一位。
2. 参数“视频文件”选择本地 MP4 / AVI 等 OpenCV 可以解码的录像。
3. 设置“起始帧”（默认0）和“每隔N帧取一帧”（默认1，表示逐帧分析）。
4. 可选填“抽帧保存目录”。为空时不写入图片；填写后每张选中的帧输出为 `<视频名>_frame_00000000.png`。
5. 后续 YOLO 节点的“图像来源”选择“视频源”；将其模型目录配置为 `best.onnx + classes.txt`。
6. 点击“单次运行”读取一帧，或“连续运行”依次处理整段视频。结束时会正常停止，不会循环最后一帧。

## 输出契约

视频源逐轮只输出 **一个完整解码帧**，不额外降采样：

| 结果字段 | 说明 |
|---|---|
| `OutputImage` | 原始大小 BGR Mat，用于下游检测 |
| `frame_index` | 0 起始的视频帧序号 |
| `timestamp_ms` | 时间戳毫秒；若容器不提供则按 FPS 估计，仍无数据为 -1 |
| `timestamp_source` | `container`、`fps_estimate` 或 `unknown` |
| `fps` | 元数据帧率，无法获得为0 |
| `out_w` / `out_h` | 原始视频帧尺寸 |
| `current_file` | 文件名和帧号 |
| `eof` | `false` 或 `true` |

当 `eof=true` 时不输出图像，`Pipeline` 跳过下游，`PipelineRunner` 自动停止连续模式。视频源负责抽帧，不承担目标识别或持久化统计；后续可将 **多目标跟踪** 节点放在 YOLO 检测节点后面。

抽帧规则：从 `start_frame` 开始，依次处理 `start_frame + k * frame_step`；使用 OpenCV 顺序解码/Grab 跳过帧，非重新采样为固定时钟间隔。修改视频文件、起始帧或间隔会关闭旧解码器，下次执行从新设置开始。重复分析可能覆盖输出目录中相同帧序号的抽帧文件；请使用独立目录保存不同实验。

## 边界

- 支持与否取决于运行机器 OpenCV 视频编解码器，不能保证所有 H.265、异常封装或网络直播流可读。
- 固定机位全场球很小；该节点不解决足球检测准确率，后续需要使用高清分块检测和针对球的专用模型。
- 暂不做实时速度同步、直播 RTSP、音轨、VFR 精确时间轴及 GPU 硬件解码。推理速度随下游模型/计算机而变化。
- 不改变旧“图像源”节点、海康相机采集节点或软件名称。

## 测试

Windows x64 + .NET 8 + OpenCV native runtime：

```powershell
dotnet test .\VisionInspection.Tests\VisionInspection.Tests.csproj --filter FullyQualifiedName~VideoSourceNodeTests
```

测试生成临时 MJPEG/AVI，检查原分辨率、抽帧帧号、保存、参数重置、EOS 与连续执行停止。**提交代码不等于实际完成了本机视频解码测试**；除非有真实 Windows 测试结果，不应声称硬件/真实比赛 MP4 已验收。
