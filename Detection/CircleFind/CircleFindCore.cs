using System.Globalization;

namespace VisionInspection.Detection;

/// <summary>圆环搜索区域（像素，源图坐标）：卡尺沿中圆 Rmid=(Ro+Ri)/2 均布，径向采样范围 = [Ri, Ro]。</summary>
public readonly record struct CircleSearchRegion(double Cx, double Cy, double ROuter, double RInner);

/// <summary>圆查找参数（VM 卡尺找圆对齐；无 search_length——环带宽度即卡尺搜索范围）。</summary>
public sealed record CircleFindParams
{
    public string Polarity { get; init; } = LineFindPolarity.DarkToBright;
    public double EdgeThreshold { get; init; } = 30;
    /// <summary>1=不滤波，3/5/7=二项式高斯核。</summary>
    public int FilterSize { get; init; } = 3;
    public int CaliperNum { get; init; } = 24;
    public string EdgeType { get; init; } = LineFindEdgeType.Strongest;
    public int RejectNum { get; init; } = 0;
    public double RejectDist { get; init; } = 3;
    public string FitMethod { get; init; } = LineFindFitMethod.Lsq;
}

/// <summary>圆查找结果：Found=false 时 Error 给可操作原因；Found=true 时圆几何/得分有效。</summary>
public sealed record CircleFindResult
{
    public bool Found { get; init; }
    public string? Error { get; init; }
    /// <summary>全部有效卡尺点（含被剔除的离群点）。</summary>
    public List<CaliperEdge> Edges { get; init; } = [];
    /// <summary>参与最终拟合的内点（绕圆心角度升序）。</summary>
    public List<CaliperEdge> Inliers { get; init; } = [];
    public double CenterX { get; init; }
    public double CenterY { get; init; }
    public double Radius { get; init; }
    /// <summary>得分 = 内点数 / 卡尺数（0~1，VM 同款有效卡尺比例语义）。</summary>
    public double Score { get; init; }
    public double MeanContrast { get; init; }
}

/// <summary>
/// 圆查找算法核（纯逻辑，可单测；VM 卡尺找圆对齐）：
/// 卡尺沿标称中圆角度均布 → 径向 0.5px 步长双线性采样灰度剖面（采样范围=环带 [Ri, Ro] ∩ 图像边界）→
/// 1D 二项式高斯平滑 → 中心差分梯度 → 阈值+极性+边缘类型选峰（复用直线查找的选边链，正向=径向向外）→
/// 抛物线亚像素 → Kasa 代数最小二乘圆拟合（3×3 线性方程组闭式解）→ 剔除点数/剔除距离迭代剔离群（永不低于 3 点）。
/// 典型 1024×768 图 + 32 卡尺×32px 环带全链 &lt;1ms（预算 5ms）。
/// </summary>
public static class CircleFindCore
{
    private sealed record ProfileCandidateSet(
        int CaliperIndex,
        double Cx,
        double Cy,
        double DirX,
        double DirY,
        List<(double T, double Contrast)> Candidates);

    public static CircleFindResult Find(byte[] gray, int width, int height, CircleSearchRegion region, CircleFindParams p, bool allowAutomaticFallback = true)
    {
        if (width <= 0 || height <= 0 || gray.Length < width * height)
        {
            return new CircleFindResult { Found = false, Error = "灰度缓冲与图像尺寸不一致" };
        }
        if (region.ROuter - region.RInner < 4)
        {
            return new CircleFindResult { Found = false, Error = "环带宽度不足（<4px）：请把内圆半径调小或外圆半径调大" };
        }

        var num = Math.Clamp(p.CaliperNum, 3, 4096);
        var rMid = (region.ROuter + region.RInner) / 2;
        var tIn = region.RInner - rMid;  // 径向内界（负）
        var tOut = region.ROuter - rMid; // 径向外界（正）

        var stats = new CaliperScanStats();
        var edges = new List<CaliperEdge>(num);
        var profileCandidates = new List<ProfileCandidateSet>(num);
        for (var i = 0; i < num; i++)
        {
            var theta = 2 * Math.PI * i / num;
            var dirX = Math.Cos(theta);
            var dirY = Math.Sin(theta);
            var cx = region.Cx + rMid * dirX;
            var cy = region.Cy + rMid * dirY;

            // 径向采样范围：环带 ∩ 图像边界（卡尺不越出图像；环带本身即搜索范围）
            var tLo = tIn;
            var tHi = tOut;
            if (!LineFindCore.ClampRange(cx, dirX, width, ref tLo, ref tHi) || !LineFindCore.ClampRange(cy, dirY, height, ref tLo, ref tHi))
            {
                continue;
            }

            var profile = LineFindCore.SampleProfile(gray, width, height, cx, cy, dirX, dirY, tLo, tHi);
            if (profile is null) continue;
            if (p.FilterSize >= 3) LineFindCore.SmoothBinomial(profile, p.FilterSize);

            if (string.Equals(p.FitMethod, LineFindFitMethod.Robust, StringComparison.Ordinal))
            {
                var candidates = LineFindCore.FindEdgeCandidates(profile, tLo, p.EdgeThreshold, p.Polarity);
                if (candidates.Count > 0)
                {
                    profileCandidates.Add(new ProfileCandidateSet(i, cx, cy, dirX, dirY, candidates));
                }
            }

            var edge = LineFindCore.FindEdge(profile, tLo, p.EdgeThreshold, p.Polarity, p.EdgeType, stats);
            if (edge is not null)
            {
                edges.Add(new CaliperEdge(i, cx + edge.Value.T * dirX, cy + edge.Value.T * dirY, edge.Value.Contrast));
            }
        }

        if (edges.Count < 3)
        {
            if (allowAutomaticFallback && string.Equals(p.FitMethod, LineFindFitMethod.Robust, StringComparison.Ordinal))
            {
                // Real images often have a weaker gradient scale than the configured VM threshold.
                // Retry only after the requested settings fail, so explicit settings remain the first choice.
                var adaptiveThreshold = Math.Max(2, Math.Min(p.EdgeThreshold, stats.MaxAbs * 0.35));
                if (adaptiveThreshold < p.EdgeThreshold - 1e-9 || !string.Equals(p.Polarity, LineFindPolarity.Any, StringComparison.Ordinal))
                {
                    var automatic = Find(gray, width, height, region, p with
                    {
                        EdgeThreshold = adaptiveThreshold,
                        Polarity = LineFindPolarity.Any,
                    }, allowAutomaticFallback: false);
                    if (automatic.Found || automatic.Edges.Count > edges.Count)
                    {
                        return automatic;
                    }
                }
            }
            var rangeText = $"径向采样 {region.RInner.ToString("0", CultureInfo.InvariantCulture)}~{region.ROuter.ToString("0", CultureInfo.InvariantCulture)}px";
            return new CircleFindResult
            {
                Found = false,
                Edges = edges,
                Error = LineFindCore.BuildNotFoundMessage(edges.Count, 3, num, rangeText, p.EdgeThreshold, p.Polarity, stats),
            };
        }

        var pts = edges.Select(e => (e.X, e.Y)).ToList();
        var active = Enumerable.Range(0, pts.Count).ToList();
        var centerX = 0.0;
        var centerY = 0.0;
        var radius = 0.0;
        var removed = 0;
        if (string.Equals(p.FitMethod, LineFindFitMethod.Robust, StringComparison.Ordinal))
        {
            var consensus = SelectConsensusCircle(edges, profileCandidates,
                Math.Max(1.5, Math.Min(5.0, p.RejectDist > 0 ? p.RejectDist : 3.0)), p.EdgeType);
            if (consensus is { Count: >= 3 })
            {
                var edgeIndex = edges.Select((edge, index) => (edge.Index, index))
                    .ToDictionary(x => x.Index, x => x.index);
                foreach (var profile in profileCandidates)
                {
                    if (!consensus.TryGetValue(profile.CaliperIndex, out var candidate) ||
                        !edgeIndex.TryGetValue(profile.CaliperIndex, out var index)) continue;
                    edges[index] = edges[index] with
                    {
                        X = profile.Cx + candidate.T * profile.DirX,
                        Y = profile.Cy + candidate.T * profile.DirY,
                        Contrast = candidate.Contrast,
                    };
                }
                pts = edges.Select(e => (e.X, e.Y)).ToList();
                active = consensus.Keys.Where(edgeIndex.ContainsKey).Select(index => edgeIndex[index]).ToList();
            }
        }
        while (true)
        {
            var fit = FitCircle(pts, active);
            if (!fit.Ok)
            {
                return new CircleFindResult
                {
                    Found = false,
                    Edges = edges,
                    Error = $"边缘点近似共线，无法拟合圆（{active.Count} 有效点）：请检查「边缘极性」或增加「卡尺数量」",
                };
            }
            (centerX, centerY, radius) = (fit.Cx, fit.Cy, fit.Radius);
            if (removed >= Math.Max(0, p.RejectNum) || active.Count <= 3)
            {
                break;
            }

            var worstIdx = -1;
            var worstRes = 0.0;
            foreach (var i in active)
            {
                var dx = pts[i].Item1 - centerX;
                var dy = pts[i].Item2 - centerY;
                var res = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - radius);
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

        // 鲁棒拟合（VM FitFun=Huber 同款：Huber IRLS 迭代降权精修；全等权/退化时保持当前拟合）
        if (string.Equals(p.FitMethod, LineFindFitMethod.Robust, StringComparison.Ordinal))
        {
            for (var iter = 0; iter < 8; iter++)
            {
                var residuals = new List<double>(active.Count);
                foreach (var i in active)
                {
                    var dx = pts[i].Item1 - centerX;
                    var dy = pts[i].Item2 - centerY;
                    residuals.Add(Math.Abs(Math.Sqrt(dx * dx + dy * dy) - radius));
                }
                var weights = LineFindCore.HuberWeights(residuals);
                if (weights.Sum() < 1e-9) break;
                var fit = FitCircle(pts, active, weights);
                if (!fit.Ok) break;
                var converged = Math.Abs(fit.Cx - centerX) < 1e-9 && Math.Abs(fit.Cy - centerY) < 1e-9 && Math.Abs(fit.Radius - radius) < 1e-9;
                (centerX, centerY, radius) = (fit.Cx, fit.Cy, fit.Radius);
                if (converged) break;
            }
        }

        var inliers = active
            .Select(i => edges[i])
            .OrderBy(e => Math.Atan2(e.Y - centerY, e.X - centerX))
            .ToList();
        return new CircleFindResult
        {
            Found = true,
            Edges = edges,
            Inliers = inliers,
            CenterX = centerX,
            CenterY = centerY,
            Radius = radius,
            Score = (double)active.Count / num,
            MeanContrast = active.Average(i => edges[i].Contrast),
        };
    }

    private static Dictionary<int, (double T, double Contrast)>? SelectConsensusCircle(
        List<CaliperEdge> edges,
        List<ProfileCandidateSet> profiles,
        double tolerance,
        string edgeType)
    {
        if (profiles.Count < 3) return null;

        var edgeIndex = edges.Select((edge, index) => (edge.Index, index))
            .ToDictionary(x => x.Index, x => x.index);
        var sampled = profiles
            .Where((_, index) => index % Math.Max(1, profiles.Count / 8) == 0)
            .Take(8)
            .ToList();
        if (sampled.Count < 3) return null;

        var best = new Dictionary<int, (double T, double Contrast)>();
        var bestMeanContrast = double.MinValue;
        var bestResidual = double.MaxValue;
        var bestPosition = edgeType == LineFindEdgeType.Last ? double.MinValue : double.MaxValue;
        var minimumCoverage = Math.Max(3, (int)Math.Ceiling(profiles.Count * 0.5));

        for (var a = 0; a < sampled.Count - 2; a++)
        for (var b = a + 1; b < sampled.Count - 1; b++)
        for (var c = b + 1; c < sampled.Count; c++)
        foreach (var ca in sampled[a].Candidates.Take(6))
        foreach (var cb in sampled[b].Candidates.Take(6))
        foreach (var cc in sampled[c].Candidates.Take(6))
        {
            var seed = new List<(double X, double Y)>
            {
                (sampled[a].Cx + ca.T * sampled[a].DirX, sampled[a].Cy + ca.T * sampled[a].DirY),
                (sampled[b].Cx + cb.T * sampled[b].DirX, sampled[b].Cy + cb.T * sampled[b].DirY),
                (sampled[c].Cx + cc.T * sampled[c].DirX, sampled[c].Cy + cc.T * sampled[c].DirY),
            };
            var fit = FitCircle(seed, [0, 1, 2]);
            if (!fit.Ok || fit.Radius <= 0) continue;

            var choices = new Dictionary<int, (double T, double Contrast)>();
            var residual = 0.0;
            var contrast = 0.0;
            var position = 0.0;
            foreach (var profile in profiles)
            {
                var bestDistance = double.MaxValue;
                var selected = default((double T, double Contrast));
                foreach (var candidate in profile.Candidates)
                {
                    var x = profile.Cx + candidate.T * profile.DirX;
                    var y = profile.Cy + candidate.T * profile.DirY;
                    var distance = Math.Abs(Math.Sqrt(Math.Pow(x - fit.Cx, 2) + Math.Pow(y - fit.Cy, 2)) - fit.Radius);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        selected = candidate;
                    }
                }
                if (bestDistance <= tolerance)
                {
                    choices[profile.CaliperIndex] = selected;
                    residual += bestDistance * bestDistance;
                    contrast += selected.Contrast;
                    position += selected.T;
                }
            }
            if (choices.Count < minimumCoverage) continue;

            var meanContrast = contrast / choices.Count;
            var better = choices.Count > best.Count ||
                (choices.Count == best.Count && (edgeType == LineFindEdgeType.Strongest
                    ? meanContrast > bestMeanContrast + 1e-9 ||
                      (Math.Abs(meanContrast - bestMeanContrast) <= 1e-9 && residual < bestResidual)
                    : edgeType == LineFindEdgeType.First
                        ? position < bestPosition
                        : position > bestPosition));
            if (!better) continue;
            best = choices;
            bestMeanContrast = meanContrast;
            bestResidual = residual;
            bestPosition = position;
        }

        return best.Count >= 3 && edgeIndex.Keys.Any(best.ContainsKey) ? best : null;
    }

    /// <summary>
    /// Kasa 代数最小二乘圆拟合（总体最小二乘闭式解，active 为参与拟合的点索引；weights 为 null 时等权，行为与旧版逐位一致）：
    /// min Σw[(x²+y²) + D·x + E·y + F]² → 3×3 线性方程组，center=(−D/2,−E/2)，R=√(D²/4+E²/4−F)。
    /// 方程组奇异（近共线）或 R 非有限 → Ok=false。
    /// </summary>
    internal static (bool Ok, double Cx, double Cy, double Radius) FitCircle(List<(double X, double Y)> pts, List<int> active, IReadOnlyList<double>? weights = null)
    {
        if (active.Count < 3) return (false, 0, 0, 0);
        double sw = 0, sxx = 0, sxy = 0, syy = 0, sxz = 0, syz = 0, sx = 0, sy = 0, sz = 0;
        for (var k = 0; k < active.Count; k++)
        {
            var (x, y) = pts[active[k]];
            var w = weights is null ? 1.0 : weights[k];
            var z = x * x + y * y;
            sw += w;
            sxx += w * x * x;
            sxy += w * x * y;
            syy += w * y * y;
            sxz += w * x * z;
            syz += w * y * z;
            sx += w * x;
            sy += w * y;
            sz += w * z;
        }

        // Cramer 法则解 3×3：M·(D,E,F)ᵀ = (−sxz,−syz,−sz)ᵀ
        var m11 = sxx; var m12 = sxy; var m13 = sx;
        var m21 = sxy; var m22 = syy; var m23 = sy;
        var m31 = sx;  var m32 = sy;  var m33 = sw;
        var det = Det3(m11, m12, m13, m21, m22, m23, m31, m32, m33);
        if (Math.Abs(det) < 1e-9) return (false, 0, 0, 0);
        var b1 = -sxz; var b2 = -syz; var b3 = -sz;
        var d = Det3(b1, m12, m13, b2, m22, m23, b3, m32, m33) / det;
        var e = Det3(m11, b1, m13, m21, b2, m23, m31, b3, m33) / det;
        var f = Det3(m11, m12, b1, m21, m22, b2, m31, m32, b3) / det;

        var cx = -d / 2;
        var cy = -e / 2;
        var r2 = d * d / 4 + e * e / 4 - f;
        if (r2 <= 0 || !double.IsFinite(r2)) return (false, 0, 0, 0);
        var r = Math.Sqrt(r2);
        if (!double.IsFinite(r) || !double.IsFinite(cx) || !double.IsFinite(cy)) return (false, 0, 0, 0);
        return (true, cx, cy, r);
    }

    private static double Det3(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
        a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
}
