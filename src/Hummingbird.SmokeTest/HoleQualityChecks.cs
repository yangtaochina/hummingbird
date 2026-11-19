using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

internal static class HoleQualityChecks
{
    public static int Run(string? siteFolder)
    {
        int passed=0,failed=0;
        void Check(bool ok,string name)
        { Console.WriteLine($"[{(ok?"PASS":"FAIL")}] {name}");if(ok)passed++;else failed++; }
        string folder=Path.Combine(Path.GetTempPath(),"hummingbird-hole-quality-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var settings=new RingScanSettings();
            var solver=new DatumDirectionSolver();
            var hole=AlignmentChecks.Hole(settings,folder,"valid",RingFrameRole.RingArc,30,780,560);
            var inspection=solver.Inspect(hole,settings);
            Check(inspection.Status==DatumHoleInspectionStatus.Detected && inspection.Candidates.Count==1,
                "单图检查不依赖用途或圆心结果，可检查5点/7点复核帧");
            var detection=inspection.Candidates.Single();
            Check(double.Hypot(detection.Px-780,detection.Py-560)<.2,
                "合成图恢复已知孔心，而非画面中心");
            Check(inspection.PreviewOnly && inspection.Message.Contains("不等于加工精度"),
                "检出成功仍明确是预览筛选，不作为加工精度验收");
            var partial=AlignmentChecks.Hole(settings,folder,"partial",RingFrameRole.Reference1,30,20,560);
            Check(solver.Inspect(partial,settings).Status==DatumHoleInspectionStatus.Rejected,
                "不完整边缘孔不会为了得到结果而强行取心");
            var bright=AlignmentChecks.Hole(settings,folder,"bright",RingFrameRole.Reference1,30,780,560,brightCore:true);
            Check(solver.Inspect(bright,settings).Status==DatumHoleInspectionStatus.Rejected,
                "亮岛内轮廓不能冒充暗孔");
            var multiple=AlignmentChecks.Hole(settings,folder,"multiple",RingFrameRole.Reference1,30,780,560,secondHole:true);
            Check(solver.Inspect(multiple,settings).Status==DatumHoleInspectionStatus.Ambiguous,
                "单图存在两个可行孔时报告歧义，不自动选择其中一个");
            var bound=RingCaptureArchive.BindImage(hole,settings,DateTime.Now,1920,1200,
                AxisCoordinateSource.ManualPanelEntry,AxisCoordinateSource.ManualPanelEntry,null,"FixedCScan");
            var tampered=bound with {Capture=bound.Capture! with {ImageSha256=new string('0',64)}};
            var mismatch=solver.Inspect(tampered,settings);
            Check(mismatch.Status==DatumHoleInspectionStatus.InputError && mismatch.Candidates.Count==0,
                "原图校验不匹配时输出输入错误，没有可用孔心");
            Check(solver.Inspect(hole with {ImagePath=Path.Combine(folder,"missing.bmp")},settings).Status==DatumHoleInspectionStatus.InputError,
                "缺失图片返回可保存的错误报告");
            Check(solver.Inspect(hole with {X=double.NaN},settings).Status==DatumHoleInspectionStatus.InputError,
                "无效坐标不进入图像到机床坐标换算");
            Check(solver.Inspect(hole,settings,new(){CenterSpreadWarningMm=double.NaN}).Status==DatumHoleInspectionStatus.InputError,
                "无效质量参数返回输入错误");
            var restored=JsonSerializer.Deserialize<DatumHoleInspection>(JsonSerializer.Serialize(inspection))!;
            Check(restored.Status==inspection.Status && restored.Candidates.Count==1 && restored.Frame.Name==hole.Name,
                "单图检查报告保存完整原始帧、候选孔和状态");

            if(siteFolder is not null)
            {
                string root=Path.GetFullPath(siteFolder);
                DatumHoleInspection Inspect(string name)
                {
                    var source=RingScanSession.Load(Path.Combine(root,name+".capture.json"));
                    var f=source.Frames.Single() with {ImagePath=Path.Combine(root,name+".bmp")};
                    return solver.Inspect(f,source.Settings);
                }
                var first=Inspect("XY复核_1点首次");
                var returned=Inspect("XY复核_1点返回");
                var later=Inspect("独立核对_1点返回");
                Check(new[]{first,returned,later}.All(v=>v.Status==DatumHoleInspectionStatus.Detected),
                    "现场三张1点图均通过逐轮廓极性筛选");
                var a=first.Candidates.Single();var b=returned.Candidates.Single();var c=later.Candidates.Single();
                double drift=double.Hypot(a.Px-b.Px,a.Py-b.Py);
                Console.WriteLine($"SITE 1-return centers delta={drift:F3}px; spreads={a.CenterSpreadMm*1000:F2}/{b.CenterSpreadMm*1000:F2}/{c.CenterSpreadMm*1000:F2}um");
                Check(drift<1.5,"现场同孔首次/返回识别中心差保持在1.5像素内（旧算法约3.36像素）");
                Check(new[]{a,b,c}.All(h=>h.CenterSpreadMm<.025),
                    "本组三张1点图阈值敏感性均降至25微米以下，仍不宣称加工精度");
                Check(first.Diagnostics.Any(t=>t.Contains("仅用于预览")),
                    "现场剩余孔心敏感性仍触发预警，没有将预览当作精度通过");
                var five=Inspect("独立核对_5点");
                Check(five.Status==DatumHoleInspectionStatus.Rejected && five.Candidates.Count==0,
                    "现场5点边界支持不足仍拒绝，不为通过样例降低条件");
                Check(five.Diagnostics.Any(t=>t.Contains("2个有效阈值支持")),
                    "现场5点报告只有两个有效阈值支持的具体原因");
                Check(Inspect("独立核对_7点").Status==DatumHoleInspectionStatus.Detected,
                    "现场7点仍可识别");
            }
        }
        catch(Exception ex){Console.WriteLine(ex);failed++;}
        finally
        {
            foreach(string file in Directory.EnumerateFiles(folder))File.Delete(file);
            Directory.Delete(folder);
        }
        Console.WriteLine($"HoleQuality: {passed} passed, {failed} failed; no hardware accessed.");
        return failed==0?0:1;
    }
}
