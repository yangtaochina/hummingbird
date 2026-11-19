using System.Security.Cryptography;
using System.Text.Json;

namespace Hummingbird.Core;

/// <summary>激光落点的可追溯预览标定，与工件圆心/方向分别保存，不启用真实加工。</summary>
public sealed record LaserSpotCalibration
{
    public string Kind { get; init; } = "LaserSpotPreviewCalibration";
    public int Version { get; init; } = 1;
    public bool PreviewOnly => true;
    public required string SourceTrialPath { get; init; }
    public required string SourceTrialSha256 { get; init; }
    public required string BeforeImagePath { get; init; }
    public required string BeforeImageSha256 { get; init; }
    public required string AfterImagePath { get; init; }
    public required string AfterImageSha256 { get; init; }
    public required RingScanSettings OpticalSettings { get; init; }
    public double PixelX { get; init; }
    public double PixelY { get; init; }
    public double ReferenceZ { get; init; }
    public double ReferenceC { get; init; }
    public string Method { get; init; } = "";
    public string Notes { get; init; } = "";

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static LaserSpotCalibration Load(string path)
    {
        var value = JsonSerializer.Deserialize<LaserSpotCalibration>(File.ReadAllText(path))
            ?? throw new InvalidDataException("激光落点文件为空。");
        value.Validate();
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        value = value with
        {
            SourceTrialPath = Path.GetFullPath(value.SourceTrialPath, folder),
            BeforeImagePath = Path.GetFullPath(value.BeforeImagePath, folder),
            AfterImagePath = Path.GetFullPath(value.AfterImagePath, folder)
        };
        value.VerifySources();
        return value;
    }

    public void Save(string path)
    {
        VerifySources();
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var relative = this with
        {
            SourceTrialPath = Path.GetRelativePath(folder, SourceTrialPath),
            BeforeImagePath = Path.GetRelativePath(folder, BeforeImagePath),
            AfterImagePath = Path.GetRelativePath(folder, AfterImagePath)
        };
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(relative, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Validate()
    {
        if (Kind != "LaserSpotPreviewCalibration" || Version != 1 || OpticalSettings is null)
            throw new InvalidDataException("不是受支持的激光落点预览标定文件。");
        OpticalSettings.Validate();
        if (new[] { PixelX, PixelY, ReferenceZ, ReferenceC }.Any(v => !double.IsFinite(v)) ||
            PixelX < 0 || PixelY < 0 || PixelX >= OpticalSettings.ImageWidth || PixelY >= OpticalSettings.ImageHeight)
            throw new InvalidDataException("激光落点像素或参考Z/C无效。");
        foreach (string? value in new[] { SourceTrialPath, BeforeImagePath, AfterImagePath })
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("激光落点缺少原始试打文件路径。");
        foreach (string? value in new[] { SourceTrialSha256, BeforeImageSha256, AfterImageSha256 })
            if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("激光落点缺少有效原始文件SHA256。");
    }

    public void ValidateOptics(RingScanSettings settings)
    {
        Validate(); settings.Validate();
        if (settings.ImageWidth != OpticalSettings.ImageWidth || settings.ImageHeight != OpticalSettings.ImageHeight ||
            !Same(settings.Jxx, OpticalSettings.Jxx) || !Same(settings.Jxy, OpticalSettings.Jxy) ||
            !Same(settings.Jyx, OpticalSettings.Jyx) || !Same(settings.Jyy, OpticalSettings.Jyy))
            throw new InvalidDataException("当前图像尺寸或X/Y像素标定与激光落点来源不一致，请恢复同一光学标定或重新标定落点。");
    }

    public void ValidateReference(double z, double c)
    {
        if (!double.IsFinite(z) || !double.IsFinite(c) || Math.Abs(z - ReferenceZ) > 0.010 ||
            Math.Abs(Math.IEEERemainder(c - ReferenceC, 360)) > 0.005)
            throw new InvalidDataException("定位数据的Z/C与激光落点来源不一致（筛选上限：Z差0.010mm、C差0.005°）。");
    }

    /// <summary>同时核对原始试打记录与两张原图；不能用演练记录冒充出光标定。</summary>
    public void VerifySources()
    {
        Validate();
        VerifyHash(SourceTrialPath, SourceTrialSha256);
        VerifyHash(BeforeImagePath, BeforeImageSha256);
        VerifyHash(AfterImagePath, AfterImageSha256);
        if (BeforeImageSha256.Equals(AfterImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("试打前后是同一张图，不能作为落点来源。");
        using var document = JsonDocument.Parse(File.ReadAllText(SourceTrialPath));
        var trial = document.RootElement;
        if (trial.GetProperty("Version").GetInt32() != 1 || trial.GetProperty("DeviceMode").GetString() != "Real" ||
            trial.GetProperty("DryRun").GetBoolean() || !trial.GetProperty("Completed").GetBoolean() ||
            !trial.GetProperty("EmissionRequested").GetBoolean() || !trial.GetProperty("ShutdownConfirmed").GetBoolean() ||
            trial.GetProperty("Error").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("落点来源必须为完成且关光读回成功的真实试打记录。");
        var optics = trial.GetProperty("SoftwareOpticalSettings").Deserialize<RingScanSettings>()
            ?? throw new InvalidDataException("原始试打记录缺少光学参数。");
        ValidateOptics(optics);
        var start = trial.GetProperty("StartPosition"); var end = trial.GetProperty("EndPosition");
        if (!Same(Number(start, "Z"), ReferenceZ) || !Same(Number(start, "C"), ReferenceC))
            throw new InvalidDataException("落点参考Z/C与原始试打记录不一致。");
        foreach (string axis in new[] { "X", "Y", "Z", "C" })
        {
            double delta = axis == "C" ? Math.IEEERemainder(Number(start, axis) - Number(end, axis), 360)
                : Number(start, axis) - Number(end, axis);
            if (Math.Abs(delta) > (axis == "C" ? 0.001 : 0.002))
                throw new InvalidDataException("原始试打前后轴坐标不稳定。");
        }
        VerifyImage(trial.GetProperty("BeforeImage"), BeforeImagePath, BeforeImageSha256);
        VerifyImage(trial.GetProperty("AfterImage"), AfterImagePath, AfterImageSha256);
    }

    private void VerifyImage(JsonElement image, string path, string hash)
    {
        string name = image.GetProperty("FileName").GetString() ?? "";
        string expected = Path.GetFullPath(name, Path.GetDirectoryName(SourceTrialPath)!);
        if (Path.GetFileName(name) != name || string.IsNullOrWhiteSpace(name) ||
            !string.Equals(expected, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(image.GetProperty("Sha256").GetString(), hash, StringComparison.OrdinalIgnoreCase) ||
            image.GetProperty("Width").GetInt32() != OpticalSettings.ImageWidth ||
            image.GetProperty("Height").GetInt32() != OpticalSettings.ImageHeight)
            throw new InvalidDataException("激光落点原图与试打来源中的文件、哈希或尺寸不一致。");
    }

    private static double Number(JsonElement element, string name)
    {
        double value = element.GetProperty(name).GetDouble();
        return double.IsFinite(value) ? value : throw new InvalidDataException("试打坐标无效。");
    }
    private static bool Same(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= 1e-6;
    private static void VerifyHash(string path, string expected)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("激光落点对应的原始文件不存在。", path);
        if (!Hash(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("激光落点来源文件内容已变化，请重新核对，不能继续沿用旧候选。");
    }
}

public sealed record LaserCompensatedTarget(int Id, double CameraX, double CameraY,
    double LaserCommandX, double LaserCommandY, double DiameterMm, string Clock);

public sealed record LaserCompensationPreview(LaserSpotCalibration Calibration, double OffsetX, double OffsetY,
    double ReferenceZ, double ReferenceC, IReadOnlyList<LaserCompensatedTarget> Targets)
{
    public string Kind => "LaserCompensationPreview";
    public bool PreviewOnly => true;
    public string Status => "落点补偿候选，尚未完成独立现场复核，不启用正式加工。";
}

/// <summary>仅计算和导出人工执行的无出光复核文本，不依赖任何设备接口。</summary>
public static class LaserSpotCompensation
{
    public static (double X, double Y) Offset(LaserSpotCalibration calibration, RingScanSettings settings)
    {
        calibration.ValidateOptics(settings);
        double px = calibration.PixelX - (settings.ImageWidth - 1) / 2.0;
        double py = calibration.PixelY - (settings.ImageHeight - 1) / 2.0;
        double det = settings.Jxx * settings.Jyy - settings.Jxy * settings.Jyx;
        // ToMachine用负逆矩阵恢复实物坐标；将目标移到激光像素处需对轴指令加正逆矩阵。
        return ((settings.Jyy * px - settings.Jxy * py) / det,
            (-settings.Jyx * px + settings.Jxx * py) / det);
    }

    public static LaserCompensationPreview Preview(LaserSpotCalibration calibration, RingScanSettings settings,
        double z, double c, IReadOnlyList<HoleTarget> targets)
    {
        calibration.VerifySources(); calibration.ValidateReference(z, c);
        var offset = Offset(calibration, settings);
        if (targets.Count == 0 || targets.Any(t => !double.IsFinite(t.MachineX) || !double.IsFinite(t.MachineY)))
            throw new InvalidDataException("请先完成当前装夹的孔位预览。");
        return new(calibration, offset.X, offset.Y, z, c, targets.Select(t => new LaserCompensatedTarget(t.Id,
            t.MachineX, t.MachineY, t.MachineX + offset.X, t.MachineY + offset.Y, t.DiameterMm, t.Clock)).ToArray());
    }

    public static string RecenterTrial(LaserSpotCalibration calibration, RingScanSettings settings)
    {
        calibration.VerifySources();
        var offset = Offset(calibration, settings);
        if (double.Hypot(offset.X, offset.Y) > 0.75 || double.Hypot(offset.X, offset.Y) < 0.001)
            throw new InvalidDataException("落点复核只支持0.001至0.750mm的小范围XY往返；该偏移不满足条件。");
        using var document = JsonDocument.Parse(File.ReadAllText(calibration.SourceTrialPath));
        var start = document.RootElement.GetProperty("StartPosition");
        double startX=start.GetProperty("X").GetDouble(),startY=start.GetProperty("Y").GetDouble();
        // 沿用已在现场验证的SUPA绝对MCS方式，避免增量X/Y受当前工件坐标旋转影响。
        return FormattableString.Invariant($"""
            ; HUMMINGBIRD LASER SPOT CAMERA RECENTER CHECK
            ; OPERATOR MUST DISABLE LASER BEFORE START - NO LASER CONTROL HERE
            ; START WITH SAME TRIAL MARK AT ORIGINAL LASER PIXEL
            ; TRIAL MCS X={start.GetProperty("X").GetDouble():F4} Y={start.GetProperty("Y").GetDouble():F4}
            ; VERIFY START MCS MATCHES TRIAL BEFORE RELEASING N20
            ; KEEP Z={calibration.ReferenceZ:F4} C={calibration.ReferenceC:F4} FIXED
            ; N40: EXISTING MARK SHOULD BE AT IMAGE CENTER
            ; N60: MARK SHOULD RETURN TO ORIGINAL LASER PIXEL
            N10 G90 G71 G94 G60
            N20 M0
            N30 SUPA G1 X{startX-offset.X:F4} Y{startY-offset.Y:F4} F6
            N40 G90 M0
            N50 SUPA G1 X{startX:F4} Y{startY:F4} F6
            N60 G90 M0
            N70 M30
            """).ReplaceLineEndings("\r\n") + "\r\n";
    }
}
