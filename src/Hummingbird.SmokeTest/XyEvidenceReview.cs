using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;
using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

/// <summary>离线测量已有取证的像素位移；不创建设备或输出运动命令。</summary>
internal static class XyEvidenceReview
{
    internal sealed record PatchMotion(int Cx,int Cy,double Dx,double Dy,double Correlation,double Uniqueness);
    internal sealed record Motion(string Reference,string Current,double Dx,double Dy,double MaxPatchSpreadPx,IReadOnlyList<PatchMotion> Patches);
    internal sealed record Sample(string File,RingScanFrame Frame);
    internal sealed record Group(string Zip,List<Sample> Images);
    public static int Run(string input,string output,string? previousSession)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(input));
        var groups=document.RootElement.GetProperty("Groups").EnumerateArray().Select(g=>new Group(g.GetProperty("Zip").GetString()!,
            g.GetProperty("Images").EnumerateArray().Select(im=>new Sample(im.GetProperty("File").GetString()!,
                im.GetProperty("Evidence").GetProperty("Frame").Deserialize<RingScanFrame>()!)).ToList())).ToList();
        if(groups.Count!=4)throw new InvalidDataException("此核对入口需要原位两组及两个独立位移组。");
        var testSource=groups[1].Images.First(s=>s.Frame.Capture?.RequestedExposureUs==500);
        string testFile=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,"算法自检_合成平移_非现场照片.bmp");
        using(var original=Cv2.ImDecode(File.ReadAllBytes(testSource.File),ImreadModes.Grayscale))
        using(var shifted=new Mat())
        using(var transform=Mat.FromArray(new double[,]{{1,0,31.25},{0,1,-19.5}}))
        {
            Cv2.WarpAffine(original,shifted,transform,original.Size(),InterpolationFlags.Linear,BorderTypes.Reflect101);
            Cv2.ImEncode(".bmp",shifted,out byte[] bytes);File.WriteAllBytes(testFile,bytes);
        }
        var synthetic=new Sample(testFile,testSource.Frame with {ImagePath=testFile,Capture=testSource.Frame.Capture! with {
            ImageSha256=LaserSpotCalibration.Hash(testFile),DeviceMode="SyntheticImageTranslation"}});
        var selfCheck=Measure(testSource,synthetic);
        double selfError=double.Hypot(selfCheck.Dx-31.25,selfCheck.Dy+19.5);
        if(selfError>.15)throw new InvalidDataException($"合成已知平移的算法自检失败：{selfError:F4}px。");
        var baseline=groups[1].Images[0].Frame;
        var b=groups[2].Images[0].Frame;var c=groups[3].Images[0].Frame;
        double bx=b.X-baseline.X,by=b.Y-baseline.Y,cx=c.X-baseline.X,cy=c.Y-baseline.Y;
        double determinant=bx*cy-by*cx;
        if(Math.Abs(determinant)<1e-5)throw new InvalidDataException("两个位移方向不独立，不能求二维响应。");
        var results=new List<object>();
        foreach(double exposure in new[]{500.0,1000,2000,4000,8000})
        {
            Sample First(int group)=>groups[group].Images.First(s=>s.Frame.Capture?.RequestedExposureUs==exposure);
            try
            {
                var still=Measure(First(0),First(1));
                var x=Measure(First(1),First(2));var y=Measure(First(1),First(3));
                double jxx=(x.Dx*cy-y.Dx*by)/determinant,jxy=(-x.Dx*cx+y.Dx*bx)/determinant;
                double jyx=(x.Dy*cy-y.Dy*by)/determinant,jyy=(-x.Dy*cx+y.Dy*bx)/determinant;
                results.Add(new {ExposureUs=exposure,MeasurementsAvailable=true,
                    TranslationConsistentWithinQuarterPixel=new[]{still,x,y}.All(m=>m.MaxPatchSpreadPx<=.25),
                    Stationary=still,MoveB=x,MoveC=y,Jxx=jxx,Jxy=jxy,Jyx=jyx,Jyy=jyy});
                Console.WriteLine(FormattableString.Invariant($"{exposure:F0}us: still({still.Dx:F4},{still.Dy:F4}); B({x.Dx:F4},{x.Dy:F4}); C({y.Dx:F4},{y.Dy:F4}); J=[{jxx:F6},{jxy:F6};{jyx:F6},{jyy:F6}]"));
            }
            catch(InvalidDataException ex){results.Add(new {ExposureUs=exposure,MeasurementsAvailable=false,Rejection=ex.Message});Console.WriteLine(exposure+"us: "+ex.Message);}
        }
        var repeatMotions=new List<Motion>();
        foreach(var group in groups)
        {
            var repeats=group.Images.Where(s=>Path.GetFileName(s.File).Contains("重复_")).ToArray();
            foreach(var sample in repeats.Skip(1))repeatMotions.Add(Measure(repeats[0],sample));
        }
        object? priorComparison=null;
        if(previousSession is not null)
        {
            var old=RingScanSession.Load(Path.GetFullPath(previousSession)).Frames.Single(f=>f.Role==RingFrameRole.Reference11);
            var current=groups[1].Images.First(s=>s.Frame.Capture?.RequestedExposureUs==old.Capture?.RequestedExposureUs);
            try{priorComparison=Measure(new Sample(old.ImagePath,old),current);}
            catch(InvalidDataException ex){priorComparison=new {Rejection=ex.Message};}
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new {Kind="XyPixelResponseReview",HardwareAccess=false,PreviewOnly=true,
            Source=input,DisplacementB=new {X=bx,Y=by},DisplacementC=new {X=cx,Y=cy},Determinant=determinant,
            KnownTranslationSelfCheck=new {Synthetic=true,ExpectedDx=31.25,ExpectedDy=-19.5,ErrorPx=selfError,Measurement=selfCheck},
            Results=results,RepeatMotions=repeatMotions,PreviousSixFrameComparison=priorComparison},new JsonSerializerOptions {WriteIndented=true}));
        return 0;
    }

    internal static Motion Measure(Sample reference,Sample current)
    {
        foreach(var sample in new[]{reference,current})RingCaptureArchive.VerifyImage(sample.Frame with {ImagePath=sample.File});
        using var a=Cv2.ImDecode(File.ReadAllBytes(reference.File),ImreadModes.Grayscale);
        using var b=Cv2.ImDecode(File.ReadAllBytes(current.File),ImreadModes.Grayscale);
        if(a.Empty()||b.Empty()||a.Size()!=b.Size())throw new InvalidDataException("像素匹配图像尺寸无效。");
        var motions=new List<PatchMotion>();const int half=120,margin=180;
        foreach(var (x,y) in new[]{(800,430),(1100,430),(800,750),(1100,750),(960,600)})
        {
            using var patch=new Mat(a,new Rect(x-half,y-half,2*half,2*half));
            Cv2.MeanStdDev(patch,out _,out Scalar deviation);if(deviation.Val0<8)continue;
            using var search=new Mat(b,new Rect(x-half-margin,y-half-margin,2*(half+margin),2*(half+margin)));
            using var match=new Mat();Cv2.MatchTemplate(search,patch,match,TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(match,out _,out double peak,out _,out Point location);
            if(location.X<=0||location.Y<=0||location.X>=match.Width-1||location.Y>=match.Height-1)continue;
            using var alternatives=match.Clone();
            int left=Math.Max(0,location.X-5),top=Math.Max(0,location.Y-5);
            Cv2.Rectangle(alternatives,new Rect(left,top,Math.Min(match.Width,location.X+6)-left,Math.Min(match.Height,location.Y+6)-top),Scalar.All(-1),-1);
            Cv2.MinMaxLoc(alternatives,out _,out double second);
            if(peak<.95 || peak-second<.02)continue;
            double Sub(float low,float middle,float high)
            {double d=low-2*middle+high;return Math.Abs(d)<1e-10?0:Math.Clamp(.5*(low-high)/d,-1,1);}
            double dx=location.X-margin+Sub(match.At<float>(location.Y,location.X-1),match.At<float>(location.Y,location.X),match.At<float>(location.Y,location.X+1));
            double dy=location.Y-margin+Sub(match.At<float>(location.Y-1,location.X),match.At<float>(location.Y,location.X),match.At<float>(location.Y+1,location.X));
            motions.Add(new(x,y,dx,dy,peak,peak-second));
        }
        if(motions.Count<3)throw new InvalidDataException("至少三个局部图块的匹配未通过。");
        double Median(IEnumerable<double> values){var sorted=values.Order().ToArray();return sorted[sorted.Length/2];}
        double mx=Median(motions.Select(m=>m.Dx)),my=Median(motions.Select(m=>m.Dy));
        double spread=motions.Max(m=>double.Hypot(m.Dx-mx,m.Dy-my));
        return new(reference.File,current.File,mx,my,spread,motions);
    }
}
