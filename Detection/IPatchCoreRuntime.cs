using System.IO;

namespace SpeakerVisionInspection.Detection;

/// <summary>兼容旧的单模型运行时接口（测试用 Fake 实现）。</summary>
public interface IPatchCoreRuntime : IDisposable
{
    double? Threshold { get; }
    (double Score, float[,] PatchMap, string Decision) Detect(OpenCvSharp.Mat bgr);
}
