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
            LastRotationDeg = _rnd.NextDouble() * 16 - 8;  // ±8°
            _hasPose = true;
        }

        double s = 1 / PixelSizeMm;
        double cxPx = Width / 2.0 + LastOffsetXmm * s;
        double cyPx = Height / 2.0 - LastOffsetYmm * s;    // 图像 y 向下：+Y(12H) 在图上朝上
        double rOuter = 34.5 / 2 * s;
        var c = new Point((int)cxPx, (int)cyPx);

        using var img = new Mat(Height, Width, MatType.CV_8UC1, Scalar.All(25));
        Cv2.Circle(img, c, (int)rOuter, Scalar.All(190), -1);                      // 盘体
        Cv2.Circle(img, c, (int)(2.3 / 2 * s), Scalar.All(40), -1);                // 中心孔

        double rot = Math.PI / 180 * LastRotationDeg;
        foreach (var h in Template?.Holes ?? new List<HoleTemplate>())
        {
            double x = h.X * Math.Cos(rot) + h.Y * Math.Sin(rot);
            double y = -h.X * Math.Sin(rot) + h.Y * Math.Cos(rot);
            Cv2.Circle(img, new Point((int)(cxPx + x * s), (int)(cyPx - y * s)), 4, Scalar.All(90), -1);
        }

        // 零位缺口：名义 12H + 装卡转角 处的外缘凹陷
        double notch = Math.PI / 180 * ((0 + LastRotationDeg) % 360);
        var nBase = new Point((int)(cxPx + Math.Sin(notch) * rOuter), (int)(cyPx - Math.Cos(notch) * rOuter));
        Cv2.Circle(img, nBase, (int)(1.2 * s), Scalar.All(25), -1);

        using var noise = new Mat(Height, Width, MatType.CV_8UC1);
        Cv2.Randn(noise, Scalar.All(0), Scalar.All(6));
        using var outMat = new Mat();
        Cv2.Add(img, noise, outMat);

        var buf = new byte[Width * Height];
        Marshal.Copy(outMat.Data, buf, 0, buf.Length);
        return Task.FromResult(new CameraFrame(buf, Width, Height, DateTime.Now));
    }
}
