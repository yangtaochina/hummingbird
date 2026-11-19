using Hummingbird.Core;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace Hummingbird.Vision;

/// <summary>局部模板匹配只提出有限XY修正，不能替代最终圆边定位。</summary>
public static class XyImageRegistration
{
    public static XyMicroAdjustment Measure(CameraFrame reference, CameraFrame current, RingScanSettings settings, DatumCircleSeed? region)
    {
        settings.Validate();
        if (reference.Width!=current.Width || reference.Height!=current.Height || current.Width!=settings.ImageWidth ||
            current.Height!=settings.ImageHeight || reference.Gray.Length!=(long)reference.Width*reference.Height || current.Gray.Length!=reference.Gray.Length)
            throw new InvalidDataException("微调匹配图像尺寸不一致。");
        using var a = new Mat(reference.Height,reference.Width,MatType.CV_8UC1);
        using var b = new Mat(current.Height,current.Width,MatType.CV_8UC1);
        Marshal.Copy(reference.Gray,0,a.Data,reference.Gray.Length); Marshal.Copy(current.Gray,0,b.Data,current.Gray.Length);
        int margin=(int)Math.Ceiling(Math.Max(Math.Abs(settings.Jxx)+Math.Abs(settings.Jxy),Math.Abs(settings.Jyx)+Math.Abs(settings.Jyy))*.20)+8;
        int half=region is null ? 120 : (int)Math.Ceiling(region.RadiusPx*1.3);
        int cx=region is null ? a.Width/2 : (int)Math.Round(region.Px), cy=region is null ? a.Height/2 : (int)Math.Round(region.Py);
        half=Math.Min(half,Math.Min(Math.Min(cx-margin,a.Width-cx-margin),Math.Min(cy-margin,a.Height-cy-margin)));
        if(half<24)throw new InvalidDataException("模板靠近画面边缘，不能可靠进行有限范围匹配。");
        var patchRect=new Rect(cx-half,cy-half,half*2,half*2);
        var searchRect=new Rect(patchRect.X-margin,patchRect.Y-margin,patchRect.Width+margin*2,patchRect.Height+margin*2);
        using var patch=new Mat(a,patchRect);using var search=new Mat(b,searchRect);
        Cv2.MeanStdDev(patch,out _,out Scalar deviation);
        if(deviation.Val0<8)throw new InvalidDataException("基准图局部纹理不足，不能据此自动移动。");
        using var match=new Mat();Cv2.MatchTemplate(search,patch,match,TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(match,out _,out double peak,out _,out Point location);
        using var alternatives=match.Clone();
        Cv2.Rectangle(alternatives,new Rect(Math.Max(0,location.X-5),Math.Max(0,location.Y-5),
            Math.Min(match.Width,location.X+6)-Math.Max(0,location.X-5),Math.Min(match.Height,location.Y+6)-Math.Max(0,location.Y-5)),Scalar.All(-1),-1);
        Cv2.MinMaxLoc(alternatives,out _,out double second);
        if(!double.IsFinite(peak) || peak<.85 || peak-second<.02)throw new InvalidDataException(FormattableString.Invariant(
            $"图像匹配不唯一或相关性不足，未请求XY运动。相关性={peak:F3}（要求≥0.850），峰值差={peak-second:F3}（要求≥0.020）。"));
        double dx=location.X-margin,dy=location.Y-margin,det=settings.Jxx*settings.Jyy-settings.Jxy*settings.Jyx;
        double x=-(settings.Jyy*dx-settings.Jxy*dy)/det,y=-(-settings.Jyx*dx+settings.Jxx*dy)/det;
        if(double.Hypot(x,y)>XyMicroMoveSession.MaximumOffsetMm)throw new InvalidDataException("识别偏移超过0.20mm微调范围。");
        return new(dx,dy,x,y,peak);
    }
}
