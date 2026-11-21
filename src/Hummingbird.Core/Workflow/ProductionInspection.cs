using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hummingbird.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InspectionVerdict { Passed, NeedsRework, Scrapped }

public sealed record InspectionEntry(Guid RunId, InspectionVerdict Verdict, string Notes, DateTimeOffset At);

/// <summary>
/// 人工检验结果登记。按工件目录（即一轮真实加工）保存在独立文件里，保留每次登记的历史；
/// 不改加工记录和资料包，也不代替补加工入口自身的来源校验。
/// </summary>
public static class ProductionInspection
{
    public const string FileName = "检验结果.json";
    public const int MaximumNotesLength = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Text(InspectionVerdict verdict) => verdict switch
    {
        InspectionVerdict.Passed => "合格",
        InspectionVerdict.NeedsRework => "需补加工",
        _ => "报废"
    };

    public static IReadOnlyList<InspectionEntry> History(string jobFolder)
    {
        string path = Path.Combine(jobFolder, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<List<InspectionEntry>>(File.ReadAllText(path)) ?? [] : [];
    }

    /// <summary>当前有效的检验结果；没有登记过、文件读不出来或登记的不是本目录这一轮时返回null。</summary>
    public static InspectionEntry? Latest(string jobFolder)
    {
        try
        {
            var latest = History(jobFolder).LastOrDefault();
            return latest is not null && latest.RunId == MachinedRun(jobFolder)?.RunId ? latest : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static InspectionEntry Save(string jobFolder, InspectionVerdict verdict, string? notes)
    {
        notes = (notes ?? "").Trim();
        if (!Enum.IsDefined(verdict)) throw new ArgumentException("检验结果无效。");
        if (notes.Length > MaximumNotesLength) throw new ArgumentException($"检验备注不超过{MaximumNotesLength}字。");
        var run = MachinedRun(jobFolder) ?? throw new InvalidOperationException("该目录没有真实加工记录，不能登记检验结果。");
        var history = History(jobFolder).ToList();
        var entry = new InspectionEntry(run.RunId, verdict, notes, DateTimeOffset.Now);
        history.Add(entry);
        string path = Path.Combine(jobFolder, FileName), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(history, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return entry;
    }

    private static ProductionRunRecord? MachinedRun(string jobFolder)
    {
        string path = Path.Combine(jobFolder, ProductionJobArchive.MachiningRecordName);
        return File.Exists(path) ? JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(path)) : null;
    }
}
