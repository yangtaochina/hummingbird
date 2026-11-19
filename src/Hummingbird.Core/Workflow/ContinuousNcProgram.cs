namespace Hummingbird.Core;

public sealed record NcTravelEnvelope(double MinX,double MaxX,double MinY,double MaxY,double Z,double C)
{
    public void Validate()
    {
        if(new[]{MinX,MaxX,MinY,MaxY,Z,C}.Any(v=>!double.IsFinite(v)) || MinX>=MaxX || MinY>=MaxY)
            throw new InvalidDataException("必须提供明确有效的XY运动边界和固定Z/C。");
    }
    public bool Contains(double x,double y)=>double.IsFinite(x)&&double.IsFinite(y)&&x>=MinX&&x<=MaxX&&y>=MinY&&y<=MaxY;
}

/// <summary>整件共享会话：R220状态、221请求序号、222完成序号、223/224目标、225会话、226停止、227/228目标快照、229序号快照。</summary>
public static class ContinuousNcProgram
{
    public const int IdleWaitTicks = LaserRequestLimits.MaximumExposureMs/100+600;
    public static string Generate(NcTravelEnvelope bounds,int token)
    {
        bounds.Validate();if(token<=0)throw new InvalidDataException("NC会话编号无效。");
        // SINUMERIK 12180：有符号常数须括起，避免负Z产生 $AA_IM[Z]--201...。
        return FormattableString.Invariant($"""
            ; HUMMINGBIRD CONTINUOUS XY DISPATCHER - PANEL START ONCE
            ; RESERVE R220-R229; REVIEW XY ENVELOPE AND FIXED Z/C
            ; NO LASER OR Z/C MOTION; NO INTERMEDIATE M0 ON NORMAL PATH
            DEF INT HM_WAIT
            G90 G71 G94 G60
            STOPRE
            IF ABS($AA_IM[Z]-({bounds.Z:F6}))>0.010 GOTOF HM_ERROR
            R221=0 R222=0 R226=0
            R225={token}
            R220=1
            HM_WAIT=0
            HM_LOOP:
            STOPRE
            G4 F0.1
            HM_WAIT=HM_WAIT+1
            IF HM_WAIT>{IdleWaitTicks} GOTOF HM_ERROR
            IF R226==9 GOTOF HM_END
            IF R221==R222 GOTOB HM_LOOP
            IF R221<>R222+1 GOTOF HM_ERROR
            R227=R223 R228=R224 R229=R221
            IF R227<({bounds.MinX:F6}) GOTOF HM_ERROR
            IF R227>({bounds.MaxX:F6}) GOTOF HM_ERROR
            IF R228<({bounds.MinY:F6}) GOTOF HM_ERROR
            IF R228>({bounds.MaxY:F6}) GOTOF HM_ERROR
            IF ABS($AA_IM[Z]-({bounds.Z:F6}))>0.010 GOTOF HM_ERROR
            R220=2
            SUPA G1 X=R227 Y=R228 F30
            STOPRE
            R222=R229
            R220=3
            HM_WAIT=0
            GOTOB HM_LOOP
            HM_ERROR:
            R220=9
            M0
            HM_END:
            R220=0
            M30
            """).ReplaceLineEndings("\r\n")+"\r\n";
    }
}
