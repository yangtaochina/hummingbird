namespace Hummingbird.Core;

/// <summary>
/// 相机诊断扩展接口（可选实现）。
///
/// 用途（2026-11-05 现场问题）：现场出现"曝光拉满仍然全黑 / 只看到一点点亮的东西"，
/// 必须先分清到底是**相机参数被卡住**还是**照明本身不够**，以及
/// **视野到底有多大**（决定角向基准孔在不在画面内）。
/// 这三件事都不是靠猜能解决的，需要向相机直接要真实参数范围。
///
/// 不放进 ICameraService 主接口的原因：主接口是"加工必需能力"，
/// 诊断能力是"现场排查用"，模拟相机可以不实现（UI 里用 `is` 判断即可）。
/// </summary>
public interface ICameraDiagnostics
{
    /// <summary>读取相机真实支持的参数范围（曝光/增益/黑电平/gamma/Binning/ROI/传感器尺寸）。</summary>
    /// <returns>可直接显示给用户的多行文本；相机不可用时返回错误说明。</returns>
    string GetParameterRanges();

    /// <summary>
    /// 自动曝光扫描：按给定曝光序列逐档拍图，统计每档的平均灰度与峰值灰度。
    /// 用于一次性摸清"当前照明下多长曝光才够亮"，避免人工反复试（现场每次试都要重新摆工件）。
    /// </summary>
    /// <param name="exposureListUs">曝光序列（微秒），升序</param>
    Task<List<ExposureProbe>> ScanExposureAsync(IEnumerable<double> exposureListUs);
}

/// <summary>单档曝光的扫描结果。</summary>
/// <param name="ExposureUs">曝光时间（微秒）</param>
/// <param name="MeanGray">该档图像平均灰度（0~255）</param>
/// <param name="MaxGray">该档图像峰值灰度（0~255）</param>
/// <param name="Ok">该档是否成功采集</param>
public sealed record ExposureProbe(double ExposureUs, double MeanGray, double MaxGray, bool Ok);
