using SpeakerVisionInspection.Trigger;
using Xunit;

namespace SpeakerVisionInspection.Tests;

public class TriggerStateMachineTests
{
    [Fact]
    public void ArmThenTriggerThenProcessed_ReturnsToArmed()
    {
        var sm = new TriggerStateMachine();
        Assert.True(sm.Arm());
        Assert.Equal(TriggerState.Armed, sm.State);
        Assert.True(sm.TriggerReceived());
        Assert.Equal(TriggerState.Busy, sm.State);
        Assert.True(sm.FrameProcessed());
        Assert.Equal(TriggerState.Armed, sm.State);
    }

    [Fact]
    public void TriggerWhileBusy_CountsOverrun()
    {
        var sm = new TriggerStateMachine();
        sm.Arm();
        sm.TriggerReceived();
        Assert.False(sm.TriggerReceived());
        Assert.Equal(1, sm.OverrunCount);
        Assert.Equal(TriggerState.Busy, sm.State);
    }

    [Fact]
    public void GrabTimeout_OnlyAfterArmed()
    {
        var sm = new TriggerStateMachine();
        sm.Arm();
        Assert.True(sm.GrabTimeout());
        Assert.Equal(TriggerState.Error, sm.State);
    }

    [Fact]
    public void ProcessingError_FromBusy_GoesError()
    {
        var sm = new TriggerStateMachine();
        sm.Arm();
        sm.TriggerReceived();
        Assert.True(sm.ProcessingError("检测异常"));
        Assert.Equal(TriggerState.Error, sm.State);
    }

    [Fact]
    public void Reset_FromError_ReturnsArmed()
    {
        var sm = new TriggerStateMachine();
        sm.Arm();
        sm.GrabTimeout();
        Assert.True(sm.Reset());
        Assert.Equal(TriggerState.Armed, sm.State);
    }
}
