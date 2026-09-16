using OpenCvSharp;
using VisionInspection.Comm;
using VisionInspection.Detection;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>发送数据/接收数据节点测试：假运行时注入，覆盖模板解析/参数校验/超时判定（全离线，无真实网络）。</summary>
public class CommDataNodeTests
{
    /// <summary>假通信运行时：收发行为可编程。</summary>
    private sealed class FakeRuntime : ICommRuntime
    {
        private readonly object _gate = new();
        private readonly List<(string Device, string Text)> _sent = [];

        public CommSendOutcome SendOutcome { get; set; } = CommSendOutcome.Success;
        public CommReceiveOutcome ReceiveOutcome { get; set; } = CommReceiveOutcome.Timeout;

        public CommSendOutcome Send(string deviceName, string text)
        {
            lock (_gate)
            {
                _sent.Add((deviceName, text));
            }

            return SendOutcome;
        }

        public CommReceiveOutcome Receive(string deviceName, int timeoutMs) => ReceiveOutcome;
        public bool IsHeld(string deviceName) => true;
        public List<(string Device, string Text)> SnapshotSent() { lock (_gate) { return [.. _sent]; } }
    }

    private static SendDataNode NewSendNode(params (string Key, string Value)[] init)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in init) dict[k] = v;
        return new SendDataNode("发送数据1", dict);
    }

    private static ReceiveDataNode NewReceiveNode(params (string Key, string Value)[] init)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in init) dict[k] = v;
        return new ReceiveDataNode("接收数据1", dict);
    }

    /// <summary>上游「检测1」节点输出 count=3、判定 NG。</summary>
    private static void SeedUpstream(PipelineRunContext ctx, string decision = "NG")
    {
        var nr = new NodeResult { Decision = decision };
        nr.Values["count"] = "3";
        ctx.Results["检测1"] = nr;
    }

    // ===== 模板解析（纯函数） =====

    [Fact]
    public void ResolveTemplate_UpstreamRef_Decision_Missing()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var ctx = new PipelineRunContext(img);
        SeedUpstream(ctx, "NG");

        var resolved = SendDataNode.ResolveTemplate("数量:{检测1.count};判定:{decision};坏:{不存在.loc_x}", ctx, out var missing);

        Assert.Equal("数量:3;判定:NG;坏:", resolved);
        Assert.Equal(["不存在.loc_x"], missing);
    }

    [Fact]
    public void ResolveTemplate_EmptyTemplate_NoMissing()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var ctx = new PipelineRunContext(img);
        var resolved = SendDataNode.ResolveTemplate("", ctx, out var missing);
        Assert.Equal("", resolved);
        Assert.Empty(missing);

        // 无占位符的纯文本原样通过
        Assert.Equal("plain", SendDataNode.ResolveTemplate("plain", ctx, out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void ResolveTemplate_DecisionFallsBackToNgWhenAnyNodeNg()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var ctx = new PipelineRunContext(img);
        SeedUpstream(ctx, "NG");
        var resolved = SendDataNode.ResolveTemplate("{decision}", ctx, out _);
        Assert.Equal("NG", resolved);
    }

    [Fact]
    public void NormalizeSuffix_EscapesAndNone()
    {
        Assert.Equal("", SendDataNode.NormalizeSuffix("无"));
        Assert.Equal("", SendDataNode.NormalizeSuffix(""));
        Assert.Equal("", SendDataNode.NormalizeSuffix(null));
        Assert.Equal("\r\n", SendDataNode.NormalizeSuffix("\\r\\n"));
        Assert.Equal("\n", SendDataNode.NormalizeSuffix("\\n"));
    }

    // ===== 发送数据节点 =====

    [Fact]
    public void Send_NoDeviceSelected_Errors()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var ctx = new PipelineRunContext(img, commRuntime: new FakeRuntime());
        var nr = NewSendNode(("send_text", "OK")).Run(img, ctx);
        Assert.Equal("ERROR", nr.Decision);
        Assert.Contains("未选择通信设备", nr.Error);
        Assert.Equal("0", nr.Values["sent"]);
    }

    [Fact]
    public void Send_WithoutRuntime_Errors()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var ctx = new PipelineRunContext(img);
        var nr = NewSendNode(("device", "PLC"), ("send_text", "OK")).Run(img, ctx);
        Assert.Equal("ERROR", nr.Decision);
        Assert.Contains("未绑定通信运行时", nr.Error);
    }

    [Fact]
    public void Send_Ok_PayloadResolvesTemplateAndSuffix()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var runtime = new FakeRuntime();
        var ctx = new PipelineRunContext(img, commRuntime: runtime);
        SeedUpstream(ctx, "OK");

        var node = NewSendNode(("device", "PLC"), ("send_text", "{检测1.count}/{decision}"), ("suffix", "\\r\\n"));
        var result = node.Run(img, ctx);

        Assert.Equal("OK", result.Decision);
        Assert.Equal("1", result.Values["sent"]);
        Assert.Equal("3/OK\r\n", result.Values["resolved_text"]);
        var sent = Assert.Single(runtime.SnapshotSent());
        Assert.Equal(("PLC", "3/OK\r\n"), sent);
    }

    [Fact]
    public void Send_RuntimeFailure_PropagatesError()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var runtime = new FakeRuntime { SendOutcome = CommSendOutcome.Fail("设备「PLC」链路未就绪（TCP客户端未连上）") };
        var ctx = new PipelineRunContext(img, commRuntime: runtime);
        var nr = NewSendNode(("device", "PLC"), ("send_text", "OK")).Run(img, ctx);
        Assert.Equal("ERROR", nr.Decision);
        Assert.Contains("未就绪", nr.Error);
        Assert.Equal("0", nr.Values["sent"]);
    }

    // ===== 接收数据节点 =====

    [Fact]
    public void Receive_GotText_Ok()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var runtime = new FakeRuntime { ReceiveOutcome = CommReceiveOutcome.Got("OK") };
        var ctx = new PipelineRunContext(img, commRuntime: runtime);
        var nr = NewReceiveNode(("device", "PLC")).Run(img, ctx);
        Assert.Equal("OK", nr.Decision);
        Assert.Equal("1", nr.Values["received"]);
        Assert.Equal("OK", nr.Values["text"]);
    }

    [Fact]
    public void Receive_Timeout_DefaultStopsLine()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var runtime = new FakeRuntime { ReceiveOutcome = CommReceiveOutcome.Timeout };
        var ctx = new PipelineRunContext(img, commRuntime: runtime);
        var nr = NewReceiveNode(("device", "PLC"), ("timeout_ms", "123")).Run(img, ctx);
        Assert.Equal("ERROR", nr.Decision);
        Assert.Contains("接收超时(123ms)", nr.Error);
        Assert.Equal("0", nr.Values["received"]);
    }

    [Fact]
    public void Receive_Timeout_ContinueAsOk()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var runtime = new FakeRuntime { ReceiveOutcome = CommReceiveOutcome.Timeout };
        var ctx = new PipelineRunContext(img, commRuntime: runtime);
        var nr = NewReceiveNode(("device", "PLC"), ("timeout_action", "按OK继续")).Run(img, ctx);
        Assert.Equal("OK", nr.Decision);
        Assert.Equal("0", nr.Values["received"]);
        Assert.DoesNotContain("text", nr.Values.Keys);
    }

    [Fact]
    public void Receive_NoDevice_OrRuntimeMissing_Errors()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var ctxWithRuntime = new PipelineRunContext(img, commRuntime: new FakeRuntime());
        var nr = NewReceiveNode().Run(img, ctxWithRuntime);
        Assert.Equal("ERROR", nr.Decision);
        Assert.Contains("未选择通信设备", nr.Error);

        var ctxNoRuntime = new PipelineRunContext(img);
        var nr2 = NewReceiveNode(("device", "PLC")).Run(img, ctxNoRuntime);
        Assert.Equal("ERROR", nr2.Decision);
        Assert.Contains("未绑定通信运行时", nr2.Error);
    }

    [Fact]
    public void ParseTimeoutMs_InvalidFallsBackToDefault()
    {
        Assert.Equal(5000, ReceiveDataNode.ParseTimeoutMs(null));
        Assert.Equal(5000, ReceiveDataNode.ParseTimeoutMs("abc"));
        Assert.Equal(5000, ReceiveDataNode.ParseTimeoutMs("-1"));
        Assert.Equal(0, ReceiveDataNode.ParseTimeoutMs("0"));
        Assert.Equal(1200, ReceiveDataNode.ParseTimeoutMs("1200"));
    }

    // ===== 工厂契约 =====

    [Fact]
    public void Factory_CreatesBothTypes_ParamsRoundTrip_DisposeIdempotent()
    {
        var send = NodeFactory.Create("SendData", "发送数据1");
        Assert.Equal("SendData", send.Type);
        Assert.Contains(send.ParamDefs, d => d.Key == "device");
        var recv = NodeFactory.Create("ReceiveData", "接收数据1");
        Assert.Equal("ReceiveData", recv.Type);
        Assert.Contains(recv.ParamDefs, d => d.Key == "device");

        recv.SetParam("device", "PLC");
        recv.SetParam("timeout_action", "按OK继续");
        Assert.Equal("PLC", recv.Params["device"]);
        Assert.Equal("按OK继续", recv.Params["timeout_action"]);

        send.Dispose();
        send.Dispose(); // 幂等
        recv.Dispose();
        recv.Dispose();
    }
}
