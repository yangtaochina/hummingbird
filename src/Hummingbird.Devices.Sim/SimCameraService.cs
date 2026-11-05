using System.Runtime.InteropServices;
using Hummingbird.Core;
using OpenCvSharp;

namespace Hummingbird.Devices.Sim;

/// <summary>
/// 模拟相机：按模板渲染一张带随机偏移/旋转的表盘灰度图。
/// FreezePose=true 后位姿冻结（标定演示用：同一偏移下反复拍图）。
/// LastOffset/LastRotation 暴露真值，供自动测试断言。
/// </summary>
public sealed class SimCameraService : ICameraService
{
    public string DeviceName => "模拟相机（合成表盘图）";
    public bool IsOpen { get; private set; }

    public int Width { get; set; } = 2448;
    public int Height { get; set; } = 2048;
    public double PixelSizeMm { get; set; } = 0.02;
    public HolePatternTemplate? Template { get; set; }
    public bool FreezePose { get; set; }

    public double LastOffsetXmm { get; private set; }
    public double LastOffsetYmm { get; private set; }
    public double LastRotationDeg { get; private set; }

    /// <summary>
    /// 角向基准：工件自带定位孔组的**名义时钟角**（度，12H=0，顺时针）。
    /// 现场事实：这些孔是工件出厂自带的（图纸 R14.45 处、每个带 Ø0.8 倒角口 → 呈"双环"），
    /// **不是**待加工孔。模拟时按名义角 + 转角 渲染出来，供 datumHoles 模式匹配。
    /// </summary>
    public IReadOnlyList<double> DatumHoleAngles { get; set; } = Array.Empty<double>();

    /// <summary>自带定位孔的半径（mm），图纸标称 14.45</summary>
    public double DatumHoleRadiusMm { get; set; } = 14.45;

    /// <summary>自带定位孔的孔径（mm），图纸标称 0.30</summary>
    public double DatumHoleDiameterMm { get; set; } = 0.30;

    public double ExposureUs { get; private set; } = 8000;
    public double Gain { get; private set; } = 1;

    private readonly Random _rnd = new(20261029);
    private bool _hasPose;

    public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
    public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }
    public Task SetExposureUsAsync(double us) { ExposureUs = us; return Task.CompletedTask; }
    public Task SetGainAsync(double g) { Gain = g; return Task.CompletedTask; }
    public void Dispose() { }

    public Task<CameraFrame> GrabAsync(int timeoutMs = 3000)
    {
        if (!FreezePose || !_hasPose)
        {
            LastOffsetXmm = _rnd.NextDouble() * 4 - 2;    // ±2mm
            LastOffsetYmm = _rnd.NextDouble() * 4 - 2;
            // 真实装夹：操作工按 12H 大致摆好（±5° 粗对齐误差），不是任意角度
            LastRotationDeg = _rnd.NextDouble() * 10 - 5;
            _hasPose = true;
        }

        double s = 1 / PixelSizeMm;
        double cxPx = Width / 2.0 + LastOffsetXmm * s;
        double cyPx = Height / 2.0 - LastOffsetYmm * s;    // 图像 y 向下：+Y(12H) 在图上朝上
        double rOuter = 34.2 / 2 * s;
        var c = new Point((int)cxPx, (int)cyPx);

        using var img = new Mat(Height, Width, MatType.CV_8UC1, Scalar.All(25));
        // 结构对照实物：金棕颗粒盘面 + 白色外环（大圈圈）+ 中心孔；渠口切在外缘
        Cv2.Circle(img, c, (int)rOuter, Scalar.All(230), -1);                     // 白色外环（Ø34.2 最外）
        Cv2.Circle(img, c, (int)(15.5 * s), Scalar.All(150), -1);                // 金棕颗粒盘面
        Cv2.Circle(img, c, (int)(2.3 / 2 * s), Scalar.All(40), -1);              // 中心孔

        double rot = Math.PI / 180 * LastRotationDeg;
        // 定位阶段工件上只有"自带孔"，待加工的 18 孔此刻还不存在 → 不渲染，
        // 否则它们会污染 datumHoles 基准检测（半径不同，可由半径约束排除，但仍以真实为准）。
        if (DatumHoleAngles.Count == 0)
        {
            foreach (var h in Template?.Holes ?? new List<HoleTemplate>())
            {
                double x = h.X * Math.Cos(rot) + h.Y * Math.Sin(rot);
                double y = -h.X * Math.Sin(rot) + h.Y * Math.Cos(rot);
                Cv2.Circle(img, new Point((int)(cxPx + x * s), (int)(cyPx - y * s)), 4, Scalar.All(90), -1);
            }
        }

        // 8 个渠口（现场 2026-10-30 核实：不均匀分布；以下角度为模拟假设值，待实物测量后替换）。
        // 注意与定位孔组保持 ≥15° 角距：渠口切入白环 0.9R 深处，贴得太近会把孔位特征
        // 粘连成长条导致检测失败（算法已加渠口扇区排除兜底，但物理上贴住是真解不开的）
        double[] notches = { 15, 52, 88, 133, 181, 226, 274, 318 };
        foreach (var n in notches)
        {
            double a = Math.PI / 180 * ((n + LastRotationDeg) % 360);
            var np = new Point((int)(cxPx + Math.Sin(a) * rOuter), (int)(cyPx - Math.Cos(a) * rOuter));
            Cv2.Circle(img, np, (int)(1.2 * s), Scalar.All(25), -1);
        }

        // 角向基准（现役 datumHoles 模式）：工件自带的定位孔组——随工件转动，
        // 名义角 + 转角即实测角。暗色小孔 + 外圈倒角口（双环感）。
        foreach (var nominalClock in DatumHoleAngles)
        {
            double clock = (nominalClock + LastRotationDeg) % 360;
            double a = Math.PI / 180 * clock;
            var hp = new Point((int)(cxPx + Math.Sin(a) * DatumHoleRadiusMm * s),
                               (int)(cyPx - Math.Cos(a) * DatumHoleRadiusMm * s));
            Cv2.Circle(img, hp, Math.Max(2, (int)(DatumHoleDiameterMm / 2 * s)), Scalar.All(45), -1);
        }

        using var noise = new Mat(Height, Width, MatType.CV_8UC1);
        Cv2.Randn(noise, Scalar.All(0), Scalar.All(6));
        using var outMat = new Mat();
        Cv2.Add(img, noise, outMat);

        var buf = new byte[Width * Height];
        Marshal.Copy(outMat.Data, buf, 0, buf.Length);
        return Task.FromResult(new CameraFrame(buf, Width, Height, DateTime.Now));
    }
}
