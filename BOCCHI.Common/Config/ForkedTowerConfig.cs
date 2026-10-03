using Ocelot.Config;
using Ocelot.Config.Fields;

namespace BOCCHI.Common.Config;

[Serializable]
[ConfigGroup("forked_tower", GroupOrder = 50)]
public class ForkedTowerConfig : IAutoConfig
{
    [Checkbox(Order = 0, Section = "helpers")]
    public bool DrawPotentialTrapPositions { get; set; } = true;

    [Checkbox(Order = 1, Section = "helpers")]
    public bool ShowRegistrationCountdown { get; set; } = true;

    [FloatRange(20f, 300f, Order = 2, Section = "helpers")]
    public float TrapDrawRange { get; set; } = 150f;

    [Checkbox(Order = 3, Section = "illegal_mode")]
    public bool AutoRegisterInIllegalMode { get; set; } = false;

    [Checkbox(Order = 4, Section = "illegal_mode")]
    public bool FightInsideTower { get; set; } = true;

    [Checkbox(Order = 5, Section = "illegal_mode")]
    public bool NavigateInsideTower { get; set; } = true;

    [FloatRange(3f, 30f, Order = 6, Section = "illegal_mode")]
    public float FollowStartDistance { get; set; } = 8f;

    [FloatRange(1f, 20f, Order = 7, Section = "illegal_mode")]
    public float FollowStopDistance { get; set; } = 4f;

    [Checkbox(Order = 8, Section = "illegal_mode")]
    public bool OpenCoffersInsideTower { get; set; } = true;

    [Checkbox(Order = 9, Section = "illegal_mode")]
    public bool UseTowerRoute { get; set; } = true;

    [Checkbox(Order = 10, Section = "diagnostics")]
    public bool RecordTowerMechanics { get; set; } = true;

    [Checkbox(Order = 11, Section = "diagnostics")]
    public bool RecordTowerRoute { get; set; } = false;
}
