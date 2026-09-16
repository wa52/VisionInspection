using System;
using System.Collections.Generic;

namespace VisionInspection.Detection;

/// <summary>单个实例分割检测结果（模型输入像素坐标，中心点表示；含掩码系数与输出 anchor 索引）。</summary>
public readonly record struct SegDetection(
    int ClassIndex, float Conf, double Cx, double Cy, double W, double H,
    int AnchorIndex, float[] Coeffs);

/// <summary>
/// Ultralytics YOLO-seg ONNX（v8/v11 标准导出）实例分割后处理纯函数。
/// output0 [1, 4+nc+K, N]：前 4 行 cx,cy,w,h（模型输入像素坐标系），中 nc 行类别置信度，后 K 行掩码系数；
/// output1 [1, K, PH, PW]：proto 掩码原型。实例掩码 = 系数·proto 后取 logits&gt;0（即 sigmoid&gt;0.5）。
/// </summary>
public static class YoloSegPostprocess
{
    /// <summary>
    /// 解析 output0（data 为 [channels, count] 展平，行主序 data[c*count+i]）→ 置信度过滤 + 按类贪心 NMS。
    /// numCoeffs 为掩码系数行数（yolo11 导出固定 32）。返回模型输入坐标系下的检测结果。
    /// </summary>
    public static List<SegDetection> ParseSegAndNms(
        float[] data, int channels, int count, int numClasses, int numCoeffs,
        float confThreshold, float iouThreshold)
    {
        var boxClsEnd = 4 + numClasses;
        if (channels < boxClsEnd + numCoeffs || count <= 0 || numClasses <= 0) return [];

        var candidates = new List<(double Cx, double Cy, double W, double H, int Cls, float Conf, int Anchor, float[] Coeffs)>();
        for (var i = 0; i < count; i++)
        {
            var bestCls = -1;
            var bestConf = 0f;
            for (var c = 4; c < boxClsEnd; c++)
            {
                var v = data[c * count + i];
                if (v > bestConf)
                {
                    bestConf = v;
                    bestCls = c - 4;
                }
            }

            if (bestCls < 0 || bestConf <= confThreshold) continue;
            var coeffs = new float[numCoeffs];
            for (var k = 0; k < numCoeffs; k++)
            {
                coeffs[k] = data[(boxClsEnd + k) * count + i];
            }

            candidates.Add((
                data[0 * count + i], data[1 * count + i], data[2 * count + i], data[3 * count + i],
                bestCls, bestConf, i, coeffs));
        }

        // 按置信度降序，逐类贪心 NMS（与 YoloPostprocess.ParseAndNms 同策略）
        candidates.Sort((a, b) => b.Conf.CompareTo(a.Conf));
        var kept = new List<SegDetection>();
        var suppressed = new HashSet<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (suppressed.Contains(i)) continue;
            var c = candidates[i];
            kept.Add(new SegDetection(c.Cls, c.Conf, c.Cx, c.Cy, c.W, c.H, c.Anchor, c.Coeffs));
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (suppressed.Contains(j)) continue;
                var o = candidates[j];
                if (o.Cls != c.Cls) continue;
                if (YoloPostprocess.Iou(c.Cx, c.Cy, c.W, c.H, o.Cx, o.Cy, o.W, o.H) > iouThreshold)
                {
                    suppressed.Add(j);
                }
            }
        }

        return kept;
    }

    /// <summary>实例掩码 logits：Σ coeff[k]·proto[k]（proto 展平 [k·ph·pw + y·pw + x]）。返回长 ph·pw。</summary>
    public static float[] ComposeMaskLogits(float[] coeffs, float[] proto, int protoH, int protoW)
    {
        var area = protoH * protoW;
        var logits = new float[area];
        if (area <= 0) return logits;
        for (var k = 0; k < coeffs.Length; k++)
        {
            var coef = coeffs[k];
            if (coef == 0f) continue;
            var baseIdx = k * area;
            for (var i = 0; i < area; i++)
            {
                logits[i] += coef * proto[baseIdx + i];
            }
        }
        return logits;
    }
}
