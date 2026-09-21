namespace GBARenderer.Tests;

public class FrameMotionTests
{
    [Fact]
    public void HandingMotionOverLeavesThingsWhereTheyEndedUp()
    {
        var motion = new FrameMotion();
        motion.Backgrounds[3] = new Displacement { From = new(1.5f, 0), To = new(0.5f, 0) };
        motion.Windows[1] = new Displacement { From = new(0, 2), To = new(0, 1) };

        motion.Settle();

        Assert.Equal((0.5f, 0.5f), (motion.Backgrounds[3].From.X, motion.Backgrounds[3].To.X));
        Assert.Equal((1f, 1f), (motion.Windows[1].From.Y, motion.Windows[1].To.Y));
    }
}
