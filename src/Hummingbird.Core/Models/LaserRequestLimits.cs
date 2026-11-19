namespace Hummingbird.Core;

/// <summary>软件输入边界，不表示设备额定能力或材料工艺推荐。</summary>
public static class LaserRequestLimits
{
    public const int MinimumExposureMs = 50;
    public const int MaximumExposureMs = 600000;
}
