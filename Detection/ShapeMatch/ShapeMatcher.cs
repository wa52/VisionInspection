using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OpenCvSharp;

namespace VisionInspection.Detection;

/// <summary>单个轮廓匹配实例（搜索图原始像素坐标）：基准点位�?+ 旋转�?�? + 分数(0~1)�?</summary>
public sealed record ShapeMatchInstance(double X, double Y, double Angle, double Score);

/// <summary>
/// 方向轮廓匹配器（VisionMaster 轮廓匹配同类原理，由粗到细）�?/// 粗层全位置×粗角度步长评分（Score = 方向匹配点数/模板有效点数；点积≥cos(45°) 视为方向匹配�?/// 贪心早停：必然失配点数超出预算即剪枝）→ 候�?NMS �?逐层细化位置/角度 �?原始分辨�?�?圆域 IoU NMS 取前 N�?/// 方向极性：启用时要求梯度方向一致（dot≥阈值），忽略时允许反向（|dot|≥阈值）�?/// 快速模式（fastMode=true，快速匹配节点用，粗定位节拍优先）：
/// 快速模式仍使用金字塔和预旋转缓存，但采用更保守的粗扫步长、角度采样和候选漏斗，优先保证重复稳定性。
/// 每层点集使用空间均匀抽样，避免评分过度依赖局部强边缘；亚像素精修后重新计算最终连续分数。
/// </summary>
public sealed class ShapeMatcher
{
    private const double MaxDirectionDiffDeg = 45.0;
    private const double FineAngleStep = 1.0;
    private const double MaxCoarseAngleStep = 12.0;
    private const int MaxLevels = 6;
    /// <summary>快速模式每层参与评分的点数上限（均匀抽稀，保持空间分布）�?</summary>
    internal const int FastMaxPointsPerLevel = 600;

    // 快速模式调优旋钮（internal static：诊断测试可调；默认值优先稳定性）
    /// <summary>快速模式粗层进入细化的候选上限（完整模式 32）。</summary>
    internal static int FastCoarseCandidates = 32;
    /// <summary>快速模式首层细化后保留的候选数（定位只看最佳位姿）。</summary>
    private const int FastRefineKeep = 16;
    /// <summary>快速模式合成粗层的最短边下限（再深边缘糊化，漏斗失效）�?</summary>
    private const int FastSynthMinSide = 48;
    /// <summary>快速模式最多向模板层数之外合成的层数�?</summary>
    internal static int FastMaxSynthLevels = 2;
    /// <summary>快速模式粗层角度步长是�?×2（偏差由 previousStep 细化窗口覆盖；关掉更稳但更慢）�?</summary>
    internal static bool FastDoubleAngleStep = false;
    /// <summary>快速模式粗层位置步长（�?；stride 越大量化偏差越大，需配合细化窗口）�?</summary>
    internal static int FastCoarseStride = 2;
    /// <summary>完整模式粗层（有细化链兜底时）是否用步长 2（量�?�? 粗层像素，细化收敛到同一峰值；A/B 对照用）�?</summary>
    internal static bool FullCoarseStride2 = true;

    /// <summary>合成�?角度倍步允许的角度量化位移上限（px，粗层坐标系）——超过则网格点得分塌方、漏斗丢真值�?</summary>
    internal const double FastMaxAngleDisplacementPx = 2.0;
    /// <summary>是否启用 AVX2 向量化评分（vgatherdps 在 AMD 上慢于标量，Intel 上可开；默认关）。</summary>
    internal static bool EnableAvx2Score = false;

    private readonly double _templateRadius;
    /// <summary>本次 Find 允许的合成层数（按模板半径自适应：角度量化位�?�?半径/115px，与层无关）�?</summary>
    private int _synthBudget;
    /// <summary>本次 Find 是否允许角度倍步（位�?�?半径/57px）�?</summary>
    private bool _allowDoubleAngle;

    private static readonly double[] SubPixelRoundsFull = [1.0, 0.5, 0.25];
    private static readonly double[] SubPixelRoundsFast = [1.0];

    private readonly ShapeTemplate _template;
    private readonly double _angleStart;
    private readonly double _angleExtent;
    private readonly int _numMatches;
    private readonly double _minScore;
    private readonly double _maxOverlap;
    private readonly bool _usePolarity;
    private readonly double _minContrast;
    private readonly double _sigma;
    private readonly bool _fastMode;
    private readonly int _workers;

    /// <summary>快速模式点集抽稀缓存（惰性，每层一份；实例内单线程使用）�?</summary>
    private List<TemplatePoint>[]? _fastPoints;

    /// <summary>合成层原始点集缓存（�?层号，�?最深层逐级减半的点集；仅快速模式触及）�?</summary>
    private Dictionary<int, List<TemplatePoint>>? _synthPoints;

    private PointArrays[]? _pointArrays;
    private ConcurrentDictionary<(int Level, double Angle), RotatedPoints>? _rotatedCache;

    private readonly double _cosDirThreshold;
    private readonly float _cosDirThresholdF;
    private readonly float _minContrastF;

    public ShapeMatcher(ShapeTemplate template, double minScore, double angleStart, double angleExtent,
        int numMatches, double maxOverlap, double minContrast, double sigma, bool usePolarity,
        bool fastMode = false, int maxWorkers = 0)
    {
        _template = template;
        _minScore = Math.Clamp(minScore, 0.01, 1.0);
        _angleStart = angleStart;
        _angleExtent = Math.Max(0, angleExtent);
        _numMatches = Math.Max(1, numMatches);
        _maxOverlap = Math.Clamp(maxOverlap, 0.05, 0.95);
        _usePolarity = usePolarity;
        _minContrast = Math.Max(1, minContrast);
        _sigma = Math.Max(0, sigma);
        _fastMode = fastMode;

        // Keep the worker count bounded so the matching stages do not oversubscribe the process.
        _workers = Math.Clamp(maxWorkers > 0 ? maxWorkers : Math.Max(1, Environment.ProcessorCount - 2), 1, 64);
        _templateRadius = TemplateRadius();
        _cosDirThreshold = Math.Cos(MaxDirectionDiffDeg * Math.PI / 180.0);
        // 标量/SIMD 统一用 float 域比较（保证两路径逐位一致；阈值边界差异 ≤1 点/位置，已由真实场景断言兜底）
        _cosDirThresholdF = (float)_cosDirThreshold;
        _minContrastF = (float)_minContrast;
    }

    /// <summary>诊断探针：最近一�?Find 的最粗层号与粗层候选（原始分辨率坐标，限流前全量）�?</summary>
    internal int LastCoarsestLevel { get; private set; } = -1;
    internal IReadOnlyList<(double X, double Y, double Angle, double Score)> LastCoarseCandidates { get; private set; } = [];
    internal double LastPyramidMs { get; private set; }
    internal double LastCoarseMs { get; private set; }
    internal double LastRefineMs { get; private set; }
    internal double LastSubMs { get; private set; }

    /// <summary>在灰度搜索图中查找全部实例（按分数降序）�?</summary>
    public List<ShapeMatchInstance> Find(Mat graySearch)
    {
        if (graySearch.Empty() || graySearch.Channels() != 1 || _template.Levels == 0)
        {
            return [];
        }

        var levels = LevelsForImage(Math.Min(graySearch.Width, graySearch.Height), _template.Levels);
        _pointArrays = new PointArrays[Math.Max(levels, _template.Levels)];
        _rotatedCache = new ConcurrentDictionary<(int Level, double Angle), RotatedPoints>();

        // 快速模式：粗搜索向下多探层——真实层先用满（不引入合成误差），模板层数不够时由最深层坐标减半**合成**更粗层。
        //（pyrDown 链的近似关系，梯度方向不变；仅作候选漏斗，最终位姿由真实层细化兜底）。
        // 采样激进度随模板半径自适应：native 角度步长链的角度量化位移 ≈ 模板半径/115 px（与层无关），
        // 倍步 ≈ 半径/57 px——位移过大时粗层网格得分塌方、漏斗丢真值（大模板实测教训，合成测试的小模板发现不了）。
        // 小模板（半径 ≤~230px）保留合成+倍步全程加速；大模板自动退回保守采样。
        if (_fastMode)
        {
            var minSide = Math.Min(graySearch.Width, graySearch.Height);
            _synthBudget = _templateRadius / 115.0 <= FastMaxAngleDisplacementPx ? FastMaxSynthLevels : 0;
            _allowDoubleAngle = _templateRadius / 57.0 <= FastMaxAngleDisplacementPx;
            while (levels < MaxLevels && (minSide >> levels) >= FastSynthMinSide)
            {
                if (levels < _template.Levels)
                {
                    levels++;
                }
                else if (levels - _template.Levels < _synthBudget && _template.Levels >= 2)
                {
                    levels++;
                }
                else
                {
                    break;
                }
            }
        }
        else
        {
            _synthBudget = 0;
            _allowDoubleAngle = false;
        }
        var coarsest = levels - 1;


        // 金字塔：images[i] = 1/2^i 缩放层；每层预计算归一化梯度方向与幅值（流水线：第 i 层方向提取与 pyrDown 下探重叠）。
        var phaseSw = System.Diagnostics.Stopwatch.StartNew();
        var dirX = new float[levels][];
        var dirY = new float[levels][];
        var mag = new float[levels][];
        var width = new int[levels];
        var height = new int[levels];
        var mats = new List<Mat>(levels);
        var dirTasks = new Task[levels];
        var current = graySearch.Clone();
        try
        {
            for (var i = 0; i < levels; i++)
            {
                if (i > 0)
                {
                    var next = new Mat();
                    Cv2.PyrDown(current, next);
                    current = next;
                }
                mats.Add(current);
                width[i] = current.Width;
                height[i] = current.Height;
                var captured = current;
                var idx = i;
                dirTasks[i] = Task.Run(() =>
                {
                    (dirX[idx], dirY[idx], mag[idx]) = ExtractDirections(captured, _sigma);
                });
            }
            Task.WaitAll(dirTasks);
        }
        finally
        {
    
        // 异常路径也必须先等在跑的任务完成，再释放它们正在读的 Mat
            try
            {
                Task.WaitAll(dirTasks.Where(t => t != null).ToArray());
            }
            catch
            {
        
        // 主异常继续向上传播，这里的任务异常不屏蔽原始错误
            }
            foreach (var m in mats)
            {
                m.Dispose();
            }
        }
        LastPyramidMs = phaseSw.Elapsed.TotalMilliseconds;
        phaseSw.Restart();


        // 由粗到细
        LastCoarsestLevel = coarsest;
        var candidates = SearchCoarsestLevel(dirX, dirY, mag, width, height, coarsest);
        LastCoarseMs = phaseSw.Elapsed.TotalMilliseconds;
        phaseSw.Restart();
        var previousStep = Math.Min(FineAngleStep * Math.Pow(2, coarsest), MaxCoarseAngleStep);
        for (var l = coarsest - 1; l >= 0; l--)
        {
    
        // 快速模式粗层位置步�?4：紧贴粗层的第一层细化窗�?±4 覆盖 �? 粗层像素量化；其下整数网�?±2 足够
            var reach = (_fastMode && l == coarsest - 1) ? 4 : 2;
            candidates = RefineLevel(candidates, l, previousStep, reach, dirX, dirY, mag, width, height);
            previousStep = FineAngleStep * Math.Pow(2, l);
            if (_fastMode && l == coarsest - 1 && candidates.Count > FastRefineKeep)
            {
        
                // 候选漏斗：首层细化后按分砍到前 FastRefineKeep 个（最终阈值仍由原始层严格把关）
                candidates.Sort(CompareCandidates);
                candidates = candidates.Take(FastRefineKeep).ToList();
            }
        }


        // 圆域 IoU NMS（半径取模板最远轮廓点），按分数降序取�?N�?        // 同分�?|角度| 升序稳定胜负（List.Sort 不稳定，恒等位姿附近 ±1° 同分会在多次执行间乱跳）
        LastRefineMs = phaseSw.Elapsed.TotalMilliseconds;
        phaseSw.Restart();
        var radius = TemplateRadius();
        var refined = new List<(double X, double Y, double Angle, double Score)>();
        var finalPoints = PointArraysFor(0);
        foreach (var c in candidates)
        {
            // 亚像素细化：3 点抛物线插值分数面顶点（位置 ±1px、角度 ±1°），整数网格精度 → 亚像素
            var (ddx, ddy, dAngle) = SubPixelOffset(c.X, c.Y, c.Angle, dirX, dirY, mag, width, height);
            var finalX = c.X + ddx;
            var finalY = c.Y + ddy;
            var finalAngle = c.Angle + dAngle;
            var finalScore = ScoreAtSub(
                finalPoints.Xs, finalPoints.Ys, finalPoints.Dxs, finalPoints.Dys, finalPoints.Xs.Length,
                dirX[0], dirY[0], mag[0], width[0], height[0],
                finalX, finalY, finalAngle * Math.PI / 180.0);
            if (finalScore >= _minScore)
            {
                refined.Add((finalX, finalY, finalAngle, finalScore));
            }
        }

        // 精修会改变分数和位置，必须按最终值重新排序并执行 NMS。
        refined.Sort(CompareCandidates);
        var result = new List<ShapeMatchInstance>();
        foreach (var c in refined)
        {
            if (result.Count >= _numMatches) break;
            if (result.Any(r => CircleIou(r.X, r.Y, radius, c.X, c.Y, radius) > _maxOverlap)) continue;
            result.Add(new ShapeMatchInstance(c.X, c.Y, c.Angle, c.Score));
        }
        LastSubMs = phaseSw.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>
    /// 亚像素细化：�?*连续评分�?*上迭代坐标下降（模板点浮点变�?+ 图像梯度场双线性插值，方向按最大幅值邻�?180° 符号对齐防对侧抵消）�?    /// 完整模式步长 1.0�?.5�?.25 三轮抛物线顶点；快速模式只做一�?1.0（简单精修，重复精度 ~0.2px 已满足粗定位）�?    /// </summary>
    private (double Dx, double Dy, double DAngle) SubPixelOffset(double x, double y, double angleDeg,
        float[][] dirX, float[][] dirY, float[][] mag, int[] width, int[] height)
    {
        var level = 0;
        var pointArrays = PointArraysFor(level);
        var xs = pointArrays.Xs;
        var ys = pointArrays.Ys;
        var dxs = pointArrays.Dxs;
        var dys = pointArrays.Dys;
        var n = xs.Length;
        if (n == 0) return (0, 0, 0);
        var w = width[level];
        var ix = (int)x;
        var iy = (int)y;
        double S(double px, double py, double deg) =>
            ScoreAtSub(xs, ys, dxs, dys, n, dirX[level], dirY[level], mag[level], w, height[level], px, py, deg * Math.PI / 180.0);

        var cx = x;
        var cy = y;
        var ca = angleDeg;
        var rounds = _fastMode ? SubPixelRoundsFast : SubPixelRoundsFull;
        foreach (var h in rounds)
        {
            var s0 = S(cx, cy, ca);
            var vx = Vertex(S(cx - h, cy, ca), s0, S(cx + h, cy, ca));
            var vy = Vertex(S(cx, cy - h, ca), s0, S(cx, cy + h, ca));
            var va = Vertex(S(cx, cy, ca - h), s0, S(cx, cy, ca + h));
            cx += Math.Clamp(vx * h, -h, h);
            cy += Math.Clamp(vy * h, -h, h);
            ca += Math.Clamp(va * h, -h, h);
        }

        return (cx - ix, cy - iy, ca - angleDeg);
    }

    private static double Vertex(double sMinus, double s0, double sPlus)
    {
        var den = sMinus - 2 * s0 + sPlus;
        if (Math.Abs(den) < 1e-9) return 0;
        return Math.Clamp(0.5 * (sMinus - sPlus) / den, -0.5, 0.5);
    }

    private List<(double X, double Y, double Angle, double Score)> SearchCoarsestLevel(
        float[][] dirX, float[][] dirY, float[][] mag, int[] width, int[] height, int level)
    {
        var pointArrays = PointArraysFor(level);
        var xs = pointArrays.Xs;
        var ys = pointArrays.Ys;
        var dxs = pointArrays.Dxs;
        var dys = pointArrays.Dys;
        var n = xs.Length;
        var result = new List<(double X, double Y, double Angle, double Score)>();
        if (n == 0)
        {
            return result;
        }


        // 候选位置范围：基准点和旋转后的全部模板点都必须落在层图像内。
        // 搜索图是节点 ROI 的裁剪图，禁止用部分模板轮廓在 ROI 边缘获得匹配。
        // 快速模式：位置步长 4（量化 ≤2 粗层像素，由下一层 ±4px 细化窗口覆盖；最粗层=原始层时退 2，靠亚像素抛物线收尾），
        // 角度步长 ×2 仅在小半径模板放行（偏差由 previousStep 细化窗口覆盖）。
        // 完整模式：粗层有细化链兜底时步长 2（量化 ≤1 粗层像素，细化收敛到同一连续峰值，结果不变）；最粗层=原始层保持 1。

        var stride = _fastMode
            ? (level > 0 ? Math.Max(1, FastCoarseStride) : 2)
            : (level > 0 && FullCoarseStride2 ? 2 : 1);
        var step = Math.Min(FineAngleStep * Math.Pow(2, level), MaxCoarseAngleStep);
        if (_fastMode && _allowDoubleAngle && FastDoubleAngleStep)
        {
            step = Math.Min(step * 2, MaxCoarseAngleStep * 2);
        }
        var x0 = 1;
        var y0 = 1;
        var x1 = width[level] - 2;
        var y1 = height[level] - 2;
        if (x1 < x0 || y1 < y0)
        {
            return result;
        }


        // 非原始层放宽 0.1：位置/角度量化会损失几个百分点，避免真值候选在粗层被阈值挡掉
        var minScoreLevel = LevelMinScore(level);
        var minMatched = (int)Math.Ceiling(minScoreLevel * n);

        // 分块 = 角度 × 行带（行带高 ~64px）。分解只取决于图像与角度数、与并行度无关——
        // 串行与并行遍历同一组分块、按块序合并，结果逐位一致。每块内先做该角度的模板点预旋转（一次），再扫块内位置。
        var angles = new List<double>();
        for (var a = _angleStart; a <= _angleStart + _angleExtent + 1e-9; a += step)
        {
            angles.Add(a);
        }
        var bandRows = 64;
        var bands = Math.Max(1, (y1 - y0 + bandRows) / bandRows);
        var blocks = new (int AngleIdx, int Y0, int Y1)[angles.Count * bands];
        var bi = 0;
        for (var ai = 0; ai < angles.Count; ai++)
        {
            for (var b = 0; b < bands; b++)
            {
                var by0 = y0 + b * bandRows;
                blocks[bi++] = (ai, by0, Math.Min(by0 + bandRows - 1, y1));
            }
        }
        var perBlock = new List<(double X, double Y, double Angle, double Score)>[blocks.Length];
        if (_workers <= 1 || blocks.Length <= 1)
        {
            for (var i = 0; i < blocks.Length; i++)
            {
                perBlock[i] = ScanBlock(blocks[i].AngleIdx, blocks[i].Y0, blocks[i].Y1, angles, xs, ys, dxs, dys,
                    dirX[level], dirY[level], mag[level], width[level], stride, x0, x1, minMatched, minScoreLevel, level);
            }
        }
        else
        {
            Parallel.For(0, blocks.Length, new ParallelOptions { MaxDegreeOfParallelism = _workers }, i =>
            {
                perBlock[i] = ScanBlock(blocks[i].AngleIdx, blocks[i].Y0, blocks[i].Y1, angles, xs, ys, dxs, dys,
                    dirX[level], dirY[level], mag[level], width[level], stride, x0, x1, minMatched, minScoreLevel, level);
            });
        }
        foreach (var list in perBlock)
        {
            result.AddRange(list);
        }

        // 诊断探针：限流前全量粗层候选
        LastCoarseCandidates = result.ToList();

        // 粗层候选限流：按分数取�?N 个进入细化（同分�?|角度| 升序，保证确定性）
        result.Sort(CompareCandidates);
        return result.Take(_fastMode ? Math.Max(1, FastCoarseCandidates) : 32).ToList();
    }

    /// <summary>扫描单个块（一个角度的一个行带）：每块预旋转一次模板点，扫块内全部候选位置�?</summary>
    private List<(double X, double Y, double Angle, double Score)> ScanBlock(
        int angleIdx, int by0, int by1, List<double> angles, float[] xs, float[] ys, float[] dxs, float[] dys,
        float[] dirX, float[] dirY, float[] mag, int width,
        int stride, int x0, int x1, int minMatched, double minScoreLevel, int level)
    {
        var angleDeg = angles[angleIdx];
        var rot = RotatedFor(level, xs, ys, dxs, dys, angleDeg);
        var found = new List<(double X, double Y, double Angle, double Score)>();
        for (var py = by0; py <= by1; py += stride)
        {
            for (var px = x0; px <= x1; px += stride)
            {
                var score = ScoreAtRot(rot, dirX, dirY, mag, width, px, py, minMatched);
                if (score >= minScoreLevel)
                {
            
        // 层坐�?�?原始分辨率坐标（×2^level，层�?0 = 原始分辨率）
                    found.Add((px * Math.Pow(2, level), py * Math.Pow(2, level), angleDeg, score));
                }
            }
        }
        return found;
    }

    private static int CompareCandidates(
        (double X, double Y, double Angle, double Score) a, (double X, double Y, double Angle, double Score) b)
    {
        var byScore = b.Score.CompareTo(a.Score);
        if (byScore != 0) return byScore;
        var byAbsAngle = Math.Abs(a.Angle).CompareTo(Math.Abs(b.Angle));
        if (byAbsAngle != 0) return byAbsAngle;
        var byAngle = a.Angle.CompareTo(b.Angle);
        if (byAngle != 0) return byAngle;
        var byX = a.X.CompareTo(b.X);
        return byX != 0 ? byX : a.Y.CompareTo(b.Y);
    }

    private List<(double X, double Y, double Angle, double Score)> RefineLevel(
        List<(double X, double Y, double Angle, double Score)> candidates, int level, double previousStep, int reach,
        float[][] dirX, float[][] dirY, float[][] mag, int[] width, int[] height)
    {
        var pointArrays = PointArraysFor(level);
        var xs = pointArrays.Xs;
        var ys = pointArrays.Ys;
        var dxs = pointArrays.Dxs;
        var dys = pointArrays.Dys;
        var n = xs.Length;
        var result = new List<(double X, double Y, double Angle, double Score)>();
        if (n == 0)
        {
            return result;
        }

        var minScoreLevel = LevelMinScore(level);
        var minMatched = (int)Math.Ceiling(minScoreLevel * n);
        var step = FineAngleStep * Math.Pow(2, level);
        var scale = Math.Pow(2, level);

        // 候选间相互独立（共享数组只读），按候选并行；结果按候选索引序合并，与串行一致。
        var perCandidate = new List<(double X, double Y, double Angle, double Score)>[candidates.Count];
        if (_workers <= 1 || candidates.Count <= 1)
        {
            for (var ci = 0; ci < candidates.Count; ci++)
            {
                perCandidate[ci] = RefineOne(candidates[ci], xs, ys, dxs, dys, dirX[level], dirY[level], mag[level],
                    width[level], minMatched, minScoreLevel, step, scale, previousStep, reach, level);
            }
        }
        else
        {
            Parallel.For(0, candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = _workers }, ci =>
            {
                perCandidate[ci] = RefineOne(candidates[ci], xs, ys, dxs, dys, dirX[level], dirY[level], mag[level],
                    width[level], minMatched, minScoreLevel, step, scale, previousStep, reach, level);
            });
        }
        foreach (var list in perCandidate)
        {
            result.AddRange(list);
        }
        return result;
    }

    /// <summary>细化单个候选：角度外层（每角度预旋转一次）× 位置邻域 ±reach，取层内最优�?</summary>
    private List<(double X, double Y, double Angle, double Score)> RefineOne(
        (double X, double Y, double Angle, double Score) c, float[] xs, float[] ys, float[] dxs, float[] dys,
        float[] dirX, float[] dirY, float[] mag, int width,
        int minMatched, double minScoreLevel, double step, double scale, double previousStep, int reach, int level)
    {
        var found = new List<(double X, double Y, double Angle, double Score)>();

        // 候选为原始分辨率坐标 → 本层坐标（÷2^level）；邻域 ±reach px、角度 ±上层步长。
        // 角度外层（与旧实现遍历集合相同，仅平局选取顺序变化）：每角度预旋转一次，位置循环复用。
        var cx = (int)Math.Round(c.X / scale);
        var cy = (int)Math.Round(c.Y / scale);
        var bestScore = -1.0;
        var bestX = 0.0;
        var bestY = 0.0;
        var bestAngle = 0.0;
        for (var angleDeg = c.Angle - previousStep; angleDeg <= c.Angle + previousStep + 1e-9; angleDeg += step)
        {
            var rot = RotatedFor(level, xs, ys, dxs, dys, angleDeg);
            for (var dy = -reach; dy <= reach; dy++)
            {
                for (var dx = -reach; dx <= reach; dx++)
                {
                    var score = ScoreAtRot(rot, dirX, dirY, mag, width, cx + dx, cy + dy, minMatched);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestX = cx + dx;
                        bestY = cy + dy;
                        bestAngle = angleDeg;
                    }
                }
            }
        }
        if (bestScore >= minScoreLevel)
        {
            // 本层最优位置 → 原始分辨率坐标（×2^level）
            found.Add((bestX * scale, bestY * scale, bestAngle, bestScore));
        }
        return found;
    }

    /// <summary>层评分阈值：原始层用 minScore；上层放�?0.1（量化损失补偿，快速模式粗步长放宽 0.2），最终结果仍由原始层把关�?</summary>
    private double LevelMinScore(int level) =>
        level > 0 ? Math.Max(0.01, _minScore - (_fastMode ? 0.2 : 0.1)) : _minScore;

    /// <summary>单候选评分：变换模板�?�?图像梯度方向比对。早停：剩余点全部命中也达不�?minMatched 时中断�?</summary>
    internal double ScoreAt(
        float[] xs, float[] ys, float[] dxs, float[] dys, int n,
        float[] dirX, float[] dirY, float[] mag, int width,
        int px, int py, double angleRad, int minMatched)
    {
        var cos = (float)Math.Cos(angleRad);
        var sin = (float)Math.Sin(angleRad);
        var matched = 0;
        for (var i = 0; i < n; i++)
        {
            var rx = xs[i] * cos - ys[i] * sin;
            var ry = xs[i] * sin + ys[i] * cos;
            var ix = px + (int)Math.Round(rx);
            var iy = py + (int)Math.Round(ry);
            if (ix >= 0 && iy >= 0 && ix < width)
            {
                var idx = iy * width + ix;
                if (idx < mag.Length)
                {
                    var m = mag[idx];
                    if (m >= _minContrastF)
                    {
                        var rdx = dxs[i] * cos - dys[i] * sin;
                        var rdy = dxs[i] * sin + dys[i] * cos;
                        var dot = rdx * dirX[idx] + rdy * dirY[idx];
                        if (_usePolarity ? dot >= _cosDirThreshold : Math.Abs(dot) >= _cosDirThreshold)
                        {
                            matched++;
                        }
                    }
                }
            }
            var remaining = n - i - 1;
            if (matched + remaining < minMatched)
            {
                return 0; // 早停：达不到最低命中数
            }
        }
        return n == 0 ? 0 : (double)matched / n;
    }

    /// <summary>单角度预旋转模板点：整数位置偏移 + 旋转后梯度方向——同一角度的所有位置复用，省逐点三角/旋转�?</summary>
    internal sealed class RotatedPoints
    {
        public int[] RiX = [];
        public int[] RiY = [];
        public float[] RdX = [];
        public float[] RdY = [];
        public int N;
    }

    /// <summary>
    /// 每角度预旋转一次。float 三角�?ScoreAt 原实现逐位一致（float 乘加顺序相同），保证完整模式数值不变�?    /// </summary>
    internal static RotatedPoints BuildRotated(float[] xs, float[] ys, float[] dxs, float[] dys, double angleDeg)
    {
        var cos = (float)Math.Cos(angleDeg * Math.PI / 180.0);
        var sin = (float)Math.Sin(angleDeg * Math.PI / 180.0);
        var n = xs.Length;
        var rot = new RotatedPoints
        {
            RiX = new int[n],
            RiY = new int[n],
            RdX = new float[n],
            RdY = new float[n],
            N = n,
        };
        for (var i = 0; i < n; i++)
        {
            var rx = xs[i] * cos - ys[i] * sin;
            var ry = xs[i] * sin + ys[i] * cos;
            rot.RiX[i] = (int)Math.Round(rx);
            rot.RiY[i] = (int)Math.Round(ry);
            rot.RdX[i] = dxs[i] * cos - dys[i] * sin;
            rot.RdY[i] = dxs[i] * sin + dys[i] * cos;
        }
        return rot;
    }

    /// <summary>只有模板全部落在搜索图内时，候选位置才有效；禁止用部分轮廓匹配 ROI 边缘。</summary>
    internal static bool IsTemplateInsideSearchImage(RotatedPoints rot, int width, int height, int px, int py)
    {
        for (var i = 0; i < rot.N; i++)
        {
            var x = px + rot.RiX[i];
            var y = py + rot.RiY[i];
            if (x < 0 || y < 0 || x >= width || y >= height) return false;
        }
        return true;
    }

    /// <summary>
    /// 单候选评分（预旋转版）：�?ScoreAt 同语义（早停/极�?对比度阈值），偏移与方向来自预旋转表�?    /// AVX2 可用时走 8 �?批向量化（逐点算术与串行完全相同、命中数一�?�?结果逐位一致；早停按批检查，
    /// 剪枝位置同样返回 0，不改变任何返回值）；否则标量�?    /// </summary>
    internal double ScoreAtRot(RotatedPoints rot, float[] dirX, float[] dirY, float[] mag, int width, int px, int py, int minMatched)
    {
        var n = rot.N;
        if (n == 0) return 0;
        var height = mag.Length / width;
        if (!IsTemplateInsideSearchImage(rot, width, height, px, py)) return 0;
        if (EnableAvx2Score && Avx2.IsSupported && n >= 16)
        {
            return ScoreAtRotAvx2(rot, dirX, dirY, mag, width, px, py, minMatched);
        }
        return ScoreAtRotScalar(rot, dirX, dirY, mag, width, px, py, minMatched);
    }

    internal double ScoreAtRotScalar(RotatedPoints rot, float[] dirX, float[] dirY, float[] mag, int width, int px, int py, int minMatched)
    {
        var matched = 0;
        var n = rot.N;
        for (var i = 0; i < n; i++)
        {
            var ix = px + rot.RiX[i];
            var iy = py + rot.RiY[i];
            if (ix >= 0 && iy >= 0 && ix < width)
            {
                var idx = iy * width + ix;
                if (idx < mag.Length)
                {
                    var m = mag[idx];
                    if (m >= _minContrastF)
                    {
                        var dot = rot.RdX[i] * dirX[idx] + rot.RdY[i] * dirY[idx];
                        if (_usePolarity ? dot >= _cosDirThresholdF : Math.Abs(dot) >= _cosDirThresholdF)
                        {
                            matched++;
                        }
                    }
                }
            }
            var remaining = n - i - 1;
            if (matched + remaining < minMatched)
            {
                return 0; // 早停：达不到最低命中数
            }
        }
        return n == 0 ? 0 : (double)matched / n;
    }

    internal unsafe double ScoreAtRotAvx2(RotatedPoints rot, float[] dirX, float[] dirY, float[] mag, int width, int px, int py, int minMatched)
    {
        var matched = 0;
        var n = rot.N;
        var vPx = Vector256.Create(px);
        var vPy = Vector256.Create(py);
        var vWidth = Vector256.Create(width);
        var vMagLen = Vector256.Create(mag.Length);
        var vMinusOne = Vector256.Create(-1);
        var vMinContrast = Vector256.Create((float)_minContrast);
        var vThreshold = Vector256.Create((float)_cosDirThreshold);
        var vAbsMask = Vector256.Create(unchecked((int)0x7fffffff)).AsSingle();
        var vZeroF = Vector256<float>.Zero;

        fixed (int* pRiX = rot.RiX)
        fixed (int* pRiY = rot.RiY)
        fixed (float* pRdX = rot.RdX)
        fixed (float* pRdY = rot.RdY)
        fixed (float* pDirX = dirX)
        fixed (float* pDirY = dirY)
        fixed (float* pMag = mag)
        {
            var i = 0;
            for (; i <= n - 8; i += 8)
            {
                var vix = Avx2.Add(vPx, Avx.LoadVector256(pRiX + i)).AsInt32();
                var viy = Avx2.Add(vPy, Avx.LoadVector256(pRiY + i)).AsInt32();
        
                var vIdx = Avx2.Add(Avx2.MultiplyLow(viy, Vector256.Create(width)), vix);
                // 有效位 = ix≥0 且 ix<width 且 iy≥0 且 idx<mag.Length
                var ok = Avx2.And(
                    Avx2.And(
                        Avx2.CompareGreaterThan(vix, vMinusOne),
                        Avx2.CompareGreaterThan(vWidth, vix)),
                    Avx2.CompareGreaterThan(viy, vMinusOne));
                // 索引钳制到数组内 + 非掩码 gather（规避 vgather 掩码访存的硬件差异）；
                // 无效 lane 的幅值用位与清零 → 对比度检查必然失败 → 不计命中（与标量跳过等价）
                var vIdxClamped = Avx2.Min(Avx2.Max(vIdx, Vector256<int>.Zero), Vector256.Create(mag.Length - 1));
                var gx = Avx2.GatherVector256(pDirX, vIdxClamped, 4);
                var gy = Avx2.GatherVector256(pDirY, vIdxClamped, 4);
                var mg = Avx2.And(Avx2.GatherVector256(pMag, vIdxClamped, 4), ok.AsSingle());
                var validC = Avx.CompareGreaterThanOrEqual(mg, vMinContrast).AsSingle();
                var rdx = Avx.LoadVector256(pRdX + i);
                var rdy = Avx.LoadVector256(pRdY + i);
                var dot = Avx.Add(Avx.Multiply(rdx, gx), Avx.Multiply(rdy, gy));
                Vector256<float> pass;
                if (_usePolarity)
                {
                    pass = Avx.CompareGreaterThanOrEqual(dot, vThreshold).AsSingle();
                }
                else
                {
                    pass = Avx.CompareGreaterThanOrEqual(Avx.And(dot, vAbsMask), vThreshold).AsSingle();
                }
                matched += BitOperations.PopCount((uint)Avx.MoveMask(Avx2.And(validC, pass)));
                var remaining = n - i - 8;
                if (matched + remaining < minMatched)
                {
                    return 0; // 批量早停（语义与逐点一致：剪枝位置返回 0）
                }
            }
    
        // 尾部标量（与串行逐点同语义，含逐点早停检查）
            for (; i < n; i++)
            {
                var ix = px + rot.RiX[i];
                var iy = py + rot.RiY[i];
                if (ix >= 0 && iy >= 0 && ix < width)
                {
                    var idx = iy * width + ix;
                    if (idx < mag.Length)
                    {
                        var m = mag[idx];
                        if (m >= _minContrast)
                        {
                            var dot = rot.RdX[i] * dirX[idx] + rot.RdY[i] * dirY[idx];
                            if (_usePolarity ? dot >= _cosDirThresholdF : Math.Abs(dot) >= _cosDirThresholdF)
                            {
                                matched++;
                            }
                        }
                    }
                }
                var remaining = n - i - 1;
                if (matched + remaining < minMatched)
                {
                    return 0;
                }
            }
        }
        return (double)matched / n;
    }

    /// <summary>
    /// 连续评分（亚像素细化专用）：模板点旋转后保持浮点坐标，图像梯度方�?幅值做双线性插�?    /// �? 邻域按幅值加权，方向插值前按最大幅值邻居做 180° 符号对齐、插值后重新归一化）�?    /// 消除最近邻取整的评分台阶。出�?插值幅值低�?minContrast/方向退化为�?均按失配计；不做早停�?    /// </summary>
    internal double ScoreAtSub(
        float[] xs, float[] ys, float[] dxs, float[] dys, int n,
        float[] dirX, float[] dirY, float[] mag, int width, int height,
        double px, double py, double angleRad)
    {
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);
        var matched = 0;
        for (var i = 0; i < n; i++)
        {
            var rx = px + xs[i] * cos - ys[i] * sin;
            var ry = py + xs[i] * sin + ys[i] * cos;
            var x0 = (int)Math.Floor(rx);
            var y0 = (int)Math.Floor(ry);
            if (x0 < 0 || y0 < 0 || x0 + 1 >= width || y0 + 1 >= height)
            {
                continue; // 出界失配
            }

            var fx = rx - x0;
            var fy = ry - y0;
            var i00 = y0 * width + x0;
            var i10 = i00 + 1;
            var i01 = i00 + width;
            var i11 = i01 + 1;

            var m = mag[i00] * (1 - fx) * (1 - fy) + mag[i10] * fx * (1 - fy)
                  + mag[i01] * (1 - fx) * fy + mag[i11] * fx * fy;
            if (m < _minContrast)
            {
                continue;
            }

    
        // 方向符号对齐：以 4 邻域中幅值最大者为参考，反向（边缘对侧）邻居翻转后参与插值，防相互抵消
            var refIdx = i00;
            if (mag[i10] > mag[refIdx]) refIdx = i10;
            if (mag[i01] > mag[refIdx]) refIdx = i01;
            if (mag[i11] > mag[refIdx]) refIdx = i11;
            var ax = dirX[refIdx];
            var ay = dirY[refIdx];

            float AlignedX(int idx)
            {
                var vx = dirX[idx];
                var vy = dirY[idx];
                if (ax * vx + ay * vy < 0)
                {
                    vx = -vx;
                    vy = -vy;
                }
                return vx;
            }

            float AlignedY(int idx)
            {
                var vx = dirX[idx];
                var vy = dirY[idx];
                if (ax * vx + ay * vy < 0)
                {
                    vx = -vx;
                    vy = -vy;
                }
                return vy;
            }

            var gx = AlignedX(i00) * (1 - fx) * (1 - fy) + AlignedX(i10) * fx * (1 - fy)
                   + AlignedX(i01) * (1 - fx) * fy + AlignedX(i11) * fx * fy;
            var gy = AlignedY(i00) * (1 - fx) * (1 - fy) + AlignedY(i10) * fx * (1 - fy)
                   + AlignedY(i01) * (1 - fx) * fy + AlignedY(i11) * fx * fy;
            var gm = Math.Sqrt(gx * gx + gy * gy);
            if (gm < 1e-6)
            {
                continue; // 方向退化（对侧边缘均匀混合）：按失配计
            }

            var rdx = dxs[i] * cos - dys[i] * sin;
            var rdy = dxs[i] * sin + dys[i] * cos;
            var dot = (rdx * gx + rdy * gy) / gm;
            if (_usePolarity ? dot >= _cosDirThreshold : Math.Abs(dot) >= _cosDirThreshold)
            {
                matched++;
            }
        }

        return n == 0 ? 0 : (double)matched / n;
    }

    /// <summary>
    /// 该层实际参与评分的点集：完整模式 = 模板全量点；快速模�?= 均匀抽稀�?≤FastMaxPointsPerLevel
    /// （按固定步长取样保持空间分布；缓存复用，同一实例多次 Find 结果一致）。NMS 半径等几何量仍用全量点�?    /// 超出模板层数的层（快速模式合成粗层）由最深层坐标逐级减半得到（方向不变，pyrDown 链近似）�?    /// </summary>
    private IReadOnlyList<TemplatePoint> PointsFor(int level)
    {
        List<TemplatePoint> all;
        if (level < _template.Levels)
        {
            all = _template.LevelPoints[level];
        }
        else
        {
            _synthPoints ??= new Dictionary<int, List<TemplatePoint>>();
            if (!_synthPoints.TryGetValue(level, out all!))
            {
                all = _template.LevelPoints[^1];
                for (var h = _template.Levels - 1; h < level; h++)
                {
                    all = all.Select(p => new TemplatePoint(p.X / 2f, p.Y / 2f, p.Dx, p.Dy)).ToList();
                }
                _synthPoints[level] = all;
            }
        }
        if (!_fastMode || all.Count <= FastMaxPointsPerLevel)
        {
            return all;
        }
        _fastPoints ??= new List<TemplatePoint>[_template.Levels];
        if (level >= _fastPoints.Length)
        {
            Array.Resize(ref _fastPoints, level + 1);
        }
        var cached = _fastPoints[level];
        if (cached != null) return cached;
        var sub = SelectSpatially(all, FastMaxPointsPerLevel);
        _fastPoints[level] = sub;
        return sub;
    }

    private static List<TemplatePoint> SelectSpatially(IReadOnlyList<TemplatePoint> points, int limit)
    {
        if (points.Count <= limit) return points.ToList();
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        var grid = Math.Max(1, (int)Math.Sqrt(limit));
        var selected = new List<TemplatePoint>(limit);
        var occupied = new HashSet<(int X, int Y)>();
        for (var i = 0; i < points.Count && selected.Count < limit; i++)
        {
            var p = points[i];
            var gx = Math.Clamp((int)((p.X - minX) / Math.Max(1e-6f, maxX - minX) * grid), 0, grid - 1);
            var gy = Math.Clamp((int)((p.Y - minY) / Math.Max(1e-6f, maxY - minY) * grid), 0, grid - 1);
            if (occupied.Add((gx, gy))) selected.Add(p);
        }
        if (selected.Count < limit)
        {
            var selectedPoints = selected.ToHashSet();
            foreach (var p in points)
            {
                if (selectedPoints.Add(p)) selected.Add(p);
                if (selected.Count >= limit) break;
            }
        }
        return selected;
    }

    private PointArrays PointArraysFor(int level)
    {
        _pointArrays ??= new PointArrays[Math.Max(level + 1, _template.Levels)];
        if (level >= _pointArrays.Length)
        {
            Array.Resize(ref _pointArrays, level + 1);
        }

        var cached = _pointArrays[level];
        if (cached != null) return cached;

        var points = PointsFor(level);
        var arrays = new PointArrays(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            arrays.Xs[i] = points[i].X;
            arrays.Ys[i] = points[i].Y;
            arrays.Dxs[i] = points[i].Dx;
            arrays.Dys[i] = points[i].Dy;
        }
        _pointArrays[level] = arrays;
        return arrays;
    }

    private RotatedPoints RotatedFor(int level, float[] xs, float[] ys, float[] dxs, float[] dys, double angleDeg)
    {
        _rotatedCache ??= new ConcurrentDictionary<(int Level, double Angle), RotatedPoints>();
        var key = (level, angleDeg);
        return _rotatedCache.GetOrAdd(key, _ => BuildRotated(xs, ys, dxs, dys, angleDeg));
    }

    private sealed class PointArrays
    {
        public PointArrays(int count)
        {
            Xs = new float[count];
            Ys = new float[count];
            Dxs = new float[count];
            Dys = new float[count];
        }

        public float[] Xs { get; }
        public float[] Ys { get; }
        public float[] Dxs { get; }
        public float[] Dys { get; }
    }

    internal (float[] Xs, float[] Ys, float[] Dxs, float[] Dys) SplitPoints(IReadOnlyList<TemplatePoint> points)
    {
        var xs = new float[points.Count];
        var ys = new float[points.Count];
        var dxs = new float[points.Count];
        var dys = new float[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            xs[i] = points[i].X;
            ys[i] = points[i].Y;
            dxs[i] = points[i].Dx;
            dys[i] = points[i].Dy;
        }
        return (xs, ys, dxs, dys);
    }

    /// <summary>单层灰度�?�?归一化梯度方�?+ 幅值（平滑 �?Sobel）。逐像素独立计算，大图并行化�?</summary>
    internal static (float[] DirX, float[] DirY, float[] Mag) ExtractDirections(Mat gray, double sigma)
    {
        using var blurred = sigma > 0.01 ? new Mat() : null;
        if (blurred != null)
        {
            Cv2.GaussianBlur(gray, blurred, new Size(0, 0), sigma);
        }
        var src = blurred ?? gray;
        using var gx = new Mat();
        using var gy = new Mat();

        // 两个 Sobel 读同一 src、互不依赖——并发执行（�?Sobel 自身确定性，结果与串行一致）
        var gxTask = Task.Run(() => Cv2.Sobel(src, gx, MatType.CV_32F, 1, 0, 3));
        Cv2.Sobel(src, gy, MatType.CV_32F, 0, 1, 3);
        gxTask.Wait();
        if (!gx.GetArray(out float[] gxData) || !gy.GetArray(out float[] gyData))
        {
            return ([], [], []);
        }
        var count = gxData.Length;
        var dirX = new float[count];
        var dirY = new float[count];
        var mag = new float[count];

        // 逐像素独立（幅值/方向只依赖同像素 gx/gy），按行分块并行，结果与串行一致
        if (count >= 1 << 17)
        {
            var rows = gray.Rows;
            var cols = gray.Cols;
            Parallel.For(0, rows, row =>
            {
                var start = row * cols;
                var end = start + cols;
                for (var i = start; i < end; i++)
                {
                    ComputeDirection(gxData[i], gyData[i], dirX, dirY, mag, i);
                }
            });
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                ComputeDirection(gxData[i], gyData[i], dirX, dirY, mag, i);
            }
        }
        return (dirX, dirY, mag);
    }

    private static void ComputeDirection(float x, float y, float[] dirX, float[] dirY, float[] mag, int i)
    {
        var m = MathF.Sqrt(x * x + y * y);
        mag[i] = m;
        if (m > 1e-6f)
        {
            dirX[i] = x / m;
            dirY[i] = y / m;
        }
    }

    /// <summary>搜索图需要的层数：最粗层最短边 ≥~150px，且不超过模板层数�?</summary>
    private static int LevelsForImage(int minSide, int templateLevels)
    {
        var l = 1;
        while (l < MaxLevels && l < templateLevels && (minSide >> l) >= 150)
        {
            l++;
        }
        return Math.Max(1, l);
    }

    private double TemplateRadius()
    {
        var radius = 10.0;
        if (_template.LevelPoints.Count == 0) return radius;
        foreach (var p in _template.LevelPoints[0])
        {
            radius = Math.Max(radius, Math.Sqrt(p.X * p.X + p.Y * p.Y));
        }
        return radius;
    }

    /// <summary>两圆�?IoU（NMS 用，一般公式）�?</summary>
    internal static double CircleIou(double x1, double y1, double r1, double x2, double y2, double r2)
    {
        var d = Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
        if (d >= r1 + r2) return 0;
        if (d <= Math.Abs(r1 - r2)) return 1;
        var a1 = r1 * r1 * Math.Acos(Math.Clamp((d * d + r1 * r1 - r2 * r2) / (2 * d * r1), -1, 1));
        var a2 = r2 * r2 * Math.Acos(Math.Clamp((d * d + r2 * r2 - r1 * r1) / (2 * d * r2), -1, 1));
        var tri = 0.5 * Math.Sqrt(Math.Max(0, (-d + r1 + r2) * (d + r1 - r2) * (d - r1 + r2) * (d + r1 + r2)));
        var area = a1 + a2 - tri;
        var union = Math.PI * r1 * r1 + Math.PI * r2 * r2 - area;
        return union <= 0 ? 0 : area / union;
    }
}




