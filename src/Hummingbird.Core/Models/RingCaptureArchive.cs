using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hummingbird.Core;

[JsonConverter(typeof(JsonStringEnumConverter<AxisCoordinateSource>))]
public enum AxisCoordinateSource { Unknown, ManualPanelEntry, OpcUaReadback, Simulator }

public sealed record RingCaptureMetadata(DateTime? HostCaptureTime, int ImageWidth, int ImageHeight,
    AxisCoordinateSource XyzSource, AxisCoordinateSource CSource, string? CNode,
    string ImageSha256, RingScanSettings SettingsAtRegistration, string Purpose)
{
    public bool EditedAfterCapture { get; init; }
    public double? RequestedExposureUs {get;init;}
    public double? RequestedGain {get;init;}
    public string? CameraParameterSource {get;init;}
    public string? DeviceMode {get;init;}
}

/// <summary>只保存和验证照片/坐标证据，不调用任何机床或激光接口。</summary>
public static class RingCaptureArchive
{
    public const string AutosaveName = "hummingbird_capture_session.autosave.json";

    public static RingScanFrame BindImage(RingScanFrame frame, RingScanSettings settings,
        DateTime? hostCaptureTime, int width, int height, AxisCoordinateSource xyzSource,
        AxisCoordinateSource cSource, string? cNode, string purpose)
    {
        if (new[] { frame.X, frame.Y, frame.Z, frame.C }.Any(v => !double.IsFinite(v)) ||
            string.IsNullOrWhiteSpace(frame.Name) || width <= 0 || height <= 0)
            throw new InvalidDataException("照片记录名称、坐标或图像尺寸无效。");
        settings.Validate();
        if (cSource == AxisCoordinateSource.OpcUaReadback && string.IsNullOrWhiteSpace(cNode))
            throw new InvalidDataException("C自动读回必须记录实际反馈节点。");
        // 克隆当前输入的标定参数，后续界面编辑不得改变已经保存的证据。
        var copy = JsonSerializer.Deserialize<RingScanSettings>(JsonSerializer.Serialize(settings))!;
        return frame with { Capture = new RingCaptureMetadata(hostCaptureTime, width, height,
            xyzSource, cSource, cSource == AxisCoordinateSource.OpcUaReadback ? cNode : null,
            Hash(frame.ImagePath), copy, purpose) };
    }

    public static string SidecarPath(string imagePath) => Path.ChangeExtension(Path.GetFullPath(imagePath), ".capture.json");

    public static void VerifyImage(RingScanFrame frame)
    {
        if (!File.Exists(frame.ImagePath)) throw new FileNotFoundException("记录对应的原图不存在。", frame.ImagePath);
        if (frame.Capture is { } evidence &&
            !string.Equals(evidence.ImageSha256, Hash(frame.ImagePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原图内容与采图记录不一致，请核对是否重命名错配或覆盖了原图。");
    }

    public static string SaveSidecar(RingScanFrame frame, RingScanSettings fallbackSettings, DatumDirectionOptions? directionOptions = null)
    {
        VerifyImage(frame);
        string path = SidecarPath(frame.ImagePath);
        new RingScanSession { Settings = frame.Capture?.SettingsAtRegistration ?? fallbackSettings,
            DirectionOptions = directionOptions ?? new(), Frames = [frame] }.Save(path);
        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
