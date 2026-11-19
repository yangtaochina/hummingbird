using System.Globalization;

namespace Hummingbird.Core;

public sealed record StationReadinessReport(MachineStatus Position, double? ActualC, IReadOnlyList<string> Problems)
{
    public bool CanContinue => Problems.Count==0;
    public bool NeedsPanelC => ActualC is null;
}

/// <summary>启动前仅读取实际位置及已有NC握手；缺少C反馈明确交给本次人工确认，不以配置值冒充实测。</summary>
public static class StationReadiness
{
    public static async Task<StationReadinessReport> InspectAsync(ICncService cnc, ISinumerikVarAccess vars,
        NcTravelEnvelope bounds, int token, string program, Func<CancellationToken,Task<double?>> readC, CancellationToken ct=default)
    {
        bounds.Validate();
        if(token<=0)throw new InvalidDataException("启动配置会话无效。");
        var problems=new List<string>();
        var position=await cnc.GetStatusAsync(ct);
        if(!position.Connected || position.Message!="OK" || new[]{position.PosX,position.PosY,position.PosZ}.Any(v=>!double.IsFinite(v)))
            problems.Add("数控位置读取失败：请在“环境与参数”检查连接及机床状态。");
        else
        {
            if(!bounds.Contains(position.PosX,position.PosY))problems.Add("当前位置超出工作范围：请在“工位恢复”恢复工作位置后重试。");
            if(Math.Abs(position.PosZ-bounds.Z)>.010)problems.Add($"工作高度未到位：当前Z={position.PosZ:F5}，要求{bounds.Z:F5}mm。请恢复工位。");
        }
        try
        {
            var values=new Dictionary<int,double>();
            foreach(int r in new[]{220,221,222,225,226})
            {
                object? raw=await vars.ReadVarAsync($"/Channel/Parameter/R[u1, {r}]",ct);
                if(raw is null)throw new IOException("等待状态为空。");
                values[r]=Convert.ToDouble(raw,CultureInfo.InvariantCulture);
                if(!double.IsFinite(values[r]))throw new IOException("等待状态无效。");
            }
            if(values[225]!=token || values[220] is not (1 or 3) || values[221]!=0 || values[222]!=0 || values[226]!=0)
                problems.Add($"{program}未处于本轮起始等待：请先处理面板报警，选择对应程序，从头Cycle Start一次进入等待，再点击开始。已有本轮起始等待时不必重复启动。");
        }
        catch(OperationCanceledException){throw;}
        catch(Exception){problems.Add($"无法读取{program}等待状态：请检查数控连接和配套程序。");}
        double? actualC=null;
        try
        {
            actualC=await readC(ct);
            if(actualC is {} c && (!double.IsFinite(c) || Math.Abs(Math.IEEERemainder(c-bounds.C,360))>.005))
                problems.Add($"C轴未到位：要求{bounds.C:F4}°，请恢复工位后重试。");
        }
        catch(OperationCanceledException){throw;}
        catch(Exception){problems.Add("已配置的C轴反馈读取失败：请检查反馈配置及数控连接，不能用人工值覆盖故障反馈。");}
        ct.ThrowIfCancellationRequested();
        return new(position,actualC,problems);
    }

    public static double ValidatePanelC(string text, NcTravelEnvelope bounds)
    {
        if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out double value) || !double.IsFinite(value) ||
            Math.Abs(Math.IEEERemainder(value-bounds.C,360))>.005)
            throw new InvalidOperationException($"请读取面板实际C角度并填写；本工位要求{bounds.C:F4}°。不一致时先恢复工位。");
        return value;
    }
}
