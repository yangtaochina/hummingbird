using System.Text.Json;
using Hummingbird.Core;

internal static class ProductionSummaryChecks
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            passed++; Console.WriteLine("[PASS] " + name);
        }
        string root = Path.Combine(Path.GetTempPath(), "hummingbird-summary-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Column(string[] row, string name) => row[Array.IndexOf(ProductionSummary.Header, name)];
            string Job(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
            string Save(string job, ProductionRunRecord record)
            {
                string run = Path.Combine(job, ProductionSummary.RunFolderPrefix + record.RunId.ToString("N"));
                ProductionJobArchive.SaveRunRecord(run, record); return run;
            }
            var started = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(8));
            ProductionRunRecord Completed(DateTimeOffset at, ProductionReworkInfo? rework = null, int ms = 3000) => new()
            {
                DryRun = false, RequestedExposureMs = ms, Rework = rework, Phase = "Completed", Completed = true, CapturedSteps = 6,
                StartedAt = at, FinishedAt = at.AddSeconds(125.5), LaserOffConfirmed = true, NcExitConfirmed = true,
                Holes = Enumerable.Range(1, 18).ToDictionary(i => i, _ => ProductionHolePhase.Completed),
                Events = ["Starting", "CaptureMove:0:0", "CaptureMove:0:1", "CaptureCompleted:0", "CaptureMove:1:0", "CaptureMove:5:0", "CaptureMove:5:1", "CaptureMove:5:2"]
            };

            string firstJob = Job("工件_20261120_090000000_aaaaaa");
            var first = Completed(started); string firstRun = Save(firstJob, first);
            File.WriteAllText(Path.Combine(firstRun, "运行来源.json"), JsonSerializer.Serialize(new
            {
                DeviceMode = "Sim", LaserPreset = new { RequestedExposureMs = 3000, Settings = new { Power = 4200 } },
                CaptureRoute = new[] { new { X = 10.0, Y = 20.0 }, new { X = 30.0, Y = 40.0 } }
            }));
            File.WriteAllText(Path.Combine(firstJob, "定位检查与预览.json"), JsonSerializer.Serialize(new
            {
                Ring = new { CenterX = -147.222228, CenterY = -196.433674, RadiusMm = 14.466494, RmsMm = 0.0244, SensitivityMm = 0.0063 },
                Result = new { Direction = new { TwelveAngleDeg = 91.25 } },
                SourceFrames = new object[]
                {
                    new { X = 10.03, Y = 20.04, Capture = new { RequestedExposureUs = 5000.0 } },
                    new { X = 30.0, Y = 40.0, Capture = new { RequestedExposureUs = 1250.0 } }
                }
            }));
            var row = ProductionSummary.Read(firstRun);
            Check(row.Length == ProductionSummary.Header.Length && Column(row, "运行号") == first.RunId.ToString() && Column(row, "工件号") == first.RunId.ToString() &&
                Column(row, "类型") == "新件加工" && Column(row, "结果") == "完成" && Column(row, "失败分类") == "" && Column(row, "完成孔数") == "18" &&
                Column(row, "耗时秒") == "125.5" && Column(row, "每孔请求秒") == "3" && Column(row, "累计每孔请求秒") == "3",
                "新件完成轮次：运行号即工件号，时长、完成孔数和结果从run.json读出");
            Check(Column(row, "设备模式") == "Sim" && Column(row, "功率原始值") == "4200" && Column(row, "圆环RMSµm") == "24.4" &&
                Column(row, "圆环敏感性µm") == "6.3" && Column(row, "圆心X") == "-147.2222" && Column(row, "12点角度") == "91.2500" &&
                Column(row, "六处曝光µs") == "5000;1250", "功率、设备模式、圆环拟合和曝光来自运行来源与定位检查文件");
            Check(Column(row, "纠偏重拍次数") == "3" && Column(row, "采图最大偏离路线µm") == "50.0",
                "纠偏重拍次数取自事件，纠偏量取实际采图位置与路线之差");

            var info = new ProductionReworkInfo(Guid.NewGuid(), first.RunId, first.RunId, 1, "run.json", "00", "manifest.json", "00", 3000, 3000, 1500, 4300, "追加");
            string reworkJob = Job("补加工_20261120_100000000_bbbbbb");
            var rework = Completed(started.AddHours(1), info, 1500); string reworkRun = Save(reworkJob, rework);
            var reworkRow = ProductionSummary.Read(reworkRun);
            Check(Column(reworkRow, "类型") == "同件补加工" && Column(reworkRow, "工件号") == first.RunId.ToString() && Column(reworkRow, "补加工轮次") == "1" &&
                Column(reworkRow, "来源运行号") == first.RunId.ToString() && Column(reworkRow, "每孔请求秒") == "1.5" &&
                Column(reworkRow, "累计每孔请求秒") == "4.5" && Column(reworkRow, "功率原始值") == "4300" && Column(reworkRow, "圆环RMSµm") == "",
                "补加工轮次沿用首轮工件号并给出累计时长；缺少可选文件时对应列留空");

            string failedJob = Job("工件_20261120_110000000_cccccc");
            var failed = new ProductionRunRecord
            {
                DryRun = false, RequestedExposureMs = 3000, Phase = "Failed", StoppedDuring = "CaptureMove:5:2", CapturedSteps = 5,
                Error = "参考11点照片已保存，但实体圆边未通过：\"太暗\"，请重拍\r\n第二行", StartedAt = started.AddHours(2), FinishedAt = started.AddHours(2).AddSeconds(40),
                LaserOffConfirmed = true
            };
            string failedRun = Save(failedJob, failed);
            var failedRow = ProductionSummary.Read(failedRun);
            Check(Column(failedRow, "结果") == "异常" && Column(failedRow, "失败分类") == "采图或识别" && Column(failedRow, "完成孔数") == "0" &&
                !Column(failedRow, "异常信息").Contains('\n'), "采图阶段失败归入“采图或识别”，异常信息压成一行");
            Check(ProductionSummary.FailureCategory(new() { Phase = "Failed", StoppedDuring = "Locating" }) == "定位计算" &&
                ProductionSummary.FailureCategory(new() { Phase = "Failed", StoppedDuring = "HoleStarted:7" }) == "逐孔加工" &&
                ProductionSummary.FailureCategory(new() { Phase = "Canceled", StoppedDuring = "HoleMove:3" }) == "人工停止" &&
                ProductionSummary.FailureCategory(new() { Phase = "Failed", StoppedDuring = "Starting" }) == "启动阶段", "失败分类按停止阶段区分定位、逐孔、人工停止和启动");

            string interruptedJob = Job("工件_20261120_120000000_dddddd");
            var interrupted = new ProductionRunRecord
            {
                DryRun = false, RequestedExposureMs = 3000, Phase = "HoleStarted:4", CapturedSteps = 6, CurrentHoleId = 4, StartedAt = started.AddHours(3),
                Holes = new() { [1] = ProductionHolePhase.Completed, [2] = ProductionHolePhase.Completed, [3] = ProductionHolePhase.Completed, [4] = ProductionHolePhase.Started }
            };
            var interruptedRow = ProductionSummary.Read(Save(interruptedJob, interrupted));
            Check(Column(interruptedRow, "结果") == "中断" && Column(interruptedRow, "失败分类") == "运行中断" && Column(interruptedRow, "完成孔数") == "3" &&
                Column(interruptedRow, "待核对孔") == "4" && Column(interruptedRow, "停止阶段") == "HoleStarted:4",
                "未正常结束的记录按中断处理，进行中的孔列为待核对");

            string path = Path.Combine(root, ProductionSummary.FileName);
            ProductionSummary.Upsert(root, firstRun); ProductionSummary.Upsert(root, failedRun); ProductionSummary.Upsert(root, firstRun);
            var table = ProductionSummary.Parse(File.ReadAllText(path));
            Check(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) && table.Count == 3 && table[0].SequenceEqual(ProductionSummary.Header) &&
                table[1][0] == first.RunId.ToString() && table[2][0] == failed.RunId.ToString(), "逐次追加：带BOM、同一运行号只保留一行、按开始时间排序");
            Check(table[2].SequenceEqual(failedRow), "含逗号、引号的异常信息写入CSV后能原样读回");

            Directory.CreateDirectory(Path.Combine(failedJob, ProductionSummary.RunFolderPrefix + "broken"));
            File.WriteAllText(Path.Combine(failedJob, ProductionSummary.RunFolderPrefix + "broken", "run.json"), "{不是JSON");
            Directory.CreateDirectory(Path.Combine(root, "整件模拟_20261120_130000000"));
            var (rows, skipped) = ProductionSummary.Rebuild(root);
            table = ProductionSummary.Parse(File.ReadAllText(path));
            Check(rows == 4 && table.Count == 5 && skipped.Count == 1 && skipped[0].Contains("broken") &&
                table.Skip(1).Select(r => r[0]).SequenceEqual(new[] { first.RunId, rework.RunId, failed.RunId, interrupted.RunId }.Select(g => g.ToString())),
                "从历史目录重建：四个运行目录各一行，读不出来的目录单独列出、不中断重建");

            File.WriteAllText(path, "旧表头,只有两列\r\n1,2\r\n");
            ProductionSummary.Upsert(root, reworkRun);
            table = ProductionSummary.Parse(File.ReadAllText(path));
            Check(table.Count == 5 && table[0].SequenceEqual(ProductionSummary.Header), "表头与当前版本不同时整表重建，不把新旧格式混在一张表里");

            bool Rejects(Action action) { try { action(); return false; } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return true; } }
            Check(ProductionInspection.Latest(firstJob) is null && Rejects(() => ProductionInspection.Save(firstJob, InspectionVerdict.Passed, "")) &&
                !File.Exists(Path.Combine(firstJob, ProductionInspection.FileName)), "没有真实加工记录的目录不能登记检验结果");
            ProductionJobArchive.Begin(firstJob, first);
            Check(Rejects(() => ProductionInspection.Save(firstJob, InspectionVerdict.Passed, new string('长', ProductionInspection.MaximumNotesLength + 1))) &&
                Rejects(() => ProductionInspection.Save(firstJob, (InspectionVerdict)9, "")), "过长备注和无效结果被拒绝");
            ProductionInspection.Save(firstJob, InspectionVerdict.NeedsRework, " 3号、7号孔偏小 ");
            var latestInspection = ProductionInspection.Save(firstJob, InspectionVerdict.Passed, "复检合格");
            Check(ProductionInspection.History(firstJob).Count == 2 && ProductionInspection.History(firstJob)[0].Notes == "3号、7号孔偏小" &&
                ProductionInspection.Latest(firstJob) == latestInspection && latestInspection.RunId == first.RunId,
                "检验登记保留历史，最后一次为当前结果，并记下对应的运行号");
            row = ProductionSummary.Read(firstRun);
            Check(Column(row, "检验结果") == "合格" && Column(row, "检验备注") == "复检合格" && Column(row, "检验时间").Length == 19 &&
                Column(ProductionSummary.Read(failedRun), "检验结果") == "", "汇总表带出当前检验结果，未登记的留空");
            string dryRun = Save(firstJob, new ProductionRunRecord { DryRun = true, Phase = "Completed", Completed = true, StartedAt = started.AddMinutes(-5) });
            Check(Column(ProductionSummary.Read(dryRun), "检验结果") == "" && Column(ProductionSummary.Read(dryRun), "类型") == "无出光试走",
                "同一目录里的无出光试走不带检验结果");
            Directory.Delete(dryRun, true); File.Delete(Path.Combine(firstJob, ProductionInspection.FileName));
            File.Delete(Path.Combine(firstJob, ProductionJobArchive.MachiningRecordName));

            string before = File.ReadAllText(Path.Combine(firstRun, "run.json"));
            ProductionSummary.Rebuild(root);
            Check(File.ReadAllText(Path.Combine(firstRun, "run.json")) == before && Directory.GetFiles(firstRun).Length == 4 &&
                Directory.GetFiles(root).Select(Path.GetFileName).SequenceEqual([ProductionSummary.FileName]),
                "汇总只读原记录：运行目录不新增或改写文件，根目录只有汇总表，不留临时文件");
            Console.WriteLine($"Production summary checks: {passed} passed.");
            return 0;
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
