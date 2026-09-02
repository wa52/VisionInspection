# SpeakerVisionInspection

WPF (`net8.0-windows` + x64) 扬声器产线视觉检测平台：Recipe 方案驱动的节点式检测流水线 + 海康相机硬触发 → 检测 → OK/NG → PLC/IO 闭环。UI 全中文、深色主题（`Theme/Dark.xaml`）；全部交互写在 `MainWindow.xaml.cs`（~4100 行 code-behind，无 designer 拆分）——改 UI 从这里入手，控件改动要保留中文文案与既有 AutomationId 习惯。

## Commands

- `dotnet build .\SpeakerVisionInspection.csproj`（强制 x64；构建不需要任何环境变量）
- `dotnet test .\SpeakerVisionInspection.Tests\SpeakerVisionInspection.Tests.csproj`（xunit，~15s，全离线可跑，无真实相机依赖）
- 单测过滤：`dotnet test ... --filter "FullyQualifiedName~YoloNodeTests"`；分类过滤 Performance/Stability 用 `[Trait("Category", ...)]` 标注，如 `dotnet test --filter "Category=Performance"`
- 运行：构建后直接启动 `bin\Debug\net8.0-windows\SpeakerVisionInspection.exe`（或仓库根的 `启动视觉检测平台.lnk`）；启动即自检并加载 exe 同目录 `recipe.json`
- 未配置任何 lint/格式化工具（无 .editorconfig/analyzer 配置），依赖 csproj 的 `Nullable`/`ImplicitUsings` 与既有代码风格

## 依赖兄弟项目 HikCameraManager

- csproj 以 `Exists(...)` 条件引用 `..\HikCameraManager\sdk\managed\MvCameraControl.Net.dll`（缺失则编译失败，无回退），并把 `..\HikCameraManager\sdk\native\**` 原生运行时复制进输出目录。移动/删除 HikCameraManager 会破坏本项目构建。
- `Camera/`、`Trigger/`、`Plc/` 是从 HikCameraManager 拷来的 vendored 副本（仅 namespace 不同；`TriggerStateMachine` 额外多了 `ProcessingError`）。不要"重构"成项目引用；修 bug 时先判断是否需要镜像回 HikCameraManager。

## 配置加载（启动即自检）

- 启动时 `EnvironmentCheckService` 自检：x64 进程、VisionMaster 根、MVDAlgorithmSDK 根、MVS SDK dev 根、`vServerApp.exe`、相机托管/原生 DLL，缺失会给出中文可操作提示。
- SDK 路径解析（`VisionMasterConfigResolver`）：与 exe 同目录的 `appsettings.json`（复制自 `appsettings.example.json`，允许 JSON 注释）优先于环境变量 `VISIONMASTER_SDK_ROOT` / `MVDALGO_DEV_ENV` / `MVS_RUNTIME_DIR` / `MVS_SDK_DEV_ROOT`；SDK 根与 VM 根可互相推导（SDK 根 = VM 根 + `\MVDAlgorithmSDK`）。相机原生运行时未配置时回退到应用目录内置副本。
- 方案持久化为 exe 同目录 `recipe.json`（自动加载/保存上次方案）；旧 `config.json` 首次运行自动迁移。产线脚本依赖 `DEPLOY_INPUT_DIR` / `DEPLOY_RESULT_DIR` / `DEPLOY_VM_HOST` / `DEPLOY_VM_PORT` / `DEPLOY_MODEL_DIR` 环境变量覆盖（`DEPLOY_MODEL_DIR` 覆盖第一个 PatchCore 节点）——不要动 `RecipeStore.ApplyEnvOverrides`。跑测试前确保这些环境变量未设置（`RecipeStoreTests` 假定干净状态）。

## 节点流水线（Detection/）

- 工具节点（Binarize/Geometry/**ColorTransform 颜色变换**/SaveImage/Display）不参与判定恒 OK；ColorTransform 转灰度（方式：加权平均[默认·OpenCV BGR2GRAY 同款]/算术平均[32F 核防饱和]/R/G/B 分量提取），输出单通道灰度图可直接供轮廓匹配/YOLO/Seg 消费（下游自带通道归一）。

- 新增检测模型 = 实现 `IModelNode` + 在 `NodeFactory` 静态构造器 `Register` + 在 `MainWindow.NodeTypeLabels` 加中文名 + 在 `MainWindow.ShowParamDefsInspector` 的类型 switch 加 case（漏加则参数面板显示"暂无参数定义"，无法选模型）+ 让 `Pipeline.TryLoadModel` 覆盖新节点（漏加则模型永不加载）+ `Pipeline` 构造器 switch 注入 `Log`。MainWindow 另有四处按模型类型过滤易漏：`FindDisplayedModelNode`（ROI 叠加跟随）、ROI 编辑面板条件、`HotApplyParam` 的 model_dir 标脏、`ComputeCropRoiMismatch`（切图/ROI 失配校验，sidecar 需含 `roi_name`）。未注册类型在"添加模块"里可见但运行时记 ERROR 不崩溃（`UnsupportedNode`，OCR 是占位）；旧类型 `ImageLoad` 自动映射为 `ImageSource`。
- 模型目录契约：PatchCore = `model.onnx + memory_bank.bin + backbone_config.json`；默认 `model_dir` 是 `..\..\..\..\patchcore train\models\foam_patchcore`（相对 exe 上溯 4 级到 speaker-inspection 根；`patchcore train/` 被 gitignore）。YOLO = `best.onnx + classes.txt`（`u训练` 导出产物）。Seg（实例分割）= 同 YOLO 契约，`u训练` yolo11*-seg 导出（output0 `[1,4+nc+32,N]` 检测+掩码系数 + output1 proto `[1,32,PH,PW]`；判定模式：缺陷占比≥阈值即NG[默认·`percent`%]/检出即NG/缺失即NG）。ContourMatch（轮廓匹配）= 模板目录 `shape_template.json`（建模弹窗生成：分层边缘方向点集+基准点；参数面板「创建模板（轮廓建模）」进 `ContourTemplateDialog`）；模板目录留空时自动用 方案目录/模板/节点名（海康式随方案管理，用户不必选目录）；保存时同时写 `source_image.png` 存档，弹窗再次打开自动回填模板图/框选区域/基准点/建模参数（滤波Sigma/边缘阈值/金字塔层数）——调参→提取预览→保存即可更新模板，无需从头建模；弹窗支持「擦除点」（半径可调，各金字塔层按原图坐标同步擦除）与撤销/重做（快照深拷贝 50 步）；搜索侧平滑固定用模板存储 Sigma（节点无 sigma 参数，min_contrast 仅作用于搜索侧）；输出基准点位姿 `x/y/angle/score`，判定模式 匹配成功即OK[默认]/匹配成功即NG；算法核在 `Detection/ShapeMatch/`（纯逻辑，合成图位姿恢复有单测）。
- ROI 只存在于节点私有集 `own_rois`（**方案级 ROI 库已移除**：工具栏"ROI 库"按钮/继承模式/库引用链全部删除；旧配方里的 roi_mode/rois 参数残留不影响运行）。序列化格式：`name;cx,cy,w,h,angle[;e=..,d=..,t=..,s=..]`（第 3 段=检测项元数据 `RoiMeta`：e=启用 d=判断(空=参与判定/仅观察) t=检测项级阈值 s=存图；缺省=全默认，旧配方零迁移兼容；`ParseOwnFull`/`SerializeOwnFull` 读写，`ParseOwn` 为几何投影）。UI = 参数面板「检测区域 (ROI)」绘制按钮（框挂到本节点；画框前节点须执行过一次——`ToggleRoiDraw` 有先后顺序守卫）+「检测项管理（本节点）」（仅 PatchCore/YOLO/Seg；**轮廓匹配只有绘制，无检测项管理**）。检测项管理弹窗（`RoiManagerDialog`）：列表选中 ↔ 图像叠加层高亮双向同步、允许重名（全部按索引定位）、「重绘选中项」、「设为总范围」、以及检测项参数编辑（启用/判断/阈值/存图）。
- **检测项级参数（节点级总阈值已移除）**：YOLO `conf`、Seg `percent`、PatchCore `threshold` 参数行已删——阈值下沉到每个检测项（`RoiMeta.Threshold`，UI 新建时预填类型默认：YOLO/Seg 0.5/PatchCore 模型训练阈值；缺省回退同款出厂默认）。YOLO 停用项不检测（roi_名=停用）、仅观察项检出记 `det_all` 但不进 triggered（不判 NG）、存图按检测项过滤；Seg 停用项占比=-1（输出停用）、判定=任一启用且参与判定项 占比≥该项阈值（`ComputeDecision(mode, roiChecks, triggeredCount)`）；PatchCore 同理（观察项 Decision="观察" 只记分，切图按检测项过滤）。
- **总范围**（`scope_index` 隐藏参数，检测项管理「设为总范围」设置）：其余检测项结果只保留落在该 ROI 内的部分——YOLO 检出中心点判定（`YoloNode.ApplyScopeFilter` 纯函数）、Seg 缺陷掩码 AND 总范围+实例中心过滤；删除总范围项时索引自动取消/平移。
- **位置修正**（`PositionCorrectionNode`，工具节点，海康式面板 `ShowPositionCorrectionEditor` **全引用化**）：读取定位节点的 `loc_x/loc_y/loc_angle/loc_valid` **标准契约键**（轮廓匹配已输出；后续新增定位手段输出同键即可接入，无需改位置修正）。定位来源=上游轮廓匹配**下拉引用**、修正目标=下游检测节点**勾选引用**（写入 target_nodes，不许手输名字）；选择方式（按点/按坐标）仅切换引用行展示；X/Y方向尺度 `scale_x/scale_y`（默认1，变换 p′=R(Δθ)·S·(p−P0)+P1）；基准位姿 `base_x/base_y/base_angle` 由「创建基准」按钮从定位来源最近一次执行一键采集（VM 同名按钮；base_* 为 hidden 参数不手填）；图像显示 开关（`show_base/show_run`）控制结果图上基准点/运行点十字标注；变换写入 `ctx.PoseCorrection` 后目标节点经 `NodeRois.ApplyPoseCorrection` 跟随工件平移+旋转；定位失败（loc_valid≠1）→ 节点 ERROR 停线防误检；未设基准 → 透传不修正；Pipeline 构建时校验 target 须在下游/定位源须在上游（日志警告）；参数面板叠加层对修正目标节点额外画**青色虚线预览框**（`BuildPreviewCorrection` 按最近一次定位模拟；非绘制模式只显示修正后框，选中项才显示原始框+手柄）。
- **ROI 叠加层交互**（防误触）：非绘制模式也可编辑——点框第一下=只选中（高亮+手柄切换），已选中的框再按住才拖拽/缩放/旋转；点空白=取消选中；画新框仍需绘制模式；右键删除。
- `PipelineResult.NodeImages` / `DisplayImage` 的 `Mat` 所有权移交调用方，UI 侧负责释放（沿用 OpenCvSharp 严格 dispose 约定）。`DisplayImage` 优先指向最后一个检测节点的图像（多图像源流程里排在检测节点之后的图像源不会盖掉检测结果，无检测节点图像时回退最后一个有图节点）；ROI 叠加只在当前选中的是模型节点时显示该节点自己的/引用的 ROI，非模型节点（如图像源）不显示任何 ROI——不要恢复"兜底显示全部方案 ROI"的旧行为（会跨节点混淆）。
- 检测结果的框线/文字标注走 `NodeShape` 矢量叠加层（`NodeResult.Annotations` → `PipelineResult.NodeAnnotations` → MainWindow 按 UniformFit 映射渲染，线宽/字号屏幕常量）——**不要把框线/文字用 PutText/Rectangle 画进位图**（位图随显示缩放会变粗/发糊，图像分辨率差异大时无法兼顾）；位图只保留像素级内容（SegNode 掩码染色、PatchCore 热图）。

## 生产闭环（Production/）

- `CameraInspectionService`：硬触发帧 → 有界队列（容量 1，`DropWrite`，溢出计入 `TriggerStateMachine.OverrunCount`，不无限排队）→ 流水线检测 → OK/NG → PLC 状态语义（Ready→Busy→OK/NG，Error 可 `Reset` 恢复）+ NG 时相机 IO 脉冲。帧缓冲在相机事件线程内同步拷贝后立即释放——不要把 SDK 回调改成异步持有 `Mat`。

## 测试注意

- `RealModelTests` 在模型文件缺失时静默 `return`：绿色不证明数值一致性；要在真机模型上复跑。
- 相机/PLC 均为假实现注入（`CameraIoNodeTests`、`ImageSourceNode` 相机分支），不需要真实硬件。

## Git

- 整个项目目录尚未提交（untracked），且还没有本项目自己的 `.gitignore`——首次提交前需忽略 `bin/ obj/ results/`（检测结果 JSON 落在 `results/`；日志在 bin 输出目录 `logs/`，随 bin 一起被忽略）。`启动视觉检测平台.lnk` 由父级 `.gitignore` 的 `*.lnk` 覆盖。
- git 仓库根在 `D:\AiProjects`（伞形工作区），`git add` 只加 `speaker-inspection/` 下的显式路径。
