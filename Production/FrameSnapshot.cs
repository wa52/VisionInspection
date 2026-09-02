namespace SpeakerVisionInspection.Production;

/// <summary>相机帧的已拷贝像素快照（生产者线程把相机缓冲拷出，供工作线程安全转换）。</summary>
public sealed record FrameSnapshot(byte[] Data, int Width, int Height, uint PixelFormat);
