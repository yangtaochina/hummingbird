using System.Globalization;
using System.Text.Json;

namespace Hummingbird.Core;

public enum ProcessStepState { Passed, Failed, NotRun }

/// <summary>一次加工中的一个步骤：结论、关键数值，以及可供核对的原始数据文件和照片。</summary>
public sealed record ProcessStep(string Name, ProcessStepState State, string Summary, string? DataFile = null, string? ImageFile = null);

/// <summary>
/// 把一件已保存的记录按加工顺序整理成步骤清单，供维护人员逐步核对。只读已保存的文件，不连接设备、不重新计算，
/// 所以显示的是当时保存下来的结果；缺少的文件对应步骤显示为未执行。
/// </summary>
public static class ProcessTrace
{
    /// <summary>未指定运行目录时，优先取本件真实加工轮次，否则取最近一次试走；都没有时只列出采图和定位步骤。</summary>
    public static string? DefaultRunFolder(string jobFolder)
    {
        string machined = Path.Combine(jobFolder, ProductionJobArchive.MachiningRecordName);
        if (File.Exists(machined))
            try
            {
                var record = JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(machined));
                string folder = Path.Combine(jobFolder, ProductionSummary.RunFolderPrefix + record!.RunId.ToString("N"));
                if (File.Exists(Path.Combine(folder, "run.json"))) return folder;
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        return Directory.Exists(jobFolder)
            ? Directory.EnumerateDirectories(jobFolder, ProductionSummary.RunFolderPrefix + "*").Where(f => File.Exists(Path.Combine(f, "run.json")))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(Path.Combine(f, "run.json"))).FirstOrDefault()
            : null;
    }

    public static IReadOnlyList<ProcessStep> Read(string jobFolder, string? runFolder = null)
    {
        jobFolder = Path.GetFullPath(jobFolder); runFolder ??= DefaultRunFolder(jobFolder);
        var steps = new List<ProcessStep>();
        ProductionRunRecord? record = null; string? runFile = runFolder is null ? null : Path.Combine(runFolder, "run.json");
        if (runFile is not null && File.Exists(runFile))
            record = ProductionJobArchive.ReviewAfterRestart(JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(runFile))
                ?? throw new InvalidDataException("run.json为空。"));
        string stopped = record is { Completed: false } ? record.StoppedDuring ?? record.Phase : "";
        string Failure() => record?.Error is { Length: > 0 } error ? "；" + error.ReplaceLineEndings(" ") : "";

        if (record is not null)
        {
            string startup = Path.Combine(runFolder!, "自动启动检查.json");
            steps.Add(File.Exists(startup)
                ? new("启动检查", ProcessStepState.Passed, $"{(record.DryRun ? "无出光试走" : record.Rework is null ? "新件加工" : $"同件补加工第{record.Rework.Round}轮")}；自动检查和现场确认已保存", startup)
                : new("启动检查", stopped.StartsWith("Starting") || stopped == "Created" ? ProcessStepState.Failed : ProcessStepState.NotRun,
                    "没有保存启动检查文件" + (stopped.StartsWith("Starting") ? Failure() : ""), runFile));
        }

        // 有运行记录时只用属于这一轮的定位数据：工件目录的检查文件内容最全，但事后重新检查会覆盖它，
        // 那时改用运行目录里收尾时保存的本轮定位结果；两者都没有就如实显示没有数据。
        string jobReport = Path.Combine(jobFolder, ProductionSummary.JobReportName);
        string reportFile = record is null ? jobReport
            : ProductionSummary.JobReportOfRun(jobFolder, record) ?? Path.Combine(runFolder!, ProductionSummary.LocatingSnapshotName);
        string missing = record is not null && !File.Exists(reportFile) && File.Exists(jobReport)
            ? "工件目录的检查文件是本轮结束后重新生成的，不代表本轮；本轮没有另存定位结果" : "";
        using var report = File.Exists(reportFile) ? Parse(reportFile) : null;
        var frames = report is not null && Find(report.RootElement, "SourceFrames") is { ValueKind: JsonValueKind.Array } source ? source.EnumerateArray().ToArray() : [];
        var inspections = report is not null && Find(report.RootElement, "ReferenceInspections") is { ValueKind: JsonValueKind.Array } reviews ? reviews.EnumerateArray().ToArray() : [];
        for (int i = 0; i < GuidedCapture.Steps.Count; i++)
        {
            string name = GuidedCapture.Steps[i].Name;
            JsonElement? frame = frames.Cast<JsonElement?>().FirstOrDefault(f => Text(f!.Value, "Name") == name);
            JsonElement? review = inspections.Cast<JsonElement?>().FirstOrDefault(r => Find(r!.Value, "Frame") is { } f && Text(f, "Name") == name);
            int attempts = record?.Events.Count(e => e.StartsWith($"CaptureMove:{i}:", StringComparison.Ordinal)) ?? 0;
            bool completed = record?.Events.Contains($"CaptureCompleted:{i}") == true;
            bool failedHere = stopped.StartsWith($"CaptureMove:{i}:", StringComparison.Ordinal);
            bool rejected = review is { } r && !Detected(r);
            var parts = new List<string>();
            if (frame is { } f)
            {
                if (Number(f, "X") is { } x && Number(f, "Y") is { } y) parts.Add($"位置 X={x:F4} Y={y:F4}");
                if (Find(f, "Capture", "RequestedExposureUs") is { ValueKind: JsonValueKind.Number } exposure) parts.Add($"曝光 {exposure.GetDouble():F0}µs");
            }
            if (attempts > 1) parts.Add($"纠偏重拍 {attempts - 1} 次");
            if (review is { } inspected) parts.Add((Detected(inspected) ? "参考孔已识别" : "参考孔未通过") + (Text(inspected, "Message") is { Length: > 0 } message ? "：" + message : ""));
            var state = failedHere || rejected ? ProcessStepState.Failed : frame is not null || completed ? ProcessStepState.Passed : ProcessStepState.NotRun;
            steps.Add(new("采图 " + name, state, (parts.Count > 0 ? string.Join("；", parts) : missing.Length > 0 ? missing : "没有这一处的照片") + (failedHere ? Failure() : ""),
                reportFile is var data && File.Exists(data) ? data : runFile, frame is { } image ? Locate(jobFolder, Text(image, "ImagePath")) : null));
        }

        // 检查文件在没拍完时也会写明“还缺哪几处”；只有该步需要的照片都在、却没有结果，才算这一步失败。
        bool Has(RingFrameRole role) => GuidedCapture.Steps.Where(s => s.Role == role).All(s => frames.Any(f => Text(f, "Name") == s.Name));
        bool arcsReady = Has(RingFrameRole.RingArc), allReady = arcsReady && Has(RingFrameRole.Reference1) && Has(RingFrameRole.Reference11);
        var ring = report is null ? null : Find(report.RootElement, "Ring");
        string rejection = report is null ? "" : Text(report.RootElement, "Rejection");
        steps.Add(ring is { ValueKind: JsonValueKind.Object } fit
            ? new("圆环拟合", ProcessStepState.Passed, $"圆心 X={Number(fit, "CenterX"):F4} Y={Number(fit, "CenterY"):F4}；半径 {Number(fit, "RadiusMm"):F4}mm；" +
                $"取边点 {(Find(fit, "Points") is { ValueKind: JsonValueKind.Array } points ? points.GetArrayLength() : Number(fit, "PointCount") ?? 0)} 个；RMS {Number(fit, "RmsMm") * 1000:F1}µm；敏感性 {Number(fit, "SensitivityMm") * 1000:F1}µm", reportFile)
            : new("圆环拟合", arcsReady && rejection.Length > 0 ? ProcessStepState.Failed : ProcessStepState.NotRun,
                !arcsReady && frames.Length > 0 ? "四处圆环照片还没拍齐，未进行拟合" : rejection.Length > 0 ? "未得到圆环结果：" + rejection
                    : missing.Length > 0 ? missing : "还没有圆环拟合结果", File.Exists(reportFile) ? reportFile : null));

        var direction = report is null ? null : Find(report.RootElement, "Result", "Direction");
        steps.Add(direction is { ValueKind: JsonValueKind.Object } angle
            ? new("参考孔方向", ProcessStepState.Passed, $"12点方向 {Number(angle, "TwelveAngleDeg"):F4}°；11点参考 {Number(angle, "Reference11AngleDeg"):F4}°", reportFile)
            : new("参考孔方向", allReady && ring is not null && rejection.Length > 0 ? ProcessStepState.Failed : ProcessStepState.NotRun,
                !allReady && frames.Length > 0 ? "六处照片还没拍齐，未计算方向" : ring is null && frames.Length > 0 ? "圆环拟合没有结果，未计算方向"
                    : rejection.Length > 0 ? "未得到方向：" + rejection : missing.Length > 0 ? missing : "还没有方向结果", File.Exists(reportFile) ? reportFile : null));

        if (record is null) return steps;
        string compensation = Path.Combine(runFolder!, "定位与补偿.json");
        bool located = File.Exists(compensation);
        int targets = 0;
        if (located) { using var doc = Parse(compensation); targets = Find(doc.RootElement, "Targets") is { ValueKind: JsonValueKind.Array } list ? list.GetArrayLength() : 0; }
        steps.Add(new("18孔坐标与落点补偿", located ? ProcessStepState.Passed : stopped == "Locating" ? ProcessStepState.Failed : ProcessStepState.NotRun,
            located ? $"已生成 {targets} 个补偿后的加工目标" : stopped == "Locating" ? "定位计算未完成" + Failure() : "未执行到这一步",
            located ? compensation : runFile));

        foreach (int id in Enumerable.Range(1, 18))
        {
            string folder = Path.Combine(runFolder!, "hole_" + id), trial = Path.Combine(folder, "trial.json"), after = Path.Combine(folder, "after.bmp");
            bool known = record.Holes.TryGetValue(id, out var phase);
            bool failedHere = stopped is { Length: > 0 } && stopped.Split(':') is [("HoleMove" or "HoleStarted"), var hole] && hole == id.ToString(CultureInfo.InvariantCulture);
            var state = known && phase == ProductionHolePhase.Completed ? ProcessStepState.Passed : known || failedHere ? ProcessStepState.Failed : ProcessStepState.NotRun;
            var parts = new List<string> { ProductionJobArchive.HoleText(record, id) };
            if (record.HoleTimings.TryGetValue(id, out var timing))
            {
                parts.Add($"目标 X={timing.X:F4} Y={timing.Y:F4}");
                if (timing.FinishedAt is { } finished) parts.Add($"用时 {(finished - timing.StartedAt).TotalSeconds:F1}秒");
            }
            if (File.Exists(trial))
            {
                using var doc = Parse(trial);
                if (Find(doc.RootElement, "ShutdownConfirmed") is { } off && off.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    parts.Add(off.GetBoolean() ? "关光已确认" : "关光未确认");
                if (Text(doc.RootElement, "Error") is { Length: > 0 } error && error != "null") parts.Add("单孔记录异常：" + error.ReplaceLineEndings(" "));
            }
            steps.Add(new($"孔 {id}", state, string.Join("；", parts) + (failedHere ? Failure() : ""), File.Exists(trial) ? trial : runFile, File.Exists(after) ? after : null));
        }

        bool done = record.Completed;
        steps.Add(new("收尾", done ? ProcessStepState.Passed : record.FinishedAt is not null || record.Phase == "Interrupted" ? ProcessStepState.Failed : ProcessStepState.NotRun,
            $"{ProductionJobArchive.PhaseText(record)}；关光确认：{Flag(record.LaserOffConfirmed)}；主控退出确认：{Flag(record.NcExitConfirmed)}；" +
            (record.ResultArchivePath is not null ? "资料包已生成" : record.ArchiveError is { } archive ? "资料包未生成：" + archive : "没有资料包") +
            (done || stopped.StartsWith("Capture") || stopped.StartsWith("Hole") || stopped == "Locating" || stopped.StartsWith("Starting") ? "" : Failure()), runFile));
        return steps;
    }

    private static string Flag(bool? value) => value switch { true => "是", false => "否", _ => "未知" };
    private static JsonDocument Parse(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static bool Detected(JsonElement review) => Find(review, "Status") is { } status &&
        (status.ValueKind == JsonValueKind.String ? status.GetString() == nameof(DatumHoleInspectionStatus.Detected)
            : status.ValueKind == JsonValueKind.Number && status.GetInt32() == (int)DatumHoleInspectionStatus.Detected);
    /// <summary>记录里的照片路径可能是采集电脑上的绝对路径；资料被复制到别处后改按工件目录里的同名文件找。</summary>
    private static string? Locate(string jobFolder, string imagePath)
    {
        if (imagePath.Length == 0) return null;
        if (File.Exists(imagePath)) return imagePath;
        string local = Path.Combine(jobFolder, imagePath[(imagePath.LastIndexOfAny(['/', '\\']) + 1)..]);
        return File.Exists(local) ? local : null;
    }
    private static JsonElement? Find(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var next) || next.ValueKind == JsonValueKind.Null) return null;
            element = next;
        }
        return element;
    }
    private static double? Number(JsonElement element, string name) =>
        Find(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out double result) ? result : null;
    private static string Text(JsonElement element, string name) =>
        Find(element, name) is { } value ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText() : "";
}
