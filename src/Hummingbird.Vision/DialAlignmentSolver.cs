using Hummingbird.Core;

namespace Hummingbird.Vision;

/// <summary>纯图像/坐标计算：固定C圆环求心→11/1参考孔定向→CAD刚性变换预览；无设备控制。</summary>
public sealed class DialAlignmentSolver
{
    public DialAlignmentPreview Analyze(RingScanSession session,HolePatternTemplate template,
        DatumDirectionOptions? options=null)
    {
        if (session.Frames.Any(f=>!Enum.IsDefined(f.Role) || f.Capture?.Purpose=="RotaryScan"))
            throw new InvalidDataException("当前自动定位只支持固定C的数据，请将旋转采图与固定C定位记录分开。");
        var eleven=session.Frames.Where(f=>f.Role==RingFrameRole.Reference11).ToArray();
        var one=session.Frames.Where(f=>f.Role==RingFrameRole.Reference1).ToArray();
        if(eleven.Length!=1 || one.Length!=1)
            throw new InvalidDataException("请在用途列指定且仅指定一张11点参考孔、一张1点参考孔；其余圆环照片标为圆环。");
        var arcs=new RingScanSession { Settings=session.Settings,
            Frames=session.Frames.Where(f=>f.Role==RingFrameRole.RingArc).ToList() };
        var ring=new RingScanSolver().Analyze(arcs);
        options ??= session.DirectionOptions;
        var direction=new DatumDirectionSolver().Analyze(session.Settings,ring,eleven[0],one[0],options);
        var targets=RingScanPreview.Targets(ring,template,direction.TwelveAngleDeg);
        var warnings=new List<string> {
            "定位预览，尚未验收实际加工精度；未应用激光落点补偿。",
            "11点/1点用途由操作者按工件方向指定；对称孔组无法独立确定唯一12点方位。",
            "参考孔制造偏差与孔缘反光会影响定向；CAD孔距保持原尺寸，不按实物孔距缩放。" };
        if(ring.Initialization is {EstimatedFromCapturePositions:true} seed)
            warnings.Add($"原粗中心不适用，已由本次圆环采图位置估计粗中心({seed.HintX:F4},{seed.HintY:F4})；"
                +$"覆盖最大间隔{seed.MaxAngularGapDeg:F1}°，该粗中心仅作图像取边初始化，不是最终拟合圆心。");
        if(options.BoundaryMode==DatumBoundaryMode.CircularFaceEdge)
            warnings.Add("11点/1点均使用实体圆面边缘模型；不使用外侧阴影中心，不与暗孔亮缘模型混算。");
        if (Math.Max(direction.Reference11.CenterSpreadMm,direction.Reference1.CenterSpreadMm)>options.CenterSpreadWarningMm)
            warnings.Add($"参考孔孔心阈值敏感性：11点{direction.Reference11.CenterSpreadMm*1000:F1}µm，1点{direction.Reference1.CenterSpreadMm*1000:F1}µm；超过图像预警值，不能据此验收微米级加工精度。");
        if(session.Frames.Any(f=>f.Capture==null || f.Capture.CSource!=AxisCoordinateSource.OpcUaReadback))
            warnings.Add("记录含历史来源未标注或C手填值，同次装夹及实际C需由现场核对。");
        if(session.Frames.Any(f=>f.Capture?.EditedAfterCapture==true)) warnings.Add("记录含采图后手工修改过的数据。");
        bool recovery=ring.CompatibilityRecovery is not null || new[]{eleven[0],one[0]}.Any(f=>f.FaceSeed?.Ellipse is not null || f.FaceSeed?.SelectionSource.Contains("Compatibility")==true);
        double maxShift=0;int evaluated=0,invalidVariants=0;
        var variants=ring.FitVariants.Count>0?ring.FitVariants:new[]{new RingFitVariant(ring.CenterX,ring.CenterY,ring.RadiusMm)};
        foreach(var variant in variants)
        foreach(var a in direction.Reference11.Samples)
        foreach(var b in direction.Reference1.Samples)
        {
            var x=settingsPoint(eleven[0],a);var y=settingsPoint(one[0],b);
            (double Twelve,double Eleven,double One,double Gap) perturbed;
            try{perturbed=DatumDirectionGeometry.Compute(variant.CenterX,variant.CenterY,x.X,x.Y,y.X,y.Y,options);}
            catch(InvalidDataException) when(!recovery){invalidVariants++;continue;}
            var varied=RingScanPreview.Targets(ring with{CenterX=variant.CenterX,CenterY=variant.CenterY},template,perturbed.Twelve);
            for(int i=0;i<targets.Count;i++)maxShift=Math.Max(maxShift,double.Hypot(targets[i].MachineX-varied[i].MachineX,targets[i].MachineY-varied[i].MachineY));
            evaluated++;
        }
        if(recovery && maxShift>options.MaxRecoveredTargetShiftMm)
            throw new InvalidDataException($"兼容识别的18孔坐标变化最大{maxShift*1000:F1}µm，超过软件稳定性上限{options.MaxRecoveredTargetShiftMm*1000:F1}µm，未输出加工孔位。");
        if(recovery)warnings.Add($"使用兼容识别；圆心和定向扰动合成后18孔最大变化{maxShift*1000:F1}µm，软件稳定性检查不等于加工精度验收。");
        return new DialAlignmentPreview(ring,direction,targets,options,warnings) {TargetStability=new(recovery,maxShift,options.MaxRecoveredTargetShiftMm,evaluated,invalidVariants)};
        (double X,double Y) settingsPoint(RingScanFrame frame,DatumHoleSample sample)=>session.Settings.ToMachine(frame,sample.Px,sample.Py);
    }
}
