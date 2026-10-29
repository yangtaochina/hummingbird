namespace Hummingbird.Core;

/// <summary>
/// 视觉定位：在一张图中找到表盘（中心 + 角向零位偏角）。
/// 定心基准=外轮廓整圆拟合；角向基准=外圈零位特征（缺口）。
/// </summary>
public interface IVisionService
{
    Task<PartPose> LocateAsync(CameraFrame frame, VisionOptions options, CancellationToken ct = default);
}
