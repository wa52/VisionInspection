# 多目标跟踪节点 V1（基础 IoU 跟踪）

属于 `VisionInspection` 通用视觉节点，与足球比赛指标计算相互独立。

## 节点连接

```text
视频源(VideoSource) → 目标检测(YOLO) → 多目标跟踪(MultiObjectTracking)
```

1. 在 **视频源** 设置视频文件、`frame_step=1`（连续跟踪优先逐帧）。
2. 在 YOLO 节点设置 `model_dir`（`best.onnx` + `classes.txt`），图像来源选 **视频源**，配置覆盖全场的检测区域。
3. 新增 **多目标跟踪** 节点，将“上游目标检测节点”设为 YOLO 节点名称。
4. 连续运行，选择多目标跟踪节点查看 `track_count`、`track_ids` 和跟踪框。

YOLO 节点现在额外输出 `detections_json`，其中是未按工业 OK/NG 筛选的原始检测框列表（模型类别、分数、图像坐标）。这项输出不改变既有缺陷检测判定。

### 跟踪参数

| 参数 | 默认 | 作用 |
|---|---:|---|
| `source` | 必选 | 上游 YOLO 节点名 |
| `min_iou` | 0.15 | 同类别目标框关联门槛 |
| `min_conf` | 0.25 | 最低检测置信度 |
| `max_missed` | 10 | 暂时丢失后保留多少个已处理帧 |

每轮输出：`track_count`、`track_ids`、`tracks_json`、带 ID 标签的矢量框，以及原尺寸输出图像。视频切换或帧号回退会清空旧跟踪状态。

## 验收与边界

这一版实现的是 **按类别 IoU 贪心关联** 的通用多目标跟踪基线，**不是** ByteTrack、BoT-SORT 或 ReID，也不能保证遮挡、快速移动或相似球衣交叉后 Track ID 不变。尤其是足球运动速度快、像素小，需要后续采用专门的关联算法与球轨迹插值。

- 22个不重叠球员合成框连续移动后，Track ID 不发生变化。
- 目标丢失超过设置值后，重新出现分配新 ID。
- 切换/重新播放视频，跟踪 ID 从头开始分配。
- 丢失上游输出时报错，不输出伪造的0目标统计。
- 原工业检测节点保持可用；目前还没有进球、红黄牌、角球、攻势、危险进攻和控球统计节点。

```powershell
dotnet test .\VisionInspection.Tests\VisionInspection.Tests.csproj --filter FullyQualifiedName~MultiObjectTrackingNodeTests
```

这些测试是合成框逻辑测试，不等于真实比赛整段视频的 IDF1/HOTA/MOTA 精度验收。
