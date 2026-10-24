using System.Text.Json;
using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;

namespace VisionInspection.Tests;

public sealed class MultiObjectTrackingNodeTests
{
    private static (PipelineRunContext Context, Mat Image) ContextWith(
        IReadOnlyList<YoloDetection> detections, long frame, string video = "match.mp4")
    {
        var image = new Mat(720, 1280, MatType.CV_8UC3, Scalar.Black);
        var ctx = new PipelineRunContext(image);
        var videoResult = new NodeResult();
        videoResult.Values["frame_index"] = frame.ToString();
        videoResult.Values["eof"] = "false";
        videoResult.Values["current_file"] = $"{video}#{frame:D8}";
        ctx.Results["视频源"] = videoResult;
        var detection = new NodeResult();
        detection.Values["detections_json"] = JsonSerializer.Serialize(detections);
        ctx.Results["目标检测"] = detection;
        ctx.Images["目标检测"] = image;
        return (ctx, image);
    }

    [Fact]
    public void TwentyTwoPlayers_MaintainUniqueIdsAcrossSmallMotion()
    {
        using var tracker = new MultiObjectTrackingNode("多目标跟踪",
            new Dictionary<string, string> { ["source"] = "目标检测", ["min_iou"] = "0.15" });
        YoloDetection[] Players(double dx) => Enumerable.Range(0, 22)
            .Select(i => new YoloDetection(0, "player", .99f,
                50 + i % 11 * 95 + dx, 80 + i / 11 * 210, 24, 52)).ToArray();

        var (c1, i1) = ContextWith(Players(0), 0);
        using (i1)
        {
            var first = tracker.Run(i1, c1);
            Assert.Equal("22", first.Values["track_count"]);
            Assert.Equal(22, first.Values["track_ids"].Split(',').Distinct().Count());
            first.OutputImage!.Dispose();
        }
        var (c2, i2) = ContextWith(Players(2), 1);
        using (i2)
        {
            var second = tracker.Run(i2, c2);
            Assert.Equal("22", second.Values["track_count"]);
            Assert.Equal(string.Join(",", Enumerable.Range(1, 22)), second.Values["track_ids"]);
            Assert.Equal(22, second.Annotations.Count);
            second.OutputImage!.Dispose();
        }
    }

    [Fact]
    public void LostTracksExpire_AndNewDetectionGetsNewId()
    {
        using var tracker = new MultiObjectTrackingNode("多目标跟踪",
            new Dictionary<string, string> { ["source"] = "目标检测", ["max_missed"] = "1" });
        var ball = new YoloDetection(1, "ball", .95f, 200, 100, 12, 12);
        var (firstCtx, firstImg) = ContextWith([ball], 0);
        using (firstImg)
        {
            var r = tracker.Run(firstImg, firstCtx);
            Assert.Equal("1", r.Values["track_ids"]);
            r.OutputImage!.Dispose();
        }
        for (var frame = 1; frame <= 2; frame++)
        {
            var (ctx, img) = ContextWith([], frame);
            using (img)
            {
                var r = tracker.Run(img, ctx);
                Assert.Equal("0", r.Values["track_count"]);
                r.OutputImage!.Dispose();
            }
        }
        var (lastCtx, lastImg) = ContextWith([ball], 3);
        using (lastImg)
        {
            var r = tracker.Run(lastImg, lastCtx);
            Assert.Equal("2", r.Values["track_ids"]);
            r.OutputImage!.Dispose();
        }
    }

    [Fact]
    public void VideoRestart_ResetsTrackingIds()
    {
        using var tracker = new MultiObjectTrackingNode("多目标跟踪",
            new Dictionary<string, string> { ["source"] = "目标检测" });
        var det = new YoloDetection(0, "player", .9f, 150, 100, 30, 60);
        for (var frame = 0; frame < 2; frame++)
        {
            var (ctx, img) = ContextWith([det], frame);
            using (img)
            {
                var result = tracker.Run(img, ctx);
                Assert.Equal("1", result.Values["track_ids"]);
                result.OutputImage!.Dispose();
            }
        }
        var (restartCtx, restartImage) = ContextWith([det], 0);
        using (restartImage)
        {
            var result = tracker.Run(restartImage, restartCtx);
            Assert.Equal("1", result.Values["track_ids"]);
            Assert.Equal(1, JsonDocument.Parse(result.Values["tracks_json"]).RootElement[0].GetProperty("hits").GetInt32());
            result.OutputImage!.Dispose();
        }
    }

    [Fact]
    public void MissingYoloSource_IsAnError_NotAnEmptyTrackResult()
    {
        using var tracker = new MultiObjectTrackingNode("多目标跟踪",
            new Dictionary<string, string> { ["source"] = "不存在的节点" });
        using var img = new Mat(10, 10, MatType.CV_8UC3);
        Assert.Throws<InvalidDataException>(() => tracker.Run(img, new PipelineRunContext(img)));
    }
}
