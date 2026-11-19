using System.Security.Cryptography;
using System.Text.Json;

namespace Hummingbird.Core;

public sealed record ProductionReworkInfo(Guid PlanId, Guid ParentRunId, Guid PhysicalWorkpieceId, int Round,
    string ParentRecordPath, string ParentRecordSha256, string ParentManifestPath, string ParentManifestSha256,
    int PreviousExposureMs, long PreviousCumulativeMs, int AdditionalExposureMs, ushort PowerRaw,
    string Reason, Guid? RetryOfRunId = null)
{
    public long CumulativeRequestedMs => checked(PreviousCumulativeMs + AdditionalExposureMs);
}

public sealed class ProductionReworkPlan
{
    public int Version { get; set; } = 1;
    public ProductionReworkInfo? Info { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public ReworkZeroExposureEvidence? RetryEvidence { get; set; }
    public static ProductionReworkPlan Create(ProductionReworkSource source, int ms, ushort power)
    {
        if (ms < LaserRequestLimits.MinimumExposureMs || ms > LaserRequestLimits.MaximumExposureMs || power is 0 or >10000)
            throw new InvalidDataException("本轮追加时长或功率无效。");
        var old = source.Record.Rework;
        return new() { Info = new(Guid.NewGuid(), source.Record.RunId, old?.PhysicalWorkpieceId ?? source.Record.RunId,
            checked((old?.Round ?? 0) + 1), source.RecordPath, source.RecordSha256, source.ManifestPath, source.ManifestSha256,
            source.Record.RequestedExposureMs!.Value, old?.CumulativeRequestedMs ?? source.Record.RequestedExposureMs.Value,
            ms, power, "操作者明确选择同件全部18孔补加工；取下回装后重新定位，本轮参数为额外加工量") };
    }
    public ProductionReworkSource Verify(int ms, ushort power)
    {
        var i = Info ?? throw new InvalidDataException("补加工计划缺少来源。");
        if (Version != 1 || i.PlanId == Guid.Empty || i.ParentRunId == Guid.Empty || i.PhysicalWorkpieceId == Guid.Empty || i.Round < 1 ||
            ms < LaserRequestLimits.MinimumExposureMs || ms > LaserRequestLimits.MaximumExposureMs || power is 0 or >10000 ||
            i.AdditionalExposureMs != ms || i.PowerRaw != power)
            throw new InvalidDataException("当前参数与已建立的补加工计划不同；请先设置本轮参数，再重新准备补加工记录。");
        if (ProductionReworkSource.Hash(i.ParentRecordPath) != i.ParentRecordSha256 ||
            ProductionReworkSource.Hash(i.ParentManifestPath) != i.ParentManifestSha256)
            throw new InvalidDataException("原加工来源已改变，未启用补加工。");
        var source = ProductionReworkSource.Load(i.ParentRecordPath);
        if (source.Record.RunId != i.ParentRunId || (source.Record.Rework?.PhysicalWorkpieceId ?? source.Record.RunId) != i.PhysicalWorkpieceId ||
            (source.Record.Rework?.Round ?? 0) + 1 != i.Round || source.Record.RequestedExposureMs != i.PreviousExposureMs ||
            (source.Record.Rework?.CumulativeRequestedMs ?? source.Record.RequestedExposureMs!.Value) != i.PreviousCumulativeMs)
            throw new InvalidDataException("补加工来源链或前轮参数不一致。");
        if(i.RetryOfRunId is { } retry)
        {
            if(RetryEvidence is null || RetryEvidence.FailedRunId!=retry)throw new InvalidDataException("缺少未出光重试的独立证据。");
            var failed=RetryEvidence.Verify();
            if(failed.Rework?.ParentRunId!=i.ParentRunId || failed.Rework.PhysicalWorkpieceId!=i.PhysicalWorkpieceId || failed.Rework.Round!=i.Round)
                throw new InvalidDataException("重试关联的失败轮次与原工件不符。");
            source.VerifyRetryReservation(RetryEvidence);
        }
        else if(RetryEvidence is not null)throw new InvalidDataException("重试证据与计划不符。");
        return source;
    }
    public static ProductionReworkPlan CreateRetry(ProductionReworkSource source,ReworkZeroExposureEvidence evidence,int ms,ushort power)
    {
        var failed=evidence.Verify();
        if(failed.Rework?.ParentRunId!=source.Record.RunId)throw new InvalidDataException("未出光记录不是该来源的补加工。");
        var plan=Create(source,ms,power);
        plan.Info=plan.Info! with {RetryOfRunId=failed.RunId,Reason="采图/定位阶段失败且没有出光的补加工，核对原记录后新建重试；失败轮不计入累计出光请求"};
        plan.RetryEvidence=evidence;_=plan.Verify(ms,power);return plan;
    }
}

/// <summary>只接收有完整归档和18孔关光证据的已完成轮次。原始run/图片/manifest均只读。</summary>
public sealed class ProductionReworkSource
{
    public required ProductionRunRecord Record { get; init; }
    public required string RecordPath { get; init; }
    public required string RecordSha256 { get; init; }
    public required string ManifestPath { get; init; }
    public required string ManifestSha256 { get; init; }
    public required RingScanSession Session { get; init; }
    public required IReadOnlyList<ProductionPoint> PreviousTargets { get; init; }
    public required ushort PowerRaw { get; init; }
    public required int ValidatedFiles { get; init; }
    public string ReservationPath => Path.Combine(Path.GetDirectoryName(ManifestPath)!, "本轮补加工登记.json");
    public static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    public static ProductionReworkSource Load(string selectedRecord)
    {
        var selected = JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(selectedRecord)) ?? throw new InvalidDataException("原加工记录为空。");
        Completed(selected);
        string directory = Path.GetDirectoryName(Path.GetFullPath(selectedRecord))!;
        string? bundle = new[] { Path.Combine(directory, "本件资料"), Path.GetDirectoryName(directory)!,
            Path.Combine(directory, "整件_" + selected.RunId.ToString("N"), "本件资料") }
            .FirstOrDefault(p => File.Exists(Path.Combine(p, "manifest.json")));
        if (bundle is null) throw new InvalidDataException("缺少原轮次的完整本件资料目录，请复制本件资料文件夹及manifest.json。");
        bundle = Path.GetFullPath(bundle); string manifest = Path.Combine(bundle, "manifest.json");
        var verified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(File.ReadAllText(manifest)))
        {
            foreach (var f in doc.RootElement.GetProperty("Files").EnumerateArray())
            {
                string name = f.GetProperty("Path").GetString()!;
                string full = Path.GetFullPath(Path.Combine(bundle, name));
                if (!full.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !verified.TryAdd(name.Replace('\\', '/'), full))
                    throw new InvalidDataException("原资料清单有越界或重复路径。");
                if (!File.Exists(full) || new FileInfo(full).Length != f.GetProperty("Bytes").GetInt64() ||
                    !Hash(full).Equals(f.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("原资料缺失或指纹不符：" + name);
            }
        }
        string FileIn(string name) => verified.GetValueOrDefault(name) ?? throw new InvalidDataException("原资料清单缺少：" + name);
        string canonical = FileIn("整件/run.json");
        var record = JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(canonical))!; Completed(record);
        if (record.RunId != selected.RunId || record.RequestedExposureMs != selected.RequestedExposureMs)
            throw new InvalidDataException("所选记录与本件归档轮次不符。");
        ushort? power = null;
        foreach (int id in Enumerable.Range(1, 18))
        {
            using var trial = JsonDocument.Parse(File.ReadAllText(FileIn($"孔位/hole_{id}/trial.json")));
            var t = trial.RootElement;
            if (t.GetProperty("DeviceMode").GetString() != "Real" || t.GetProperty("DryRun").GetBoolean() ||
                !t.GetProperty("Completed").GetBoolean() || !t.GetProperty("EmissionRequested").GetBoolean() ||
                !t.GetProperty("ShutdownConfirmed").GetBoolean() || t.GetProperty("Error").ValueKind != JsonValueKind.Null ||
                t.GetProperty("ProductionRunId").GetGuid() != record.RunId || t.GetProperty("HoleId").GetInt32() != id ||
                t.GetProperty("RequestedExposureMs").GetInt32() != record.RequestedExposureMs)
                throw new InvalidDataException($"原孔{id}未完整完成或关光，不能作为全18孔补加工来源。");
            ushort p = t.GetProperty("RequestedPreset").GetProperty("Settings").GetProperty("Power").GetUInt16();
            if (p is 0 or >10000 || power is not null && power != p) throw new InvalidDataException("原18孔功率记录不一致。");
            power = p;
            foreach (var (key, file) in new[] { ("BeforeImage", "before.bmp"), ("AfterImage", "after.bmp") })
                if (!Hash(FileIn($"孔位/hole_{id}/{file}")).Equals(t.GetProperty(key).GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"原孔{id}前后图与试打记录不匹配。");
        }
        var session = RingScanSession.Load(FileIn("定位/整组记录.json")); _ = GuidedCapture.Route(session);
        using var points = JsonDocument.Parse(File.ReadAllText(FileIn("整件/定位与补偿.json")));
        var targets = points.RootElement.GetProperty("Targets").EnumerateArray().Select(p => new ProductionPoint(
            p.GetProperty("Id").GetInt32(), p.GetProperty("LaserCommandX").GetDouble(), p.GetProperty("LaserCommandY").GetDouble())).ToArray();
        if (targets.Length != 18 || !targets.Select(p => p.Id).Order().SequenceEqual(Enumerable.Range(1, 18)) || targets.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            throw new InvalidDataException("原轮次缺少18孔定位来源。");
        return new() { Record = record, RecordPath = canonical, RecordSha256 = Hash(canonical), ManifestPath = manifest,
            ManifestSha256 = Hash(manifest), Session = session, PreviousTargets = targets, PowerRaw = power!.Value, ValidatedFiles = verified.Count };
    }
    private static void Completed(ProductionRunRecord record)
    {
        if (record.DryRun || record.Synthetic || !record.Completed || record.Phase != "Completed" || record.Error is not null ||
            record.CapturedSteps != 6 || record.LaserOffConfirmed != true || !record.NcExitConfirmed ||
            record.RequestedExposureMs is null or <LaserRequestLimits.MinimumExposureMs or >LaserRequestLimits.MaximumExposureMs ||
            record.Holes.Count != 18 || !Enumerable.Range(1, 18).All(id => record.Holes.GetValueOrDefault(id) == ProductionHolePhase.Completed))
            throw new InvalidDataException("仅完整完成并确认关光/NC退出的真实18孔轮次，才能建立全18孔补加工；中断或待核对孔不自动重打。");
    }
    public void EnsureNoRework()
    {
        if (File.Exists(ReservationPath)) throw new InvalidOperationException("该轮次已登记过补加工。请核对对应补加工结果；若还需追加，选择最新完成的补加工轮次，不能从同一首轮重复启动。");
    }
    public void EnsureCanStart(ProductionReworkInfo info)
    {
        if(info.RetryOfRunId is not { } retry){EnsureNoRework();return;}
        if(!File.Exists(ReservationPath))throw new InvalidOperationException("缺少原补加工登记，不能自动释放或重试。");
        using var doc=JsonDocument.Parse(File.ReadAllText(ReservationPath));
        if(doc.RootElement.GetProperty("ParentRunId").GetGuid()!=Record.RunId || doc.RootElement.GetProperty("ChildRunId").GetGuid()!=retry)
            throw new InvalidOperationException("该失败轮次已被另一轮替代或登记来源不同，未允许再次重试。");
    }
    private void VerifyRetryRegistration(ProductionRunRecord failed)
    {
        EnsureCanStart(failed.Rework! with {RetryOfRunId=failed.RunId});
        using var doc=JsonDocument.Parse(File.ReadAllText(ReservationPath));
        if(doc.RootElement.GetProperty("PlanId").GetGuid()!=failed.Rework!.PlanId ||
            failed.Rework.ParentRecordSha256!=RecordSha256 || failed.Rework.ParentManifestSha256!=ManifestSha256)
            throw new InvalidDataException("失败轮次与原补加工登记或来源指纹不一致。");
    }
    public void VerifyRetryReservation(ReworkZeroExposureEvidence evidence)
    {
        var failed=evidence.Verify();VerifyRetryRegistration(failed);
        using var doc=JsonDocument.Parse(File.ReadAllText(ReservationPath));
        string job=Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(evidence.RunPath))!)!.FullName;
        if(!string.Equals(Path.GetFullPath(doc.RootElement.GetProperty("ChildJobFolder").GetString()!),job,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("失败轮次所在目录与原补加工登记不一致。");
    }
    public void Reserve(ProductionReworkPlan plan, Guid childRunId, string jobFolder)
    {
        var info=plan.Info??throw new InvalidOperationException("缺少补加工计划。");
        if(info.ParentRunId!=Record.RunId || childRunId==Guid.Empty)throw new InvalidOperationException("补加工登记轮次无效。");
        using var gate=new FileStream(ReservationPath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        EnsureCanStart(info);
        if(info.RetryOfRunId is not null)_=plan.Verify(info.AdditionalExposureMs,info.PowerRaw);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ParentRunId = Record.RunId,
            info.PlanId, ChildRunId = childRunId, ChildJobFolder = Path.GetFullPath(jobFolder), CreatedAt = DateTimeOffset.Now,
            PreviousChildRunId=info.RetryOfRunId,ZeroExposureEvidence=plan.RetryEvidence }));
        if(info.RetryOfRunId is { } previous)
        {
            byte[] old=File.ReadAllBytes(ReservationPath);
            string history=Path.Combine(Path.GetDirectoryName(ReservationPath)!,"补加工登记历史_"+previous.ToString("N")+".json");
            if(File.Exists(history))
            {if(!File.ReadAllBytes(history).SequenceEqual(old))throw new IOException("补加工登记历史已有不同内容，未覆盖。");}
            else using(var backup=new FileStream(history,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){backup.Write(old);backup.Flush(true);}
            string temporary=ReservationPath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes);output.Flush(true);}
                File.Move(temporary,ReservationPath,true);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        else using(var stream=new FileStream(ReservationPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){stream.Write(bytes);stream.Flush(true);}
    }
    public void VerifyPattern(IReadOnlyList<ProductionPoint> current)
    {
        if (current.Count != 18 || !current.Select(p => p.Id).Order().SequenceEqual(Enumerable.Range(1, 18)) || current.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)))
            throw new InvalidDataException("补加工必须完整重新定位相同18孔。");
        foreach (var a in PreviousTargets)
        foreach (var b in PreviousTargets.Where(p => p.Id > a.Id))
        {
            var x = current.Single(p => p.Id == a.Id); var y = current.Single(p => p.Id == b.Id);
            if (Math.Abs(double.Hypot(a.X - b.X, a.Y - b.Y) - double.Hypot(x.X - y.X, x.Y - y.Y)) > .005)
                throw new InvalidDataException("本轮18孔与原轮次孔位几何不一致，未开始补加工。");
        }
    }
}
