using OpenCvSharp;
using VisionInspection.Detection;
using VisionInspection.Models;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>
/// 字符识别（CharRec，传统路线）测试：Dice 相似度 / 判定纯函数 / 二值化极性 / 字符分割 /
/// 字模库目录契约（含非法字符转义）/ OCR 占位迁移映射 / 合成图端到端（训练字模 → 识别 → 判定 → 总范围）。
/// </summary>
public class CharRecTests
{
    private const string DirA0 = "A0";

    private static byte[] UniformSample(byte value)
    {
        var pixels = new byte[CharTemplateLibrary.NormW * CharTemplateLibrary.NormH];
        Array.Fill(pixels, value);
        return pixels;
    }

    private static CharRecNode.RoiResult Item(
        string name, string text, double conf, RoiMeta? meta = null, bool enabled = true) =>
        new(name, enabled, [new SegChar(new Rect(0, 0, 10, 20), text, conf)], meta ?? new RoiMeta(), default);

    // ===== 算法核纯逻辑 =====

    [Fact]
    public void Dice_IdenticalOne_DisjointZero()
    {
        var a = UniformSample(255);
        Assert.Equal(1.0, CharRecognizer.Dice(a, UniformSample(255)), 6);
        Assert.Equal(0.0, CharRecognizer.Dice(a, UniformSample(0)), 6);
        Assert.Equal(0.0, CharRecognizer.Dice(UniformSample(0), UniformSample(0)), 6); // 双方无前景
    }

    [Fact]
    public void Match_EmptyLibrary_UnknownZeroScore()
    {
        var (label, score) = CharRecognizer.Match(UniformSample(255), Array.Empty<CharSample>());
        Assert.Equal("?", label);
        Assert.Equal(0.0, score, 6);
    }

    [Fact]
    public void Binarize_Polarity_BrightAndDarkForeground()
    {
        using var gray = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(gray, new Rect(5, 0, 5, 10), Scalar.All(200), -1);

        using var bright = CharRecognizer.Binarize(gray, 128, otsu: false, brightOnDark: true);
        bright.GetArray(out byte[] b1);
        Assert.Equal(0, b1[0]);    // 暗底 → 背景
        Assert.Equal(255, b1[7]);  // 亮字 → 前景

        using var dark = CharRecognizer.Binarize(gray, 128, otsu: false, brightOnDark: false);
        dark.GetArray(out byte[] b2);
        Assert.Equal(255, b2[0]);  // 暗字 → 前景
        Assert.Equal(0, b2[7]);    // 亮底 → 背景
    }

    [Fact]
    public void SegmentChars_HeightFilter_AndXOrder()
    {
        using var bin = Mat.Zeros(60, 120, MatType.CV_8UC1).ToMat();
        Cv2.Rectangle(bin, new Rect(60, 10, 20, 30), Scalar.All(255), -1); // 高 30（保留）
        Cv2.Rectangle(bin, new Rect(10, 10, 20, 30), Scalar.All(255), -1); // 高 30（保留，X 更小）
        Cv2.Rectangle(bin, new Rect(100, 40, 10, 5), Scalar.All(255), -1); // 高 5（噪点，被过滤）

        var rects = CharRecognizer.SegmentChars(bin, minCharH: 10, maxCharH: 0);
        Assert.Equal(2, rects.Count);
        Assert.True(rects[0].X < rects[1].X); // 按 X 升序
        Assert.Equal(10, rects[0].X);
        Assert.Equal(60, rects[1].X);
    }

    [Fact]
    public void SegmentChars_MaxCharHeightFilter()
    {
        using var bin = Mat.Zeros(80, 60, MatType.CV_8UC1).ToMat();
        Cv2.Rectangle(bin, new Rect(10, 10, 20, 60), Scalar.All(255), -1);
        Assert.Empty(CharRecognizer.SegmentChars(bin, minCharH: 5, maxCharH: 40));
        Assert.Single(CharRecognizer.SegmentChars(bin, minCharH: 5, maxCharH: 0));
    }

    // ===== 判定纯函数 =====

    [Fact]
    public void CheckPass_ConfidenceBoundary()
    {
        // 阈值边界：conf == threshold 通过（conf < threshold 才失败）
        Assert.True(CharRecNode.CheckPass(CharRecNode.DecisionEqualNg, "A0", 0.7, null, 0.7));
        Assert.False(CharRecNode.CheckPass(CharRecNode.DecisionEqualNg, "A0", 0.69, null, 0.7));
    }

    [Fact]
    public void CheckPass_EqualMode()
    {
        Assert.True(CharRecNode.CheckPass(CharRecNode.DecisionEqualNg, "A0", 0.9, "A0", 0.7));
        Assert.False(CharRecNode.CheckPass(CharRecNode.DecisionEqualNg, "B0", 0.9, "A0", 0.7));
        Assert.False(CharRecNode.CheckPass(CharRecNode.DecisionEqualNg, "?0", 0.9, "A0", 0.7)); // 未识别必不过
        Assert.False(CharRecNode.CheckPass(CharRecNode.DecisionEqualNg, "", 0.9, "A0", 0.7));   // 漏印必不过
    }

    [Fact]
    public void CheckPass_ContainMode_AndEmptyTarget()
    {
        Assert.True(CharRecNode.CheckPass(CharRecNode.DecisionContainNg, "XA0Y", 0.9, "A0", 0.7));
        Assert.False(CharRecNode.CheckPass(CharRecNode.DecisionContainNg, "XY", 0.9, "A0", 0.7));
        // 目标字符为空 → 仅按置信度判定
        Assert.True(CharRecNode.CheckPass(CharRecNode.DecisionEqualNg, "随便", 0.9, "", 0.7));
        Assert.True(CharRecNode.CheckPass(CharRecNode.DecisionEqualNg, "随便", 0.9, null, 0.7));
    }

    [Fact]
    public void ComputeDecision_ObserveOnly_AlwaysOk()
    {
        var ng = Item("SN", "B0", 0.9, new RoiMeta(Target: "A0"));
        Assert.Equal("OK", CharRecNode.ComputeDecision(CharRecNode.DecisionObserveOnly, [ng]));
    }

    [Fact]
    public void ComputeDecision_JudgeItems_AnyFailNg()
    {
        var ok1 = Item("SN", "A0", 0.9, new RoiMeta(Target: "A0"));
        var ok2 = Item("日期", "123", 0.9, new RoiMeta(Target: "123"));
        Assert.Equal("OK", CharRecNode.ComputeDecision(CharRecNode.DecisionEqualNg, [ok1, ok2]));

        var fail = Item("日期", "124", 0.9, new RoiMeta(Target: "123"));
        Assert.Equal("NG", CharRecNode.ComputeDecision(CharRecNode.DecisionEqualNg, [ok1, fail]));
    }

    [Fact]
    public void ComputeDecision_DisabledAndObserve_Skipped()
    {
        // 停用项 / 仅观察项不参与判定（低置信也不判 NG）
        var disabled = Item("停用区", "B0", 0.1, new RoiMeta(Target: "A0"), enabled: false);
        var observe = Item("观察区", "B0", 0.1, new RoiMeta(Judge: "仅观察", Target: "A0"));
        Assert.Equal("OK", CharRecNode.ComputeDecision(CharRecNode.DecisionEqualNg, [disabled, observe]));
    }

    [Fact]
    public void ComputeDecision_RoiThresholdOverridesDefault()
    {
        // 检测项级阈值 0.95：conf 0.9 不达标 → NG（缺省 0.7 时可通过）
        var item = Item("SN", "A0", 0.9, new RoiMeta(Threshold: "0.95", Target: "A0"));
        Assert.Equal("NG", CharRecNode.ComputeDecision(CharRecNode.DecisionEqualNg, [item]));
        var relaxed = Item("SN", "A0", 0.9, new RoiMeta(Target: "A0"));
        Assert.Equal("OK", CharRecNode.ComputeDecision(CharRecNode.DecisionEqualNg, [relaxed]));
    }

    // ===== 字模库目录契约 =====

    [Fact]
    public void Library_SaveLoadRoundTrip_AndEscaping()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"charlib_{Guid.NewGuid():N}");
        try
        {
            var pixels = UniformSample(255);
            pixels[0] = 0;
            CharTemplateLibrary.SaveSample(dir, "A0", pixels);
            CharTemplateLibrary.SaveSample(dir, "?", pixels); // 路径非法字符 → URL 转义目录

            Assert.True(Directory.Exists(Path.Combine(dir, Uri.EscapeDataString("?"))));
            var loaded = CharTemplateLibrary.Load(dir);
            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, s => s.Label == "A0");
            Assert.Contains(loaded, s => s.Label == "?");
            Assert.All(loaded, s => Assert.Equal(pixels, s.Pixels)); // 归一化尺寸/像素无损

            var samples = CharTemplateLibrary.ListSamples(dir);
            Assert.Equal(2, samples.Count);

            CharTemplateLibrary.DeleteSample(samples.First(s => s.Label == "?").Path);
            Assert.Single(CharTemplateLibrary.Load(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Library_LoadMissingDir_ReturnsEmpty()
    {
        Assert.Empty(CharTemplateLibrary.Load(Path.Combine(Path.GetTempPath(), $"no_such_{Guid.NewGuid():N}")));
        Assert.Empty(CharTemplateLibrary.Load(""));
    }

    // ===== 旧 OCR 占位迁移 =====

    [Fact]
    public void LegacyOcrType_MapsToCharRec()
    {
        using var node = NodeFactory.Create("OCR", "旧占位");
        Assert.Equal("CharRec", node.Type);
        Assert.False(NodeFactory.IsRegistered("OCR")); // 占位注册已删除，「添加模块」不再出现
    }

    // ===== 合成图端到端：训练字模 → 识别 → 判定 =====

    /// <summary>Hershey 字体渲染（白底黑字，训练与识别同字体同参数 → 归一化后 Dice≈1）。</summary>
    private static Mat PutTextCore(int width, int height, string text, int x, int y)
    {
        var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255));
        Cv2.PutText(mat, text, new Point(x, y), HersheyFonts.HersheySimplex, 1.4, Scalar.All(0), 2, LineTypes.AntiAlias);
        return mat;
    }

    /// <summary>训练一个字符的字模：单独渲染 → 二值化 → 分割 → 归一化 → 存库。</summary>
    private static void TrainGlyph(string dir, string ch)
    {
        using var glyph = PutTextCore(120, 160, ch, 30, 120);
        using var gray = CharRecNode.ToGray(glyph);
        using var bin = CharRecognizer.Binarize(gray, 128, otsu: false, brightOnDark: false);
        var rects = CharRecognizer.SegmentChars(bin, minCharH: 10, maxCharH: 0);
        var rect = Assert.Single(rects);
        CharTemplateLibrary.SaveSample(dir, ch, CharRecognizer.NormalizeSample(bin, rect));
    }

    private static string TrainLibrary(params string[] chars)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"charrec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        foreach (var ch in chars)
        {
            TrainGlyph(dir, ch);
        }
        return dir;
    }

    private static CharRecNode NewNode(string modelDir, params (string Key, string Value)[] extra)
    {
        var node = new CharRecNode("01 字符识别");
        node.SetParam("model_dir", modelDir);
        node.SetParam("polarity", CharRecNode.PolarityDarkOnBright);
        node.SetParam("min_char_h", "10");
        node.SetParam("bin_method", "固定阈值");
        node.SetParam("threshold", "128");
        foreach (var (k, v) in extra)
        {
            node.SetParam(k, v);
        }
        return node;
    }

    [Fact]
    public void EndToEnd_TrainRecognize_MatchOk()
    {
        var dir = TrainLibrary("A", "0");
        try
        {
            using var img = PutTextCore(200, 120, "A", 30, 90);
            Cv2.PutText(img, "0", new Point(120, 90), HersheyFonts.HersheySimplex, 1.4, Scalar.All(0), 2, LineTypes.AntiAlias);
            using var node = NewNode(dir, ("own_rois", NodeRois.SerializeOwnFull(new List<RoiItem>
            {
                new("SN", new RoiRect(0.5, 0.5, 0.95, 0.9, 0), new RoiMeta(Target: "A0")),
            })));
            node.EnsureLoaded("");
            Assert.True(node.TemplateCount >= 2);

            var result = node.Run(img, new PipelineRunContext(img));
            Assert.Equal("A0", result.Values["roi_SN_text"]);
            Assert.Equal("A0", result.Values["text"]);
            Assert.Equal("2", result.Values["count"]);
            Assert.Equal("1", result.Values["roi_SN_match"]);
            Assert.Equal("OK", result.Decision);
            Assert.True(result.Values.TryGetValue("roi_SN_conf", out var conf)
                && double.Parse(conf, System.Globalization.CultureInfo.InvariantCulture) > 0.6);
            Assert.Equal(2, result.Annotations.Count(s => s.Box is not null)); // 逐字符框
            Assert.Single(result.Annotations, s => s.Polys is not null); // 检测项四边形
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EndToEnd_Mismatch_Ng()
    {
        var dir = TrainLibrary("A", "0");
        try
        {
            using var img = PutTextCore(200, 120, "A", 30, 90);
            Cv2.PutText(img, "0", new Point(120, 90), HersheyFonts.HersheySimplex, 1.4, Scalar.All(0), 2, LineTypes.AntiAlias);
            using var node = NewNode(dir, ("own_rois", NodeRois.SerializeOwnFull(new List<RoiItem>
            {
                new("SN", new RoiRect(0.5, 0.5, 0.95, 0.9, 0), new RoiMeta(Target: "B0")),
            })));
            node.EnsureLoaded("");

            var result = node.Run(img, new PipelineRunContext(img));
            Assert.Equal("0", result.Values["roi_SN_match"]);
            Assert.Equal("NG", result.Decision);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EndToEnd_ContainMode_AndObserveOnly()
    {
        var dir = TrainLibrary("A", "0");
        try
        {
            using var img = PutTextCore(200, 120, "A", 30, 90);
            Cv2.PutText(img, "0", new Point(120, 90), HersheyFonts.HersheySimplex, 1.4, Scalar.All(0), 2, LineTypes.AntiAlias);

            // 包含目标字符：目标 "A" ⊂ "A0" → OK
            using (var contain = NewNode(dir, ("decision_mode", CharRecNode.DecisionContainNg),
                       ("own_rois", NodeRois.SerializeOwnFull(new List<RoiItem>
                       {
                           new("SN", new RoiRect(0.5, 0.5, 0.95, 0.9, 0), new RoiMeta(Target: "A")),
                       }))))
            {
                contain.EnsureLoaded("");
                Assert.Equal("OK", contain.Run(img, new PipelineRunContext(img)).Decision);
            }

            // 仅识别不判定：目标不匹配也恒 OK
            using (var observe = NewNode(dir, ("decision_mode", CharRecNode.DecisionObserveOnly),
                       ("own_rois", NodeRois.SerializeOwnFull(new List<RoiItem>
                       {
                           new("SN", new RoiRect(0.5, 0.5, 0.95, 0.9, 0), new RoiMeta(Target: "B0")),
                       }))))
            {
                observe.EnsureLoaded("");
                var result = observe.Run(img, new PipelineRunContext(img));
                Assert.Equal("OK", result.Decision);
                Assert.Equal("A0", result.Values["roi_SN_text"]); // 文本仍输出
                Assert.DoesNotContain(result.Annotations, s => s.Kind == NodeShapeKind.Defect); // 观察模式标注不染红
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EndToEnd_ScopeFilter_RemovesOutOfScopeChars()
    {
        var dir = TrainLibrary("A", "0", "B");
        try
        {
            // 三字符分开放置：A(30) 0(130) B(230)，图像 320 宽
            using var img = PutTextCore(320, 120, "A", 30, 90);
            Cv2.PutText(img, "0", new Point(130, 90), HersheyFonts.HersheySimplex, 1.4, Scalar.All(0), 2, LineTypes.AntiAlias);
            Cv2.PutText(img, "B", new Point(230, 90), HersheyFonts.HersheySimplex, 1.4, Scalar.All(0), 2, LineTypes.AntiAlias);

            using var node = NewNode(dir,
                ("scope_index", "1"), // 第 2 个检测项（范围）为总范围
                ("own_rois", NodeRois.SerializeOwnFull(new List<RoiItem>
                {
                    // 全文本区（会检出 A0B，总范围过滤后只留 A0）
                    new("文本", new RoiRect(0.5, 0.5, 0.98, 0.9, 0), new RoiMeta(Target: "A0B")),
                    // 范围：x 像素 0~200（只含 A0，不含 B）
                    new("范围", new RoiRect(0.3125, 0.5, 0.625, 0.9, 0), new RoiMeta()),
                })));
            node.EnsureLoaded("");

            var result = node.Run(img, new PipelineRunContext(img));
            Assert.Equal("A0", result.Values["roi_文本_text"]); // B 已被总范围过滤（不过滤应为 A0B）
            Assert.Equal("A0", result.Values["roi_范围_text"]); // 范围自身只覆盖 A0
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EndToEnd_NoRoi_RecognizesWholeImageWithoutJudgment()
    {
        var dir = TrainLibrary("A", "0");
        try
        {
            using var img = PutTextCore(200, 120, "A", 30, 90);
            Cv2.PutText(img, "0", new Point(120, 90), HersheyFonts.HersheySimplex, 1.4, Scalar.All(0), 2, LineTypes.AntiAlias);
            using var node = NewNode(dir);
            node.EnsureLoaded("");

            var result = node.Run(img, new PipelineRunContext(img));
            Assert.Equal("OK", result.Decision); // 未画检测项 = 不判定恒 OK
            Assert.Equal("A0", result.Values["text"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
