using System.Globalization;

namespace VisionInspection.Detection;

/// <summary>边缘极性（VM 卡尺找线对齐；正方向 = 沿区域法线正向，即从长轴一侧指向另一侧）。</summary>
public static class LineFindPolarity
{
    public const string DarkToBright = "黑到白";
    public const string BrightToDark = "白到黑";
    public const string Any = "任意";
}

/// <summary>边缘类型：每个卡尺从过阈值的候选边缘中选哪一个。</summary>
public static class LineFindEdgeType
{
    public const string Strongest = "最强";
    public const string First = "第一条";
    public const string Last = "最后一条";
}

/// <summary>拟合方式：鲁棒拟合（默认，VM FitFun=Huber 同款：最小二乘初拟合 + Huber IRLS 降权离群点）/ 最小二乘（旧行为保留）。圆查找复用。</summary>
public static class LineFindFitMethod
{
    public const string Lsq = "最小二乘";
    public const string Robust = "鲁棒拟合";
}

/// <summary>搜索区域（像素，源图坐标）：长轴 = 预期直线方向，法线 = 卡尺搜索方向。</summary>
public readonly record struct LineSearchRegion(double Cx, double Cy, double HalfLen, double HalfWidth, double AxisAngleRad);

/// <summary>连续帧直线跟踪先验（坐标与角度均为当前搜索图像坐标）。</summary>
public readonly record struct LineFindPrior(double X, double Y, double AngleDeg);

/// <summary>直线查找参数（VM 直线查找对齐）。</summary>
public sealed record LineFindParams
{
    public string Polarity { get; init; } = LineFindPolarity.DarkToBright;
    public double EdgeThreshold { get; init; } = 30;
    /// <summary>1=不滤波，2/3/5/7=二项式高斯核。</summary>
    public int FilterSize { get; init; } = 3;
    public int CaliperNum { get; init; } = 16;
    public double SearchLength { get; init; } = 32;
    public string EdgeType { get; init; } = LineFindEdgeType.Strongest;
    public int RejectNum { get; init; } = 0;
    public double RejectDist { get; init; } = 3;
    public string FitMethod { get; init; } = LineFindFitMethod.Lsq;
}

/// <summary>单个卡尺找到的边缘点。</summary>
public sealed record CaliperEdge(int Index, double X, double Y, double Contrast);

/// <summary>卡尺剖面扫描统计（未找到边缘时的诊断来源）：Find 循环内逐剖面累积。圆查找复用。</summary>
public sealed class CaliperScanStats
{
    /// <summary>实际扫描的剖面数（卡尺越出图像/剖面过短时少于卡尺数）。</summary>
    public int Profiles { get; internal set; }

    /// <summary>剖面最大正向梯度（黑→白：沿采样正向亮度上升）。</summary>
    public double MaxPos { get; internal set; }

    /// <summary>剖面最大负向梯度幅值（白→黑）。</summary>
    public double MaxNeg { get; internal set; }

    /// <summary>最大梯度（不分方向）。</summary>
    public double MaxAbs => Math.Max(MaxPos, MaxNeg);
}

/// <summary>直线查找结果：Found=false 时 Error 给可操作原因；Found=true 时直线几何/得分有效。</summary>
public sealed record LineFindResult
{
    public bool Found { get; init; }
    public string? Error { get; init; }
    /// <summary>全部有效卡尺点（含被剔除的离群点）。</summary>
    public List<CaliperEdge> Edges { get; init; } = [];
    /// <summary>参与最终拟合的内点（沿线方向升序）。</summary>
    public List<CaliperEdge> Inliers { get; init; } = [];
    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double X2 { get; init; }
    public double Y2 { get; init; }
    /// <summary>直线角度（度，图像坐标系 y 向下，归一到 (-90, 90]）。</summary>
    public double AngleDeg { get; init; }
    /// <summary>得分 = 内点数 / 卡尺数（0~1，VM 同款有效卡尺比例语义）。</summary>
    public double Score { get; init; }
    public double MeanContrast { get; init; }

    public double MidX => (X1 + X2) / 2;
    public double MidY => (Y1 + Y2) / 2;
}

/// <summary>
/// 直线查找算法核（纯逻辑，可单测；VM 卡尺找线对齐）：
/// 卡尺沿区域长轴均布 → 法线方向双线性采样灰度剖面 → 1D 二项式高斯平滑 → 中心差分梯度 →
/// 阈值+极性+边缘类型选峰 → 抛物线亚像素 → 全最小二乘拟合（2×2 协方差主方向）→ 剔除点数/剔除距离迭代剔离群。
/// 典型 1024×768 图 + 600×80 区域全链 &lt;1ms（预算 5ms）。
/// </summary>
public static class LineFindCore
{
    private sealed record ProfileCandidateSet(
        int CaliperIndex,
        double Cx,
        double Cy,
        double NrmX,
        double NrmY,
        List<(double T, double Contrast)> Candidates);

    private static readonly double[] Kernel5 = [1, 4, 6, 4, 1];
    private static readonly double[] Kernel7 = [1, 6, 15, 20, 15, 6, 1];
    private static readonly double[] Kernel9 = [1, 8, 28, 56, 70, 56, 28, 8, 1];
    private static readonly double[] Kernel3 = [1, 2, 1];

    public static LineFindResult Find(byte[] gray, int width, int height, LineSearchRegion region, LineFindParams p, LineFindPrior? prior = null)
    {
        if (width <= 0 || height <= 0 || gray.Length < width * height)
        {
            return new LineFindResult { Found = false, Error = "灰度缓冲与图像尺寸不一致" };
        }

        var num = Math.Clamp(p.CaliperNum, 2, 4096);
        var axisX = Math.Cos(region.AxisAngleRad);
        var axisY = Math.Sin(region.AxisAngleRad);
        var nrmX = -axisY;
        var nrmY = axisX;

        // 法线采样半宽（各卡尺相同）：0 表示扫描整个 ROI 短边；正值才是显式搜索长度。
        // VM 的 RegionWidth 是卡尺宽度，不是这里的法线搜索范围，不能把它误换成 5px 搜索窗。
        var searchHalf = p.SearchLength <= 0 ? region.HalfWidth : Math.Max(1.0, p.SearchLength / 2);
        var half = Math.Max(2.0, Math.Min(searchHalf, region.HalfWidth));
        var clampedByRegion = p.SearchLength > 0 && searchHalf > region.HalfWidth + 0.5;
        var searchCult = CultureInfo.InvariantCulture;
        var rangeText = p.SearchLength <= 0
            ? $"采样半宽 ±{half.ToString("0", searchCult)}px（沿 ROI 短边全范围）"
            : clampedByRegion
            ? $"采样半宽 ±{half.ToString("0", searchCult)}px（搜索长度 {p.SearchLength.ToString("0", searchCult)} 被 ROI 短边宽度 {(region.HalfWidth * 2).ToString("0", searchCult)} 钳制）"
            : $"采样半宽 ±{half.ToString("0", searchCult)}px";
        var stats = new CaliperScanStats();

        var edges = new List<CaliperEdge>(num);
        var profileCandidates = new List<ProfileCandidateSet>(num);
        for (var i = 0; i < num; i++)
        {
            // 卡尺中心沿长轴均布（含两端）
            var s = 2 * region.HalfLen * i / (num - 1) - region.HalfLen;
            var cx = region.Cx + s * axisX;
            var cy = region.Cy + s * axisY;

            // 法线采样范围：采样半宽 ∩ 图像边界（VM 语义：卡尺不越出区域）
            var tLo = -half;
            var tHi = half;
            if (!ClampRange(cx, nrmX, width, ref tLo, ref tHi) || !ClampRange(cy, nrmY, height, ref tLo, ref tHi))
            {
                continue;
            }

            var profile = SampleProfile(gray, width, height, cx, cy, nrmX, nrmY, tLo, tHi);
            if (profile is null) continue;
            if (p.FilterSize >= 3) SmoothBinomial(profile, p.FilterSize);

            if (string.Equals(p.FitMethod, LineFindFitMethod.Robust, StringComparison.Ordinal))
            {
                var candidates = FindEdgeCandidates(profile, tLo, p.EdgeThreshold, p.Polarity);
                if (candidates.Count > 0)
                {
                    profileCandidates.Add(new ProfileCandidateSet(i, cx, cy, nrmX, nrmY, candidates));
                }
            }

            var edge = FindEdge(profile, tLo, p.EdgeThreshold, p.Polarity, p.EdgeType, stats);
            if (edge is not null)
            {
                edges.Add(new CaliperEdge(i, cx + edge.Value.T * nrmX, cy + edge.Value.T * nrmY, edge.Value.Contrast));
            }
        }

        if (edges.Count < 2)
        {
            return new LineFindResult
            {
                Found = false,
                Edges = edges,
                Error = BuildNotFoundMessage(edges.Count, 2, num, rangeText, p.EdgeThreshold, p.Polarity, stats),
            };
        }

        var pts = edges.Select(e => (e.X, e.Y)).ToList();
        var active = Enumerable.Range(0, pts.Count).ToList();
        var theta = 0.0;
        var lineCx = 0.0;
        var lineCy = 0.0;
        var removed = 0;
        if (string.Equals(p.FitMethod, LineFindFitMethod.Robust, StringComparison.Ordinal))
        {
            // Huber IRLS needs a usable initial line. A TLS fit can be pulled
            // toward a second strong edge before Huber gets a chance to help.
            // Select one continuous candidate per caliper before fitting.
            var candidateActive = SelectConsensusCandidates(
                edges,
                profileCandidates,
                Math.Max(1.5, Math.Min(5.0, p.RejectDist > 0 ? p.RejectDist : 3.0)),
                p.EdgeType,
                region.AxisAngleRad,
                prior);
            if (candidateActive is { Count: >= 2 })
            {
                active = candidateActive;
                pts = edges.Select(e => (e.X, e.Y)).ToList();
                (theta, lineCx, lineCy) = FitLine(pts, active);
            }
            else
            {
                var seed = FindConsensusLine(
                    pts,
                    Math.Max(1.5, Math.Min(5.0, p.RejectDist > 0 ? p.RejectDist : 3.0)),
                    region.AxisAngleRad);
                if (seed is not null)
                {
                    (theta, lineCx, lineCy, active) = seed.Value;
                }
            }
        }

        while (true)
        {
            (theta, lineCx, lineCy) = FitLine(pts, active);
            if (removed >= Math.Max(0, p.RejectNum) || active.Count <= 2)
            {
                break;
            }

            var cos = Math.Cos(theta);
            var sin = Math.Sin(theta);
            var worstIdx = -1;
            var worstRes = 0.0;
            foreach (var i in active)
            {
                var dx = pts[i].Item1 - lineCx;
                var dy = pts[i].Item2 - lineCy;
                var res = Math.Abs(-sin * dx + cos * dy); // 垂距 = |叉积|
                if (res > worstRes)
                {
                    worstRes = res;
                    worstIdx = i;
                }
            }

            if (worstIdx < 0 || worstRes <= Math.Max(0, p.RejectDist))
            {
                break;
            }

            active.Remove(worstIdx);
            removed++;
        }

        // 鲁棒拟合（VM FitFun=Huber 同款：LLS 初拟合 + Huber IRLS 迭代降权精修，线性影响区不丢点只降权；
        // 全等权/退化时保持当前拟合）
        if (string.Equals(p.FitMethod, LineFindFitMethod.Robust, StringComparison.Ordinal))
        {
            for (var iter = 0; iter < 8; iter++)
            {
                var residuals = new List<double>(active.Count);
                var rcos = Math.Cos(theta);
                var rsin = Math.Sin(theta);
                foreach (var i in active)
                {
                    var dx = pts[i].Item1 - lineCx;
                    var dy = pts[i].Item2 - lineCy;
                    residuals.Add(Math.Abs(-rsin * dx + rcos * dy));
                }
                var weights = HuberWeights(residuals);
                if (weights.Sum() < 1e-9) break;
                var (nt, ncx, ncy) = FitLine(pts, active, weights);
                var converged = Math.Abs(nt - theta) < 1e-9 && Math.Abs(ncx - lineCx) < 1e-9 && Math.Abs(ncy - lineCy) < 1e-9;
                (theta, lineCx, lineCy) = (nt, ncx, ncy);
                if (converged) break;
            }
        }

        var dirX = Math.Cos(theta);
        var dirY = Math.Sin(theta);
        // Endpoint semantics are the search ROI's long-axis span, not the
        // first/last surviving caliper. Missing edge points must not move the
        // reported midpoint or the loc_* pose contract.
        var roiCenterS = (region.Cx - lineCx) * dirX + (region.Cy - lineCy) * dirY;
        var sMin = roiCenterS - region.HalfLen;
        var sMax = roiCenterS + region.HalfLen;

        var inliers = active
            .Select(i => edges[i])
            .OrderBy(e => (e.X - lineCx) * dirX + (e.Y - lineCy) * dirY)
            .ToList();
        return new LineFindResult
        {
            Found = true,
            Edges = edges,
            Inliers = inliers,
            X1 = lineCx + sMin * dirX,
            Y1 = lineCy + sMin * dirY,
            X2 = lineCx + sMax * dirX,
            Y2 = lineCy + sMax * dirY,
            AngleDeg = NormalizeLineAngle(theta * 180.0 / Math.PI),
            Score = (double)active.Count / num,
            MeanContrast = active.Average(i => edges[i].Contrast),
        };
    }

    /// <summary>直线角度归一到 (-90, 90]（VM 角度归一化同款）。</summary>
    internal static double NormalizeLineAngle(double deg)
    {
        var a = deg % 180.0;
        if (a > 90) a -= 180;
        else if (a <= -90) a += 180;
        return a;
    }

    /// <summary>未找到时的完整诊断信息（直线/圆查找共用）：前缀兼容既有文案 + 采样范围/阈值/剖面最大梯度 + 可操作建议。</summary>
    internal static string BuildNotFoundMessage(int found, int minRequired, int calipers, string rangeText, double edgeThreshold, string polarity, CaliperScanStats stats)
    {
        var cult = CultureInfo.InvariantCulture;
        return $"有效边缘点不足（{found}/{minRequired}，共 {calipers} 卡尺）｜{rangeText}｜边缘阈值 {edgeThreshold.ToString("0", cult)}" +
               $"｜剖面最大梯度 黑→白 {stats.MaxPos.ToString("F1", cult)} / 白→黑 {stats.MaxNeg.ToString("F1", cult)}｜{NotFoundAdvice(edgeThreshold, polarity, stats)}";
    }

    /// <summary>未找到时的可操作建议（直线/圆查找共用）：依据剖面梯度统计区分 阈值过高/极性相反/区域未覆盖边缘 三类原因。</summary>
    internal static string NotFoundAdvice(double threshold, string polarity, CaliperScanStats stats)
    {
        if (stats.Profiles == 0)
        {
            return "采样剖面全部越出图像边界：请检查搜索区域位置/大小";
        }
        var anyPolarity = string.Equals(polarity, LineFindPolarity.Any, StringComparison.Ordinal);
        var wantPositive = !string.Equals(polarity, LineFindPolarity.BrightToDark, StringComparison.Ordinal);
        var matching = anyPolarity ? stats.MaxAbs : (wantPositive ? stats.MaxPos : stats.MaxNeg);
        if (matching >= threshold)
        {
            return "部分卡尺找到边缘：请检查搜索区域是否完整覆盖边缘（调整位置/加大范围），或降低「边缘阈值」";
        }
        if (stats.MaxAbs >= threshold)
        {
            return wantPositive
                ? "过阈边缘方向与极性不符（白→黑）：请把「边缘极性」改为「白到黑」或「任意」"
                : "过阈边缘方向与极性不符（黑→白）：请把「边缘极性」改为「黑到白」或「任意」";
        }
        if (stats.MaxAbs < 1)
        {
            return "剖面几乎无灰度变化：请检查搜索区域是否落在平坦区域，或加大搜索范围（边缘可能在采样窗之外）";
        }
        return $"剖面最大梯度 {stats.MaxAbs.ToString("F1", CultureInfo.InvariantCulture)} 低于阈值：请降低「边缘阈值」（建议 ≤{Math.Floor(stats.MaxAbs).ToString("0", CultureInfo.InvariantCulture)}），或加大搜索范围（边缘可能在采样窗之外）";
    }

    /// <summary>全最小二乘（总体最小二乘）：加权质心 + 加权 2×2 协方差主方向（weights 为 null 时退化为等权，行为与旧版逐位一致）。active 为参与拟合的点索引。</summary>
    internal static (double AngleRad, double Cx, double Cy) FitLine(List<(double X, double Y)> pts, List<int> active, IReadOnlyList<double>? weights = null)
    {
        double sw = 0, mx = 0, my = 0;
        for (var k = 0; k < active.Count; k++)
        {
            var w = weights is null ? 1.0 : weights[k];
            sw += w;
            mx += pts[active[k]].X * w;
            my += pts[active[k]].Y * w;
        }
        mx /= sw;
        my /= sw;
        double sxx = 0, syy = 0, sxy = 0;
        for (var k = 0; k < active.Count; k++)
        {
            var w = weights is null ? 1.0 : weights[k];
            var dx = pts[active[k]].X - mx;
            var dy = pts[active[k]].Y - my;
            sxx += w * dx * dx;
            syy += w * dy * dy;
            sxy += w * dx * dy;
        }
        var theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        return (theta, mx, my);
    }

    /// <summary>
    /// 从候选边缘点中寻找最大共识直线，作为鲁棒拟合初值。
    /// 卡尺数量通常很小，枚举点对比随机 RANSAC 更稳定，也能保持现场结果可复现。
    /// </summary>
    internal static (double AngleRad, double Cx, double Cy, List<int> Active)? FindConsensusLine(
        List<(double X, double Y)> pts, double tolerance, double? axisAngleRad = null)
    {
        if (pts.Count < 2) return null;

        var best = default((double AngleRad, double Cx, double Cy, List<int> Active)?);
        var bestResidual = double.MaxValue;
        for (var a = 0; a < pts.Count - 1; a++)
        {
            for (var b = a + 1; b < pts.Count; b++)
            {
                var dx = pts[b].X - pts[a].X;
                var dy = pts[b].Y - pts[a].Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-6) continue;

                var cos = dx / length;
                var sin = dy / length;
                if (axisAngleRad is { } axis)
                {
                    var lineAngle = Math.Atan2(sin, cos) * 180.0 / Math.PI;
                    var axisAngle = axis * 180.0 / Math.PI;
                    if (Math.Abs(NormalizeLineAngle(lineAngle - axisAngle)) > 3.0) continue;
                }
                var active = new List<int>(pts.Count);
                var residual = 0.0;
                for (var i = 0; i < pts.Count; i++)
                {
                    var px = pts[i].X - pts[a].X;
                    var py = pts[i].Y - pts[a].Y;
                    var distance = Math.Abs(-sin * px + cos * py);
                    if (distance <= tolerance)
                    {
                        active.Add(i);
                        residual += distance * distance;
                    }
                }

                if (best is null || active.Count > best.Value.Active.Count ||
                    (active.Count == best.Value.Active.Count && residual < bestResidual))
                {
                    var (angle, cx, cy) = FitLine(pts, active);
                    best = (angle, cx, cy, active);
                    bestResidual = residual;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// 在所有卡尺的候选边缘中寻找最大共识直线，并把每个卡尺切换到该直线附近的候选。
    /// 这一步解决“单个卡尺选到另一条强边，整体仍拟合成平行线”的问题。
    /// </summary>
    private static List<int>? SelectConsensusCandidates(
        List<CaliperEdge> edges,
        List<ProfileCandidateSet> profiles,
        double tolerance,
        string edgeType,
        double axisAngleRad,
        LineFindPrior? prior)
    {
        if (profiles.Count < 2) return null;

        var edgeIndex = edges
            .Select((edge, index) => (edge.Index, index))
            .ToDictionary(item => item.Index, item => item.index);
        var bestActive = new List<int>();
        var bestResidual = double.MaxValue;
        var bestContrast = double.MinValue;
        var bestPosition = edgeType == LineFindEdgeType.Last ? double.MinValue : double.MaxValue;
        var bestCoverage = 0;
        var bestChoices = new Dictionary<int, (double T, double Contrast)>();
        var minimumCoverage = Math.Max(2, (int)Math.Ceiling(profiles.Count * 0.5));
        var priorAngle = prior?.AngleDeg ?? 0;
        var usePrior = prior is { } && Math.Abs(NormalizeLineAngle(priorAngle - axisAngleRad * 180.0 / Math.PI)) <= 5;
        var bestPriorDistance = double.MaxValue;

        for (var a = 0; a < profiles.Count - 1; a++)
        {
            for (var b = a + 1; b < profiles.Count; b++)
            {
                foreach (var ca in profiles[a].Candidates)
                {
                    var ax = profiles[a].Cx + ca.T * profiles[a].NrmX;
                    var ay = profiles[a].Cy + ca.T * profiles[a].NrmY;
                    foreach (var cb in profiles[b].Candidates)
                    {
                        var bx = profiles[b].Cx + cb.T * profiles[b].NrmX;
                        var by = profiles[b].Cy + cb.T * profiles[b].NrmY;
                        var dx = bx - ax;
                        var dy = by - ay;
                        var length = Math.Sqrt(dx * dx + dy * dy);
                        if (length < 1e-6) continue;

                        var cos = dx / length;
                        var sin = dy / length;
                        var lineAngle = Math.Atan2(sin, cos) * 180.0 / Math.PI;
                        var axisAngle = axisAngleRad * 180.0 / Math.PI;
                        if (Math.Abs(NormalizeLineAngle(lineAngle - axisAngle)) > 3.0) continue;
                        var choices = new Dictionary<int, (double T, double Contrast)>();
                        var residual = 0.0;
                        var contrast = 0.0;
                        var position = 0.0;
                        foreach (var profile in profiles)
                        {
                            var best = default((double T, double Contrast));
                            var bestDistance = double.MaxValue;
                            foreach (var candidate in profile.Candidates)
                            {
                                var px = profile.Cx + candidate.T * profile.NrmX;
                                var py = profile.Cy + candidate.T * profile.NrmY;
                                var distance = Math.Abs(-sin * (px - ax) + cos * (py - ay));
                                if (distance < bestDistance)
                                {
                                    bestDistance = distance;
                                    best = candidate;
                                }
                            }
                            if (bestDistance <= tolerance)
                            {
                                choices[profile.CaliperIndex] = best;
                                residual += bestDistance * bestDistance;
                                contrast += best.Contrast;
                                position += best.T;
                            }
                        }

                        if (choices.Count < minimumCoverage) continue;

                        var positionPreferred = edgeType switch
                        {
                            LineFindEdgeType.First => position < bestPosition,
                            LineFindEdgeType.Last => position > bestPosition,
                            _ => contrast > bestContrast,
                        };
                        var meanContrast = contrast / choices.Count;
                        var priorDistance = double.MaxValue;
                        if (usePrior)
                        {
                            priorDistance = Math.Abs(-sin * (prior!.Value.X - ax) + cos * (prior.Value.Y - ay));
                        }
                        var better = edgeType == LineFindEdgeType.Strongest
                            ? (usePrior && priorDistance <= 50
                                ? priorDistance < bestPriorDistance - 1e-9 ||
                                  (Math.Abs(priorDistance - bestPriorDistance) <= 1e-9 && meanContrast > bestContrast)
                                : bestPriorDistance == double.MaxValue && meanContrast > bestContrast + 1e-9) ||
                              (Math.Abs(meanContrast - bestContrast) <= 1e-9 && choices.Count > bestCoverage) ||
                              (Math.Abs(meanContrast - bestContrast) <= 1e-9 && choices.Count == bestCoverage && residual < bestResidual)
                            : choices.Count > bestActive.Count ||
                              (choices.Count == bestActive.Count && positionPreferred);
                        if (better)
                        {
                            bestChoices = choices;
                            bestActive = choices.Keys
                                .Where(edgeIndex.ContainsKey)
                                .Select(index => edgeIndex[index])
                                .ToList();
                            bestResidual = residual;
                            bestContrast = meanContrast;
                            bestCoverage = choices.Count;
                            if (usePrior && priorDistance <= 50) bestPriorDistance = priorDistance;
                            bestPosition = position;
                        }
                    }
                }
            }
        }

        if (bestActive.Count < 2) return null;
        foreach (var profile in profiles)
        {
            if (!bestChoices.TryGetValue(profile.CaliperIndex, out var candidate) ||
                !edgeIndex.TryGetValue(profile.CaliperIndex, out var index)) continue;
            edges[index] = edges[index] with
            {
                X = profile.Cx + candidate.T * profile.NrmX,
                Y = profile.Cy + candidate.T * profile.NrmY,
                Contrast = candidate.Contrast,
            };
        }
        return bestActive;
    }

    /// <summary>提取单个剖面的全部局部梯度峰，供跨卡尺共识选择使用。</summary>
    internal static List<(double T, double Contrast)> FindEdgeCandidates(
        double[] profile, double tLo, double edgeThreshold, string polarity)
    {
        var gm = new double[profile.Length];
        var signed = new double[profile.Length];
        for (var i = 1; i < profile.Length - 1; i++)
        {
            signed[i] = profile[i + 1] - profile[i - 1];
            gm[i] = Math.Abs(signed[i]);
        }

        var any = string.Equals(polarity, LineFindPolarity.Any, StringComparison.Ordinal);
        var positive = !string.Equals(polarity, LineFindPolarity.BrightToDark, StringComparison.Ordinal);
        var near = Math.Max(1.0, edgeThreshold * 0.4);
        var strict = new List<(double T, double Contrast)>();
        var fallback = new List<(double T, double Contrast)>();
        for (var i = 1; i < profile.Length - 1; i++)
        {
            if (!any && (signed[i] > 0) != positive) continue;
            if (gm[i] < near || gm[i] < gm[i - 1] || gm[i] < gm[i + 1]) continue;
            var candidate = RefineAtPeak(gm, tLo, i);
            (gm[i] >= edgeThreshold ? strict : fallback).Add(candidate);
        }
        var result = strict.Count > 0 ? strict : fallback;
        const int maxCandidatesPerCaliper = 12;
        if (result.Count <= maxCandidatesPerCaliper) return result;

        // Full-ROI search can contain many texture peaks. Keep both scan ends
        // for First/Last semantics, then retain the strongest remaining peaks
        // so consensus remains bounded and deterministic.
        var bounded = result
            .OrderBy(candidate => candidate.T)
            .Take(2)
            .Concat(result.OrderByDescending(candidate => candidate.Contrast).Take(maxCandidatesPerCaliper))
            .OrderByDescending(candidate => candidate.Contrast)
            .DistinctBy(candidate => Math.Round(candidate.T, 3))
            .Take(maxCandidatesPerCaliper)
            .ToList();
        return bounded;
    }

    /// <summary>Tukey 双权权重（IRLS 用，红色散降权）：尺度 c = max(4.685 × 1.4826 × median(|r|), 0.5)；|r|≥c → 0，否则 (1−u²)²。保留为 A/B 对拍。</summary>
    internal static double[] TukeyWeights(List<double> residuals)
    {
        var abs = residuals.Select(Math.Abs).OrderBy(v => v).ToList();
        var c = Math.Max(4.685 * 1.4826 * abs[abs.Count / 2], 0.5);
        var weights = new double[residuals.Count];
        for (var i = 0; i < weights.Length; i++)
        {
            var u = Math.Abs(residuals[i]) / c;
            if (u < 1)
            {
                var t = 1 - u * u;
                weights[i] = t * t;
            }
        }
        return weights;
    }

    /// <summary>Huber 权重（VM FitFun=Huber 同款，线性影响区 M-估计）：k = 1.345 × 1.4826 × median(|r|)（高斯 95% 效率常数），|r|≤k → 1，否则 k/|r|；下限 0.5px 防零尺度失效。圆查找复用。</summary>
    internal static double[] HuberWeights(List<double> residuals)
    {
        var abs = residuals.Select(Math.Abs).OrderBy(v => v).ToList();
        var k = Math.Max(1.345 * 1.4826 * abs[abs.Count / 2], 0.5);
        var weights = new double[residuals.Count];
        for (var i = 0; i < weights.Length; i++)
        {
            var r = Math.Abs(residuals[i]);
            weights[i] = r <= k ? 1.0 : k / r;
        }
        return weights;
    }

    /// <summary>法线方向 0.5px 步长采样剖面（相位量化 ±0.2px）；采样不足 9 点返回 null。圆查找复用。</summary>
    internal static double[]? SampleProfile(
        byte[] gray, int width, int height,
        double cx, double cy, double nx, double ny, double tLo, double tHi)
    {
        var count = (int)Math.Round((tHi - tLo) * 2) + 1;
        if (count < 9) return null;
        var profile = new double[count];
        for (var i = 0; i < count; i++)
        {
            var t = tLo + i * 0.5;
            profile[i] = SampleBilinear(gray, width, height, cx + t * nx, cy + t * ny);
        }
        return profile;
    }

    /// <summary>
    /// 单卡尺选边：中心差分梯度（±0.5px 邻域，幅值刻度=每像素）→ 阈值+极性过滤 → 边缘类型选峰 →
    /// 抛物线亚像素（|梯度| 三点抛物线顶点，钳 ±1 采样步=±0.5px）。返回 T=沿法线绝对位移、Contrast=峰值幅值。圆查找复用。
    /// </summary>
    internal static (double T, double Contrast)? FindEdge(double[] profile, double tLo, double edgeThreshold, string polarity, string edgeType, CaliperScanStats? stats = null)
    {
        var m = profile.Length;
        var gm = new double[m];
        var gSigned = new double[m];
        // 采样步 0.5px：s[i+1]-s[i-1] 跨度恰为 1px → 幅值刻度=每像素灰度变化（边缘阈值 0-255 同 VM 语义）
        for (var i = 1; i < m - 1; i++)
        {
            var g = profile[i + 1] - profile[i - 1];
            gSigned[i] = g;
            gm[i] = Math.Abs(g);
        }

        // 诊断统计：记录剖面最大梯度（阈值/极性过滤前的原始值），未找到时用于给出可操作提示
        if (stats is not null)
        {
            stats.Profiles++;
            for (var i = 1; i < m - 1; i++)
            {
                if (gSigned[i] > stats.MaxPos) stats.MaxPos = gSigned[i];
                else if (-gSigned[i] > stats.MaxNeg) stats.MaxNeg = -gSigned[i];
            }
        }

        // 先填满 gm 再选峰（First 模式在首个候选处细化时，邻域梯度必须已就绪）
        var anyPolarity = string.Equals(polarity, LineFindPolarity.Any, StringComparison.Ordinal);
        var wantPositive = !string.Equals(polarity, LineFindPolarity.BrightToDark, StringComparison.Ordinal);
        var bestIdx = -1;
        var bestVal = 0.0;
        // 现场低对比度边缘可能因滤波/量化落在阈值下方。保留 40% 阈值兜底，
        // 仍要求原极性且至少达到 1 灰度/像素，避免平坦区域被当成边缘。
        var nearThreshold = Math.Max(1.0, edgeThreshold * 0.4);
        var nearIdx = -1;
        var nearVal = 0.0;
        for (var i = 1; i < m - 1; i++)
        {
            var mag = gm[i];
            if (!anyPolarity && (gSigned[i] > 0) != wantPositive) continue;
            if (mag >= nearThreshold)
            {
                switch (edgeType)
                {
                    case LineFindEdgeType.First:
                        if (nearIdx < 0) nearIdx = i;
                        break;
                    case LineFindEdgeType.Last:
                        nearIdx = i;
                        break;
                    default:
                        if (mag > nearVal)
                        {
                            nearVal = mag;
                            nearIdx = i;
                        }
                        break;
                }
            }
            if (mag < edgeThreshold) continue;
            switch (edgeType)
            {
                case LineFindEdgeType.First:
                    return RefineAtPeak(gm, tLo, i);
                case LineFindEdgeType.Last:
                    bestIdx = i;
                    break;
                default: // 最强
                    if (mag > bestVal)
                    {
                        bestVal = mag;
                        bestIdx = i;
                    }
                    break;
            }
        }
        // 严格阈值没有候选时使用近阈值候选；严格候选始终优先。
        var selectedIdx = bestIdx >= 0 ? bestIdx : nearIdx;
        return selectedIdx < 0 ? null : RefineAtPeak(gm, tLo, selectedIdx);
    }

    /// <summary>爬坡到局部峰（First/Last 的阈值肩部点 → 该过渡带的 |梯度| 峰）再抛物线细化。圆查找复用。</summary>
    internal static (double T, double Contrast) RefineAtPeak(double[] gm, double tLo, int i)
    {
        while (i > 1 && gm[i - 1] > gm[i]) i--;
        while (i < gm.Length - 2 && gm[i + 1] > gm[i]) i++;
        return Refine(gm, tLo, i);
    }

    /// <summary>抛物线亚像素：|梯度| 三点抛物线顶点偏移（单位=采样步 0.5px，钳 ±1 步）。圆查找复用。</summary>
    internal static (double T, double Contrast) Refine(double[] gm, double tLo, int i)
    {
        var l = gm[i - 1];
        var c = gm[i];
        var r = gm[i + 1];
        var denom = l - 2 * c + r;
        var delta = denom != 0 ? (l - r) / (2 * denom) : 0;
        if (delta > 1) delta = 1;
        else if (delta < -1) delta = -1;
        return (tLo + (i + delta) * 0.5, c);
    }

    /// <summary>高斯 1D 平滑（边缘钳位；滤波尺寸 px → 0.5px 采样步下的二项式核：2→3点/3→5点/5→7点/7→9点）。圆查找复用。</summary>
    internal static void SmoothBinomial(double[] s, int size)
    {
        var kernel = size switch
        {
            2 => Kernel3,
            3 => Kernel5,
            5 => Kernel7,
            7 => Kernel9,
            _ => Kernel5,
        };
        var r = kernel.Length / 2;
        var sum = kernel.Sum();
        var tmp = new double[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            double acc = 0;
            for (var k = 0; k < kernel.Length; k++)
            {
                var j = i + k - r;
                if (j < 0) j = 0;
                else if (j >= s.Length) j = s.Length - 1;
                acc += s[j] * kernel[k];
            }
            tmp[i] = acc / sum;
        }
        Array.Copy(tmp, s, s.Length);
    }

    /// <summary>沿法线的采样区间与图像边界求交（slab）；整段在外返回 false。常量方向贴边（c==size）合法（双线性钳列采样）。圆查找复用。</summary>
    internal static bool ClampRange(double c, double dir, int size, ref double lo, ref double hi)
    {
        if (Math.Abs(dir) < 1e-9)
        {
            return c >= -1 && c <= size;
        }
        var t0 = -c / dir;
        var t1 = (size - 1 - c) / dir;
        var a = Math.Min(t0, t1);
        var b = Math.Max(t0, t1);
        lo = Math.Max(lo, a);
        hi = Math.Min(hi, b);
        return hi > lo;
    }

    /// <summary>双线性灰度采样（坐标钳制到图像内）。圆查找复用。</summary>
    internal static double SampleBilinear(byte[] gray, int w, int h, double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        if (x0 < 0) x0 = 0;
        else if (x0 > w - 1) x0 = w - 1;
        if (y0 < 0) y0 = 0;
        else if (y0 > h - 1) y0 = h - 1;
        var x1 = Math.Min(x0 + 1, w - 1);
        var y1 = Math.Min(y0 + 1, h - 1);
        var fx = Math.Clamp(x - x0, 0, 1);
        var fy = Math.Clamp(y - y0, 0, 1);
        var g00 = gray[y0 * w + x0];
        var g10 = gray[y0 * w + x1];
        var g01 = gray[y1 * w + x0];
        var g11 = gray[y1 * w + x1];
        var top = g00 + (g10 - g00) * fx;
        var bottom = g01 + (g11 - g01) * fx;
        return top + (bottom - top) * fy;
    }
}
