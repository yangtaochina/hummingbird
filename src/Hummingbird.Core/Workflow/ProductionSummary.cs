using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Hummingbird.Core;

/// <summary>
/// 跨工件的加工汇总表（CSV）。每行由一个运行目录里已保存的记录推导，只读原记录、不改原记录；
/// 汇总表可随时从历史目录重建，不是加工记录的来源，也不参与补加工校验。
/// </summary>
public static class ProductionSummary
{
    public const string FileName = "加工汇总.csv";
    public const string RunFolderPrefix = "整件_";

    public static readonly string[] Header =
    [
        "运行号", "工件号", "开始时间", "结束时间", "耗时秒", "类型", "设备模式", "补加工轮次", "来源运行号",
        "每孔请求秒", "功率原始值", "累计每孔请求秒", "采图完成数", "完成孔数", "待核对孔", "结果", "失败分类", "停止阶段", "异常信息",
        "关光确认", "主控退出确认", "圆心X", "圆心Y", "圆环半径mm", "圆环RMSµm", "圆环敏感性µm", "12点角度",
        "六处曝光µs", "纠偏重拍次数", "采图最大偏离路线µm", "检验结果", "检验备注", "检验时间", "工件目录", "运行目录"
    ];

    /// <summary>失败发生在哪一类步骤；完成返回空串。</summary>
    public static string FailureCategory(ProductionRunRecord record)
    {
        if (record.Completed) return "";
        if (record.Phase == "Canceled") return "人工停止";
        if (record.Phase == "Interrupted") return "运行中断";
        string stage = record.StoppedDuring ?? record.Phase;
        return stage.Split(':')[0] switch
        {
            "Created" or "Starting" => "启动阶段",
            "CaptureMove" or "CaptureCompleted" => "采图或识别",
            "Locating" => "定位计算",
            "HoleMove" or "HoleStarted" or "HoleCompleted" => "逐孔加工",
            _ => "其他"
        };
    }

    public static string ResultText(ProductionRunRecord record) =>
        record.Completed ? "完成" : record.Phase switch
        {
            "Canceled" => "已停止",
            "Interrupted" => "中断",
            "Failed" => "异常",
            _ => "未结束"
        };

    /// <summary>读取一个运行目录（工件目录/整件_运行号）并生成一行；缺少可选文件时对应列留空。</summary>
    public static string[] Read(string runFolder)
    {
        runFolder = Path.GetFullPath(runFolder);
        string jobFolder = Path.GetDirectoryName(runFolder) ?? throw new InvalidDataException("运行目录没有上级工件目录。");
        var saved = JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(Path.Combine(runFolder, "run.json")))
            ?? throw new InvalidDataException("run.json为空。");
        var record = ProductionJobArchive.ReviewAfterRestart(saved);

        string deviceMode = "", power = record.Rework?.PowerRaw.ToString(CultureInfo.InvariantCulture) ?? "";
        var route = new List<(double X, double Y)>();
        Optional(Path.Combine(runFolder, "运行来源.json"), source =>
        {
            deviceMode = Text(source, "DeviceMode");
            if (power.Length == 0 && Find(source, "LaserPreset", "Settings", "Power") is { ValueKind: JsonValueKind.Number } p)
                power = p.GetRawText();
            if (Find(source, "CaptureRoute") is { ValueKind: JsonValueKind.Array } points)
                foreach (var point in points.EnumerateArray())
                    if (Number(point, "X") is { } x && Number(point, "Y") is { } y) route.Add((x, y));
        });

        string centerX = "", centerY = "", radius = "", rms = "", sensitivity = "", twelve = "", exposures = "", deviation = "";
        Optional(Path.Combine(jobFolder, "定位检查与预览.json"), report =>
        {
            if (Find(report, "Ring") is { ValueKind: JsonValueKind.Object } ring)
            {
                centerX = Format(Number(ring, "CenterX"), "F4"); centerY = Format(Number(ring, "CenterY"), "F4");
                radius = Format(Number(ring, "RadiusMm"), "F4");
                rms = Format(Number(ring, "RmsMm") * 1000, "F1"); sensitivity = Format(Number(ring, "SensitivityMm") * 1000, "F1");
            }
            if (Find(report, "Result", "Direction") is { ValueKind: JsonValueKind.Object } direction)
                twelve = Format(Number(direction, "TwelveAngleDeg"), "F4");
            if (Find(report, "SourceFrames") is not { ValueKind: JsonValueKind.Array } frames) return;
            var list = frames.EnumerateArray().ToArray();
            exposures = string.Join(";", list.Select(f => Find(f, "Capture", "RequestedExposureUs") is { ValueKind: JsonValueKind.Number } e
                ? e.GetDouble().ToString("F0", CultureInfo.InvariantCulture) : ""));
            // 路线点与实际采图位置的差即累计纠偏量（含读数噪声）；帧数与路线不一致时不猜测对应关系。
            if (route.Count == list.Length && list.Length > 0 && list.All(f => Number(f, "X") is not null && Number(f, "Y") is not null))
                deviation = Format(list.Select((f, i) => Math.Sqrt(Math.Pow(Number(f, "X")!.Value - route[i].X, 2) +
                    Math.Pow(Number(f, "Y")!.Value - route[i].Y, 2))).Max() * 1000, "F1");
        });

        // CaptureMove:步骤:第几次；次数大于0表示该处纠偏后重拍。
        int recaptures = record.Events.Select(e => e.Split(':')).Where(p => p.Length == 3 && p[0] == "CaptureMove" &&
            int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)).Sum(p => int.Parse(p[2], CultureInfo.InvariantCulture) > 0 ? 1 : 0);
        double? seconds = record.StartedAt is { } start && record.FinishedAt is { } finish ? (finish - start).TotalSeconds : null;
        long? cumulative = record.Rework?.CumulativeRequestedMs ?? record.RequestedExposureMs;
        // 检验登记属于本目录的真实加工轮次；无出光试走的行不带检验结果。
        var inspection = ProductionInspection.Latest(jobFolder) is { } latest && latest.RunId == record.RunId ? latest : null;
        return
        [
            record.RunId.ToString(), (record.Rework?.PhysicalWorkpieceId ?? record.RunId).ToString(),
            Time(record.StartedAt), Time(record.FinishedAt), Format(seconds, "F1"),
            record.DryRun ? "无出光试走" : record.Rework is null ? "新件加工" : "同件补加工", deviceMode,
            record.Rework?.Round.ToString(CultureInfo.InvariantCulture) ?? "", record.Rework?.ParentRunId.ToString() ?? "",
            Format(record.RequestedExposureMs / 1000.0, "0.###"), power, Format(cumulative / 1000.0, "0.###"),
            record.CapturedSteps.ToString(CultureInfo.InvariantCulture),
            record.Holes.Count(h => h.Value == ProductionHolePhase.Completed).ToString(CultureInfo.InvariantCulture),
            string.Join(";", record.Holes.Where(h => h.Value != ProductionHolePhase.Completed).Select(h => h.Key).Order()),
            ResultText(record), FailureCategory(record), record.StoppedDuring ?? "", (record.Error ?? "").ReplaceLineEndings(" "),
            record.LaserOffConfirmed?.ToString() ?? "", record.NcExitConfirmed.ToString(),
            centerX, centerY, radius, rms, sensitivity, twelve, exposures,
            recaptures.ToString(CultureInfo.InvariantCulture), deviation,
            inspection is null ? "" : ProductionInspection.Text(inspection.Verdict), inspection?.Notes.ReplaceLineEndings(" ") ?? "", Time(inspection?.At),
            Path.GetFileName(jobFolder), Path.GetFileName(runFolder)
        ];
    }

    /// <summary>把一个运行目录写入汇总表；同一运行号只保留最新一行。表头与当前版本不同时整表重建。</summary>
    public static void Upsert(string captureRoot, string runFolder)
    {
        string path = Path.Combine(captureRoot, FileName);
        var row = Read(runFolder);
        List<string[]>? rows = File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
        if (rows.Count > 0 && !rows[0].SequenceEqual(Header)) { Rebuild(captureRoot); return; }
        var body = rows.Skip(1).Where(r => r.Length == Header.Length && r[0] != row[0]).Append(row);
        Write(path, body);
    }

    /// <summary>扫描采图根目录下所有工件目录的运行记录并重写汇总表。返回行数和读不出来的目录。</summary>
    public static (int Rows, IReadOnlyList<string> Skipped) Rebuild(string captureRoot)
    {
        var rows = new List<string[]>(); var skipped = new List<string>();
        if (Directory.Exists(captureRoot))
            foreach (string job in Directory.EnumerateDirectories(captureRoot))
                foreach (string run in Directory.EnumerateDirectories(job, RunFolderPrefix + "*"))
                {
                    if (!File.Exists(Path.Combine(run, "run.json"))) continue;
                    try { rows.Add(Read(run)); }
                    catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
                    { skipped.Add(Path.GetFileName(job) + "/" + Path.GetFileName(run) + "：" + ex.Message); }
                }
        Directory.CreateDirectory(captureRoot);
        Write(Path.Combine(captureRoot, FileName), rows);
        return (rows.Count, skipped);
    }

    private static void Write(string path, IEnumerable<string[]> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows.OrderBy(r => r[2], StringComparer.Ordinal).ThenBy(r => r[0], StringComparer.Ordinal).Prepend(Header))
            text.Append(string.Join(",", row.Select(Escape))).Append("\r\n");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 带BOM，Excel才会按UTF-8打开中文列名。
            File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(true));
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var field = new StringBuilder(); bool quoted = false;
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row.ToArray()); row.Clear(); }
            else if (c != '\r') field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private static void Optional(string path, Action<JsonElement> read)
    {
        if (!File.Exists(path)) return;
        try { using var document = JsonDocument.Parse(File.ReadAllText(path)); read(document.RootElement); }
        catch (JsonException) { }
    }
    private static JsonElement? Find(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var next)) return null;
            element = next;
        }
        return element;
    }
    private static double? Number(JsonElement element, string name) =>
        Find(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out double result) && double.IsFinite(result) ? result : null;
    private static string Text(JsonElement element, string name) =>
        Find(element, name) is { } value ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText() : "";
    private static string Format(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "";
    private static string Time(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
}
