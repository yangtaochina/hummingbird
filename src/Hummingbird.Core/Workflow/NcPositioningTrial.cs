using System.Globalization;

namespace Hummingbird.Core;

/// <summary>生成由机床面板启动的单轴往返采图程序。</summary>
public static class NcPositioningTrial
{
    public static string Generate(string axis, decimal step)
    {
        decimal limit = axis switch
        {
            "X" or "Y" => 0.20m,
            "C" => 1.00m,
            _ => throw new ArgumentException("试走轴只支持 X、Y、C。", nameof(axis))
        };

        if (Math.Abs(step) < 0.001m || Math.Abs(step) > limit || step != Math.Round(step, 3))
            throw new ArgumentOutOfRangeException(nameof(step), $"步长绝对值须为 0.001 至 {limit}，最多三位小数。");

        string forward = step.ToString("+0.000;-0.000", CultureInfo.InvariantCulture);
        string backward = (-step).ToString("+0.000;-0.000", CultureInfo.InvariantCulture);
        return $"""
            ; HUMMINGBIRD SINGLE AXIS CAMERA TRIAL
            N10 G71 G94 G60
            N20 G91 G1 {axis}{forward} F6
            N30 G90 M0
            N40 G91 G1 {axis}{backward} F6
            N50 G90 M0
            N60 M30
            """.ReplaceLineEndings("\r\n") + "\r\n";
    }
}
