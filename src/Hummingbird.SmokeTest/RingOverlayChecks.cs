using Hummingbird.Core;

internal static class RingOverlayChecks
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            passed++; Console.WriteLine("[PASS] " + name);
        }
        var settings = new RingScanSettings();
        const double centerX = -147.2222, centerY = -196.4337, radius = 14.4665;
        // 四帧分别对着圆环的12、3、6、9点方向。
        var frames = new[] { 90, 0, 270, 180 }.Select((deg, i) => new RingScanFrame("圆环" + i, i + ".bmp",
            centerX + radius * Math.Cos(deg * Math.PI / 180), centerY + radius * Math.Sin(deg * Math.PI / 180), 0, 0)).ToArray();
        var (mx, my) = settings.ToMachine(frames[0], 321.5, 876.25);
        var (px, py) = settings.ToPixel(frames[0], mx, my);
        Check(Math.Abs(px - 321.5) < 1e-9 && Math.Abs(py - 876.25) < 1e-9, "机床坐标换算回像素与原像素一致");

        // 每帧放三个点：两个在圆上，一个偏出0.05mm；帧序号故意与帧的位置不对应。
        RingEdgePoint Point(int frame, double offsetDeg, double extraRadius)
        {
            double angle = (new[] { 90, 0, 270, 180 }[frame] + offsetDeg) * Math.PI / 180;
            double x = centerX + (radius + extraRadius) * Math.Cos(angle), y = centerY + (radius + extraRadius) * Math.Sin(angle);
            var (u, v) = settings.ToPixel(frames[frame], x, y);
            return new RingEdgePoint(99, u, v, x, y);
        }
        var points = Enumerable.Range(0, 4).SelectMany(f => new[] { Point(f, -1, 0), Point(f, 1, 0), Point(f, 0, 0.05) }).ToArray();
        var fit = new RingScanResult(centerX, centerY, radius, 0.02, 0.006, 0, 0, points, []);
        var overlays = frames.Select(f => RingFrameOverlay.Build(settings, f, fit)).ToArray();
        Check(overlays.All(o => o.EdgePoints.Count == 3) && overlays.Select((o, i) => o.EdgePoints.SequenceEqual(points.Skip(i * 3).Take(3).Select(p => (p.Px, p.Py)))).All(same => same),
            "每帧只显示这一帧自己取到的边缘点，不依赖拟合结果里的帧序号");
        Check(overlays.All(o => o.FittedArcs.Count == 1 && o.FittedArcs[0].Count > 100 && o.FittedArcs[0].All(p => p.Px >= 0 && p.Py >= 0 && p.Px <= settings.ImageWidth - 1 && p.Py <= settings.ImageHeight - 1)),
            "每帧的拟合圆是画面内连续的一段，包括跨过0°的那一帧");
        Check(overlays.All(o => o.FittedArcs[0].Zip(o.FittedArcs[0].Skip(1)).All(pair => Math.Abs(pair.First.Px - pair.Second.Px) + Math.Abs(pair.First.Py - pair.Second.Py) < 10)),
            "拟合圆相邻采样点之间不到10像素，连线不会横穿画面");
        double Distance(RingFrameOverlay overlay, (double Px, double Py) point) => overlay.FittedArcs[0].Min(p => Math.Sqrt(Math.Pow(p.Px - point.Px, 2) + Math.Pow(p.Py - point.Py, 2)));
        double pixelsPerMm = Math.Sqrt(Math.Abs(settings.Jxx * settings.Jyy - settings.Jxy * settings.Jyx));
        Check(overlays.All(o => Distance(o, o.EdgePoints[0]) < 2 && Distance(o, o.EdgePoints[1]) < 2 && Math.Abs(Distance(o, o.EdgePoints[2]) - 0.05 * pixelsPerMm) < 2),
            "圆上的点落在画出的拟合圆上，偏出0.05mm的点离线约0.05mm对应的像素数");
        var away = RingFrameOverlay.Build(settings, new RingScanFrame("别处", "x.bmp", centerX, centerY, 0, 0), fit);
        Check(away.EdgePoints.Count == 0 && away.FittedArcs.Count == 0, "画面里没有圆环时不画任何内容");
        Console.WriteLine($"Ring overlay checks: {passed} passed.");
        return 0;
    }
}
