# VisionInspection

扬声器产线视觉检测平台，基于 WPF、.NET 8 和海康 MVS 相机 SDK。

## 功能

- Recipe 驱动的节点式检测流程
- 海康相机枚举、连接、预览、软触发和硬触发
- YOLO 目标检测、实例分割、语义分割
- 轮廓匹配、快速匹配、直线查找、圆查找
- Blob 分析、字符识别和几何测量
- ROI 检测项管理、总范围和位置修正
- TCP 客户端、TCP 服务端、UDP、串口通信
- OK、NG、ERROR 状态处理和相机 IO 输出
- 检测结果、ROI 切图和整图保存

## 环境要求

- Windows x64
- .NET 8 SDK
- Visual Studio 2022 或 .NET SDK 命令行
- 海康 MVS Runtime（使用真实相机时）
- 同级目录的 `HikCameraManager` 项目及其 SDK 文件

项目默认引用：

```text
..\HikCameraManager\sdk\managed\MvCameraControl.Net.dll
```

因此推荐保持以下目录结构：

```text
speaker-inspection/
├─ HikCameraManager/
│  └─ sdk/
└─ SpeakerVisionInspection/
```

缺少海康托管 DLL 时，项目无法完成编译。真实运行还需要 MVS 原生运行库；程序启动时会执行环境自检并提示缺失项。

## 构建

在 `SpeakerVisionInspection` 目录执行：

```powershell
dotnet build .\VisionInspection.csproj
```

项目目标框架为 `net8.0-windows`，目标平台为 `x64`。

## 测试

```powershell
dotnet test .\VisionInspection.Tests\VisionInspection.Tests.csproj
```

测试不依赖真实相机，模型文件缺失时真实模型冒烟测试会自动跳过。

## 启动

构建后运行：

```powershell
.\bin\Debug\net8.0-windows\VisionInspection.exe
```

程序启动时会：

1. 检查 x64 进程和相机运行时依赖。
2. 从 exe 同目录加载 `recipe.json`。
3. 读取同目录的 `appsettings.json`（可选）。

## 配置

首次配置可以复制示例文件：

```powershell
Copy-Item .\appsettings.example.json .\bin\Debug\net8.0-windows\appsettings.json
```

支持的配置项：

- `MVS_RUNTIME_DIR`：MVS 原生运行库目录；未设置时使用程序目录内置运行库。
- `DEPLOY_INPUT_DIR`：部署输入目录覆盖路径。
- `DEPLOY_RESULT_DIR`：部署结果目录覆盖路径。
- `DEPLOY_VM_HOST`：VisionMaster 主机地址覆盖值。
- `DEPLOY_VM_PORT`：VisionMaster 端口覆盖值。
- `DEPLOY_MODEL_DIR`：覆盖第一个 PatchCore 节点模型目录。

生产环境中的相机、通信和方案配置会保存到 exe 同目录，包括 `camera.json`、`trigger.json`、`comm.json` 和 `recipe.json`。这些本机配置不应提交到 GitHub。

## 模型目录

- YOLO：`best.onnx` + `classes.txt`
- 实例分割：`best.onnx` + `classes.txt`
- 语义分割：`best.onnx` + `classes.txt`
- PatchCore：`model.onnx` + `memory_bank.bin` + `backbone_config.json`
- 轮廓匹配和快速匹配：`shape_template.json`
- 字符识别：字符目录及归一化样本图片

模型文件通常较大，建议放在项目外部目录，并在方案中配置模型目录，不要提交到 GitHub。

## 保存规则

- 检测节点的 ROI 切图支持：`全部`、`仅OK`、`仅NG`、`不保存`。
- “输出图像”节点负责整图保存，也支持上述四种保存类型。
- 保存目录填写根目录，程序会自动追加 `OK` 或 `NG` 子目录。

例如目录填写：

```text
D:\vision\results
```

实际文件会保存到：

```text
D:\vision\results\OK\...
D:\vision\results\NG\...
```

## 说明

- 真实相机操作需要安装 MVS Runtime 并连接设备。
- PLC 目前使用日志型接缝实现，真实 PLC 协议需要根据现场品牌和协议扩展。
- 生产硬触发模式下，检测流程由相机触发驱动服务执行。
