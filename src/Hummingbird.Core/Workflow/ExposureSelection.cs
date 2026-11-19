namespace Hummingbird.Core;

public sealed record ExposureQuality(double Score, double DarkFraction, double SaturatedFraction,
    double Contrast, bool? FeatureDetected = null, string Detail = "");
public sealed record ExposureTrial(double ExposureUs, ExposureQuality Quality);
public sealed record ExposureSelection(CameraFrame Frame, double ExposureUs, IReadOnlyList<ExposureTrial> Trials);

/// <summary>有限档曝光搜索；图像评分不代替定位算法的最终检查。</summary>
public static class ExposureSelectionPolicy
{
    public static double[] Candidates(double basis)
    {
        if (!double.IsFinite(basis) || basis < 20 || basis > 1000000)
            throw new ArgumentOutOfRangeException(nameof(basis));
        return new[] { 1.0, .25, .5, 2, 4 }.Select(f => Math.Clamp(basis * f, 20, 1000000)).Distinct().ToArray();
    }

    public static ExposureQuality Measure(CameraFrame frame, DatumCircleSeed? region = null)
    {
        if (frame.Width < 32 || frame.Height < 32 || frame.Gray.Length != (long)frame.Width * frame.Height)
            throw new InvalidDataException("曝光选优收到无效灰度图。");
        int left = 0, top = 0, right = frame.Width, bottom = frame.Height;
        if (region is not null)
        {
            if (!double.IsFinite(region.Px) || !double.IsFinite(region.Py) || !double.IsFinite(region.RadiusPx) || region.RadiusPx <= 0)
                throw new InvalidDataException("曝光选优范围无效。");
            left = (int)Math.Clamp(region.Px - region.RadiusPx * 1.3, 0, frame.Width - 1);
            right = (int)Math.Clamp(region.Px + region.RadiusPx * 1.3, left + 1, frame.Width);
            top = (int)Math.Clamp(region.Py - region.RadiusPx * 1.3, 0, frame.Height - 1);
            bottom = (int)Math.Clamp(region.Py + region.RadiusPx * 1.3, top + 1, frame.Height);
        }
        var histogram = new int[256]; int count = 0;
        for (int y = top; y < bottom; y += 2)
        for (int x = left; x < right; x += 2) { histogram[frame.Gray[y * frame.Width + x]]++; count++; }
        int Percentile(double fraction)
        {
            int sum = 0;
            for (int i = 0; i < 256; i++) { sum += histogram[i]; if (sum >= count * fraction) return i; }
            return 255;
        }
        double dark = histogram.Take(6).Sum() / (double)count;
        double saturated = histogram.Skip(250).Sum() / (double)count;
        double contrast = Percentile(.95) - Percentile(.05);
        return new(contrast * (1 - dark) * (1 - saturated), dark, saturated, contrast);
    }

    public static bool Better(ExposureQuality candidate, ExposureQuality current)
    {
        int Rank(bool? value) => value == true ? 2 : value is null ? 1 : 0;
        return Rank(candidate.FeatureDetected) > Rank(current.FeatureDetected) ||
            Rank(candidate.FeatureDetected) == Rank(current.FeatureDetected) && candidate.Score > current.Score;
    }
}
