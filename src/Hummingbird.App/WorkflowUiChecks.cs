using System.Text.Json;
using Hummingbird.Core;
using OpenCvSharp;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    internal void WriteWorkflowLayoutArtifacts(string folder)
    {
        if(_captureDetails.SelectedTab?.Text!="一键加工")throw new InvalidOperationException("空记录应默认显示一键加工，不应自动切到空照片。");
        CheckBundledStationPrograms(folder);
        foreach(var size in new[]{new System.Drawing.Size(1360,900),new System.Drawing.Size(1100,740)})
        {
            ClientSize=size;
            for(int i=0;i<_tabs.TabPages.Count;i++)
            {
                _tabs.SelectedIndex=i;PerformLayout();_tabs.SelectedTab!.PerformLayout();
                using var bitmap=new Bitmap(Width,Height);DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));
                bitmap.Save(Path.Combine(folder,$"layout_{size.Width}_{i+1}.png"));
            }
        }
        _tabs.SelectedTab=_environmentPage;PerformLayout();
        IEnumerable<Control> Descendants(Control root)=>root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Descendants(c)));
        var sizes=Descendants(_environmentPage).OfType<TextBox>().Where(c=>c.Name is "EnvironmentReport" or "ConfigurationGuide")
            .Select(c=>new{c.Name,c.Width,c.Height}).ToArray();
        File.WriteAllText(Path.Combine(folder,"text-sizes.json"),JsonSerializer.Serialize(sizes));
        if(sizes.Any(c=>c.Height<100))throw new InvalidOperationException("多行说明高度不正确。");
    }
    private async Task CheckWorkflowAdditions(string folder, Action<bool,string> check, string? source, string? calibration)
    {
        check(Text.StartsWith("飞秒创芯") && !Text.Contains("蜂鸟"),"标题使用飞秒创芯");
        _tabs.SelectedTab=_environmentPage;PerformLayout();
        IEnumerable<Control> Descendants(Control root)=>root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Descendants(c)));
        check(Descendants(_environmentPage).OfType<TextBox>().Single(c=>c.Name=="EnvironmentReport").Height>=100 &&
            Descendants(_environmentPage).OfType<TextBox>().Single(c=>c.Name=="ConfigurationGuide").Height>=120,
            "环境检查及配置说明的多行文本保持完整高度，不因自适应布局被压成单行");
        _tabs.SelectedTab=_machiningPage;_captureDetails.SelectedIndex=0;PerformLayout();
        check(_livePicture.Visible && _livePicture.Width>=350,"定位加工页保留大实时画面");
        check(_reworkActions.Controls.Cast<Control>().Any(c=>c.Name=="PrepareProductionRework"),"补加工准备入口在独立功能页");
        string logRoot=Path.Combine(folder,"categorized-logs");var logger=new MemoryLogger(logRoot);
        logger.Info("[整件] 测试进度");logger.Info("[定位采集] 测试视觉");logger.Info("激光只读测试");logger.Warn("数控连接测试异常");
        string day=Path.Combine(logRoot,DateTime.Now.ToString("yyyy-MM-dd"));
        check(new[]{"加工","视觉","激光","设备","异常"}.All(c=>File.Exists(Path.Combine(day,c+".log"))),"运行日志按日期、加工、视觉、激光、设备及异常分类落盘");
        var record=new ProductionRunRecord {DryRun=true,Synthetic=true,Phase="Starting"};
        string runFolder=Path.Combine(folder,"event-history");ProductionJobArchive.SaveRunRecord(runFolder,record);
        record.Phase="Failed";record.Error="模拟错误";ProductionJobArchive.SaveRunRecord(runFolder,record);
        using(var events=JsonDocument.Parse(File.ReadAllText(Path.Combine(runFolder,"加工事件.json"))))
            check(events.RootElement.GetArrayLength()==2 && events.RootElement[0].GetProperty("Phase").GetString()=="Starting" &&
                events.RootElement[1].GetProperty("Phase").GetString()=="Failed","每轮事件按顺序保留阶段变更及运行ID，失败不覆盖已发生事件");
        if(source is not null)
        {
            string synthetic=Path.Combine(folder,"optical-synthetic-fixture");Directory.CreateDirectory(synthetic);
            using var image=new Mat(1200,1920,MatType.CV_8UC1);
            var random=new Random(140);var pixels=new byte[1200*1920];random.NextBytes(pixels);
            System.Runtime.InteropServices.Marshal.Copy(pixels,0,image.Data,pixels.Length);
            Cv2.GaussianBlur(image,image,new OpenCvSharp.Size(3,3),0);
            var names=new List<string>();
            var movements=new[]{(0.0,0.0,0.0,0.0),(0.0,0.0,0.0,0.0),(.1,0.0,60.0,5.0),(0.0,.1,-5.0,58.0)};
            for(int i=0;i<4;i++)
            {
                var (x,y,px,py)=movements[i];using var shifted=new Mat();
                using var transform=Mat.FromArray(new double[,] {{1,0,px},{0,1,py}});
                Cv2.WarpAffine(image,shifted,transform,image.Size(),InterpolationFlags.Linear,BorderTypes.Reflect101);
                Cv2.ImEncode(".bmp",shifted,out byte[] bytes);
                string path=Path.Combine(synthetic,$"synthetic_{i}.bmp");File.WriteAllBytes(path,bytes);
                var frame=RingCaptureArchive.BindImage(new RingScanFrame("SyntheticOpticsFixture",path,x,y,-1,0),new RingScanSettings(),DateTime.Now,1920,1200,
                    AxisCoordinateSource.OpcUaReadback,AxisCoordinateSource.ManualPanelEntry,null,"Synthetic data; no hardware access");
                frame=frame with {Capture=frame.Capture! with {DeviceMode="Real",RequestedExposureUs=500,RequestedGain=0}};
                names.Add(RingCaptureArchive.SaveSidecar(frame,new RingScanSettings()));
            }
            var result=await Task.Run(()=>CalculateOpticalXy(names.ToArray(),Path.Combine(synthetic,"calculated")));
            var originalFrame=RingScanSession.Load(names[0]).Frames.Single();var repeatFrame=RingScanSession.Load(names[1]).Frames.Single();
            var texture=await Task.Run(()=>CheckOpticalStationaryTexture(originalFrame,repeatFrame));
            check(texture.Patches.Count>=3 && double.Hypot(texture.Dx,texture.Dy)<.25,"校准采样前原位重复纹理自动通过，无需人工勾选清晰度");
            bool movingRejected=false;
            try{CheckOpticalStationaryTexture(originalFrame,RingScanSession.Load(names[2]).Frames.Single());}catch(InvalidDataException){movingRejected=true;}
            check(movingRejected,"原位照片实际发生位移时在采样前拒绝，不跳过重复性检查");
            string flatPath=Path.Combine(synthetic,"flat.bmp");Hummingbird.Vision.RingScanSolver.SaveFrame(flatPath,new(new byte[1920*1200],1920,1200,DateTime.Now));
            bool flatRejected=false;
            var flat=originalFrame with{ImagePath=flatPath,Capture=originalFrame.Capture! with{ImageSha256=LaserSpotCalibration.Hash(flatPath)}};
            try{CheckOpticalStationaryTexture(flat,flat);}catch(InvalidDataException){flatRejected=true;}
            check(flatRejected,"平坦无纹理图像在XY采样前拒绝，不以人工许可替代图像检查");
            check(result.Path is not null,"已知独立位移的合成纹理通过四图计算及一致性检查");
            var settings=RingScanSession.Load(result.Path!).Settings;
            check(Math.Abs(settings.Jxx-600)<.5 && Math.Abs(settings.Jxy+50)<.5 && Math.Abs(settings.Jyx-50)<.5 && Math.Abs(settings.Jyy-580)<.5,
                "四图XY结果还原已知二维响应，方向与交叉项正确");
            bool duplicateRejected=false;
            try{CalculateOpticalXy([names[0],names[1],names[2],names[2]],Path.Combine(synthetic,"invalid"));}catch(InvalidDataException){duplicateRejected=true;}
            check(duplicateRejected,"重复来源不能冒充两个独立位移");
            var changed=RingScanSession.Load(names[3]);changed.Frames[0]=changed.Frames[0] with {Capture=changed.Frames[0].Capture! with {RequestedGain=1}};
            string mismatch=Path.Combine(synthetic,"gain-mismatch.json");changed.Save(mismatch);
            bool mismatchRejected=false;
            try{CalculateOpticalXy([names[0],names[1],names[2],mismatch],Path.Combine(synthetic,"invalid-gain"));}catch(InvalidDataException){mismatchRejected=true;}
            check(mismatchRejected,"四图曝光/增益不一致时拒绝计算");
        }
        if(calibration is not null)
        {
            var loaded=LaserSpotCalibration.Load(calibration);
            string newSpot=Path.Combine(folder,"recalculated-existing-spot");
            await Task.Run(()=>LaserSpotReview.Run(loaded.SourceTrialPath,newSpot));
            using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(newSpot,"落点分析.json")));
            var recalculated=LaserSpotCalibration.Load(report.RootElement.GetProperty("CalibrationPath").GetString()!);
            check(recalculated.SourceTrialSha256==loaded.SourceTrialSha256 && double.Hypot(recalculated.PixelX-loaded.PixelX,recalculated.PixelY-loaded.PixelY)<.01,
                "界面采用的落点分析从已有真实前后图重新计算，保持原算法及完整来源核验");
            _markOpticsChanged!();bool blocked=false;
            try{_readProductionCalibration!();}catch(InvalidOperationException ex){blocked=ex.Message.Contains("光学条件已调整");}
            check(blocked && _stateStore.Load().OpticalRecalculationRequired && !_fixedCConfirmed.Checked,
                "标记光学调整立即停用旧落点并持久保存，不自动恢复装夹确认");
            bool oldRejected=false;
            try{await _importOpticalCalibration!(calibration);}catch(InvalidDataException){oldRejected=true;}
            check(oldRejected && _stateStore.Load().OpticalRecalculationRequired,"光学调整后不能通过重新导入旧试打来源恢复加工");
        }
    }
}
