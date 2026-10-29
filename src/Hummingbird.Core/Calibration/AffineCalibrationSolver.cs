namespace Hummingbird.Core;

public interface ICalibrationSolver
{
    /// <summary>最小二乘拟合像素→机床平面仿射变换（推荐9点）。</summary>
    CalibrationResult Fit(IReadOnlyList<CalibPointPair> pairs);
}

/// <summary>仿射（6参数）最小二乘标定求解器：Mx=A*px+B*py+C, My=D*px+E*py+F。</summary>
public sealed class AffineCalibrationSolver : ICalibrationSolver
{
    public CalibrationResult Fit(IReadOnlyList<CalibPointPair> pairs)
    {
        if (pairs.Count < 3)
            return Invalid("点数不足（仿射至少3点，推荐9点）");

        var rx = Solve3(pairs.Select(p => (p.Px, p.Py, p.Mx)));
        var ry = Solve3(pairs.Select(p => (p.Px, p.Py, p.My)));
        if (!rx.Ok || !ry.Ok)
            return Invalid("法方程奇异：点位共线或重合，请检查录入数据");

        double sq = 0;
        foreach (var p in pairs)
        {
            double dx = rx.A * p.Px + rx.B * p.Py + rx.C - p.Mx;
            double dy = ry.A * p.Px + ry.B * p.Py + ry.C - p.My;
            sq += dx * dx + dy * dy;
        }
        double rms = Math.Sqrt(sq / pairs.Count);

        return rms <= 0.05
            ? new CalibrationResult(true, rms, rx.A, rx.B, rx.C, ry.A, ry.B, ry.C)
              { Note = $"RMS={rms:F4}mm（{pairs.Count}点）" }
            : new CalibrationResult(false, rms, rx.A, rx.B, rx.C, ry.A, ry.B, ry.C)
              { Note = $"RMS={rms:F4}mm 超过0.05mm阈值，请检查点位" };

        static CalibrationResult Invalid(string why)
            => new(false, double.NaN, 0, 0, 0, 0, 0, 0) { Note = why };
    }

    /// <summary>解 z = a*x + b*y + c 的法方程（Cramer法则）。</summary>
    private static (double A, double B, double C, bool Ok) Solve3(IEnumerable<(double x, double y, double z)> pts)
    {
        double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0, n = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var (x, y, z) in pts)
        {
            sxx += x * x; sxy += x * y; sx += x; syy += y * y; sy += y; n++;
            sxz += x * z; syz += y * z; sz += z;
        }
        double d = Det(sxx, sxy, sx, sxy, syy, sy, sx, sy, n);
        if (Math.Abs(d) < 1e-9) return (0, 0, 0, false);
        double a = Det(sxz, sxy, sx, syz, syy, sy, sz, sy, n) / d;
        double b = Det(sxx, sxz, sx, sxy, syz, sy, sx, sz, n) / d;
        double c = Det(sxx, sxy, sxz, sxy, syy, syz, sx, sy, sz) / d;
        return (a, b, c, true);
    }

    private static double Det(double a1, double a2, double a3, double b1, double b2, double b3, double c1, double c2, double c3)
        => a1 * (b2 * c3 - b3 * c2) - a2 * (b1 * c3 - b3 * c1) + a3 * (b1 * c2 - b2 * c1);
}
