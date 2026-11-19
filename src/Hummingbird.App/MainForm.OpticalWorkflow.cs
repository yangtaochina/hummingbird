using System.Text.Json;
using Hummingbird.Core;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private bool _opticalUiAction;
    private TabPage BuildOpticalWorkflowPage(TabPage compensation, TabPage settings, Func<Func<Task>,Task> run)
    {
        var page=new TabPage("光学与落点");
        var tabs=new TabControl {Dock=DockStyle.Fill,Multiline=true};
        var calculate=new TabPage("调整后重新计算");var flow=WorkflowFlow();
        var output=new TextBox {Name="OpticalCalculationStatus",Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Width=980,Height=145,
            Text="普通回位、工件取放：沿用适用标定，重新定位即可。镜头、倍率、相机高度或工作距离改变：重新取得XY响应及落点来源。"};
        async Task Act(Func<Task> action)
        {
            _opticalUiAction=true;
            try{await run(async()=>{try{await action();}catch(Exception ex){output.Text="计算未完成："+ex.Message;throw;}});}
            finally{_opticalUiAction=false;}
        }
        var sources=new TextBox[4];
        var labels=new[]{"原位照片","原位重复照片","位移B照片","位移C照片"};
        for(int i=0;i<sources.Length;i++)sources[i]=new TextBox {Width=700,ReadOnly=true};
        string? resultFolder=null,xySession=null,laserResult=null;
        flow.Controls.Add(GroupBoxOf("1 确认光学调整",Lbl("仅轴移动后回到原工位不需要本操作。确实改变光学条件时，先停机，再标记旧落点停用。"),
            Btn("已调整光学：停用旧落点",(_,_)=>
            {
                if(_workspaceBusy || _laserExposureTask is {IsCompleted:false})return;
                _markOpticsChanged?.Invoke();output.Text="旧落点已停用，并保存调整标记。请取得当前光学条件的照片和新试打记录后计算。";
            })));
        flow.Controls.Add(Lbl("2 现场取得同一特征原位、原位重复及两个独立方向位移的照片。保持Z/C、曝光、增益和光学条件一致，位移由现场面板执行。选择各照片的.capture.json；本页只读取资料。"));
        for(int i=0;i<4;i++)
        {
            int index=i;
            flow.Controls.Add(GroupBoxOf(labels[i],sources[i],Btn("选择照片记录",(_,_)=>
            {
                using var dialog=new OpenFileDialog {Filter="照片配套记录|*.capture.json|采集记录|*.json"};
                if(dialog.ShowDialog(this)==DialogResult.OK){sources[index].Text=dialog.FileName;xySession=null;}
            })));
        }
        string Folder()
        {
            if(resultFolder is not null)return resultFolder;
            string root=EnsureCaptureRoot()??throw new OperationCanceledException("未选择资料目录。");
            resultFolder=Path.Combine(root,"光学标定_"+DateTime.Now.ToString("yyyyMMdd_HHmmssfff"));
            Directory.CreateDirectory(resultFolder);return resultFolder;
        }
        flow.Controls.Add(GroupBoxOf("计算XY响应",Btn("从四张照片计算XY",async(_,_)=>await Act(async()=>
        {
            xySession=null;string folder=Path.Combine(Folder(),"XY_"+DateTime.Now.ToString("HHmmssfff"));string[] selected=sources.Select(t=>t.Text).ToArray();
            var result=await Task.Run(()=>CalculateOpticalXy(selected,folder));
            xySession=result.Path;output.Text=result.Description;
            _log.Info("[光学标定] "+result.Description);
        })),Btn("采用本次XY结果并建立空记录",async(_,_)=>await Act(()=>
        {
            if(xySession is null)throw new InvalidOperationException("请先完成XY计算；图块分歧超限的候选不能采用。");
            _markOpticsChanged?.Invoke();
            _adoptOpticalSettings!(RingScanSession.Load(xySession).Settings);
            output.Text="已采用本次XY响应并建立空定位记录。旧六步基准与落点不适用于新光学条件。\r\n接下来重新建立六步采图基准，并在现场选定位置保存新单点试打前后图。";
            return Task.CompletedTask;
        }))));
        flow.Controls.Add(GroupBoxOf("3 重新计算激光落点",Lbl("在维护与辅助的单点试打页保存当前光学条件的新trial.json及前后图。计算成功后查看落点标注，按现场流程复核位置，再导入结果并确认光学条件。"),
            Btn("前往单点采样",(_,_)=>{var tools=(TabControl)_toolsPage.Controls[0];SelectPage(tools.TabPages.Cast<TabPage>().Single(t=>t.Text=="单点试打"));}),
            Btn("选择trial并计算落点",async(_,_)=>
            {
                using var dialog=new OpenFileDialog {Filter="试打记录|trial.json"};if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                await Act(async()=>
                {
                    laserResult=null;
                    string trial=dialog.FileName,folder=Path.Combine(Folder(),"落点_"+DateTime.Now.ToString("HHmmssfff"));
                    await Task.Run(()=>LaserSpotReview.Run(trial,folder));
                    using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"落点分析.json")));
                    laserResult=report.RootElement.GetProperty("CalibrationPath").GetString()!;
                    output.Text="落点已计算，原始trial和前后图已复制并核对来源。\r\n"+laserResult+"\r\n打开结果查看落点标注；独立位置复核与实物质量仍由现场核定。";
                    _log.Info("[光学标定] 新落点计算完成："+laserResult);
                });
            }),Btn("导入本次落点结果",async(_,_)=>await Act(async()=>
            {
                if(laserResult is null)throw new InvalidOperationException("先计算落点结果。");
                await _importOpticalCalibration!(laserResult);tabs.SelectedTab=compensation;
            })),Btn("打开计算资料",(_,_)=>{if(resultFolder is not null)OpenLocalFolder(resultFolder);})));
        output.Dock=DockStyle.Bottom;output.Height=105;calculate.Controls.Add(flow);calculate.Controls.Add(output);
        compensation.Text="落点来源与补偿";settings.Text="XY参数详情";
        calculate.Text="维护：已有照片分析";
        tabs.TabPages.AddRange([BuildAutomaticOpticalTab(run),compensation,calculate,settings]);page.Controls.Add(tabs);return page;
    }

    internal static (string? Path,string Description) CalculateOpticalXy(string[] sidecars,string folder)
    {
        if(sidecars.Length!=4 || sidecars.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=4)
            throw new InvalidDataException("请选择四张独立照片记录。");
        var frames=sidecars.Select(path=>RingScanSession.Load(path).Frames.Single()).ToArray();
        var first=frames[0];
        foreach(var f in frames)
        {
            if(f.Capture is not {DeviceMode:"Real",XyzSource:AxisCoordinateSource.OpcUaReadback,RequestedExposureUs:not null,RequestedGain:not null} capture || capture.EditedAfterCapture)
                throw new InvalidDataException("XY计算需要真实相机照片、原始参数及数控读回的实际坐标。");
            RingCaptureArchive.VerifyImage(f);
            if(capture.ImageWidth!=1920 || capture.ImageHeight!=1200)
                throw new InvalidDataException("当前多图块复核支持1920×1200原图，其他尺寸须另行核定采样区域。");
            if(Math.Abs(f.Z-first.Z)>.002 || Math.Abs(Math.IEEERemainder(f.C-first.C,360))>.001 ||
                capture.RequestedExposureUs!=first.Capture!.RequestedExposureUs || capture.RequestedGain!=first.Capture.RequestedGain)
                throw new InvalidDataException("四张照片的Z/C、曝光或增益不一致。");
        }
        if(double.Hypot(frames[1].X-first.X,frames[1].Y-first.Y)>.002)
            throw new InvalidDataException("原位重复照片的实际XY不一致。");
        double bx=frames[2].X-frames[1].X,by=frames[2].Y-frames[1].Y,cx=frames[3].X-frames[1].X,cy=frames[3].Y-frames[1].Y;
        double det=bx*cy-by*cx;
        if(Math.Abs(det)<1e-5)throw new InvalidDataException("两个位移方向不独立或过小，不能计算二维响应。");
        XyEvidenceReview.Sample Sample(int i)=>new(frames[i].ImagePath,frames[i]);
        var still=XyEvidenceReview.Measure(Sample(0),Sample(1));
        var b=XyEvidenceReview.Measure(Sample(1),Sample(2));var c=XyEvidenceReview.Measure(Sample(1),Sample(3));
        var settings=JsonSerializer.Deserialize<RingScanSettings>(JsonSerializer.Serialize(first.Capture!.SettingsAtRegistration))!;
        settings.Jxx=(b.Dx*cy-c.Dx*by)/det;settings.Jxy=(-b.Dx*cx+c.Dx*bx)/det;
        settings.Jyx=(b.Dy*cy-c.Dy*by)/det;settings.Jyy=(-b.Dy*cx+c.Dy*bx)/det;
        settings.CalibrationNote="界面四图计算局部XY响应；来源见"+Path.Combine(folder,"XY计算依据.json");settings.Validate();
        bool stable=new[]{still,b,c}.All(m=>m.MaxPatchSpreadPx<=.25) && double.Hypot(still.Dx,still.Dy)<=.25;
        Directory.CreateDirectory(folder);
        // 保存可移植的来源副本，不重写原图或原sidecar。
        var copied=new List<RingScanFrame>();
        for(int i=0;i<frames.Length;i++)
        {
            string image=Path.Combine(folder,$"XY来源_{i}.bmp");File.Copy(frames[i].ImagePath,image,false);
            var copy=frames[i] with {ImagePath=image};RingCaptureArchive.SaveSidecar(copy,copy.Capture!.SettingsAtRegistration);copied.Add(copy);
        }
        string reportPath=Path.Combine(folder,"XY计算依据.json");
        File.WriteAllText(reportPath,JsonSerializer.Serialize(new {Kind="OpticalXyRecalculation",HardwareAccess=false,PreviewOnly=true,ConsistentWithinQuarterPixel=stable,
            OriginalRecords=sidecars,Frames=copied,Stationary=still,MoveB=b,MoveC=c,Settings=settings},new JsonSerializerOptions {WriteIndented=true}));
        string path=Path.Combine(folder,"当前XY响应_空记录.json");
        if(stable)new RingScanSession {Settings=settings}.Save(path);
        return (stable?path:null,FormattableString.Invariant($"J=[{settings.Jxx:F6}, {settings.Jxy:F6}; {settings.Jyx:F6}, {settings.Jyy:F6}] px/mm\r\n")
            +(stable?"多图块及原位重复通过0.25px一致性检查，可采用局部响应；不代表加工精度验收。":"已保存候选及分歧依据；未通过0.25px一致性检查，不能直接采用，请核对原图与光学稳定性。"));
    }
}
