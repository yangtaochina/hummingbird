using System.Text.Json;
using Hummingbird.Core;

internal static class ProcessTraceChecks
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            passed++; Console.WriteLine("[PASS] " + name);
        }
        string root = Path.Combine(Path.GetTempPath(), "hummingbird-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var names = GuidedCapture.Steps.Select(s => s.Name).ToArray();
            string Job(string name, int frames, bool ring, string rejection = "", bool elevenRejected = false)
            {
                string job = Path.Combine(root, name); Directory.CreateDirectory(job);
                foreach (string frame in names.Take(frames)) File.WriteAllBytes(Path.Combine(job, frame + ".bmp"), [1]);
                File.WriteAllText(Path.Combine(job, "定位检查与预览.json"), JsonSerializer.Serialize(new
                {
                    GeneratedAt = DateTimeOffset.Now.AddMinutes(-4.5), Rejection = rejection,
                    // 记录里是采集电脑上的绝对路径，本机不存在。
                    SourceFrames = names.Take(frames).Select((n, i) => new { Name = n, ImagePath = @"D:\现场\不存在\" + n + ".bmp", X = 10.0 + i, Y = 20.0, Capture = new { RequestedExposureUs = 5000.0 } }),
                    ReferenceInspections = new[] { new { Frame = new { Name = names[0] }, Status = (object)"Detected", Message = "通过" } }
                        .Concat(frames == 6 ? [new { Frame = new { Name = names[5] }, Status = (object)(elevenRejected ? 1 : 0), Message = elevenRejected ? "画面太暗" : "通过" }] : []),
                    Ring = ring ? new { CenterX = -147.2222, CenterY = -196.4337, RadiusMm = 14.4665, RmsMm = 0.0244, SensitivityMm = 0.0063, Points = new[] { 1, 2, 3 } } : null,
                    Result = ring && rejection.Length == 0 ? new { Direction = new { TwelveAngleDeg = 91.25, Reference11AngleDeg = 121.25 } } : null
                }));
                return job;
            }
            string Run(string job, ProductionRunRecord record, bool startup = true, bool located = true)
            {
                string run = Path.Combine(job, ProductionSummary.RunFolderPrefix + record.RunId.ToString("N"));
                ProductionJobArchive.SaveRunRecord(run, record);
                if (startup) File.WriteAllText(Path.Combine(run, "自动启动检查.json"), "{}");
                if (located) File.WriteAllText(Path.Combine(run, "定位与补偿.json"), JsonSerializer.Serialize(new { Targets = Enumerable.Range(1, 18).Select(i => new { Id = i }) }));
                foreach (int id in record.Holes.Keys)
                {
                    Directory.CreateDirectory(Path.Combine(run, "hole_" + id));
                    File.WriteAllText(Path.Combine(run, "hole_" + id, "trial.json"), JsonSerializer.Serialize(new { ShutdownConfirmed = true, Error = (string?)null }));
                    File.WriteAllBytes(Path.Combine(run, "hole_" + id, "after.bmp"), [1]);
                }
                return run;
            }
            var at = DateTimeOffset.Now.AddMinutes(-5);
            string[] Captured(int count) => Enumerable.Range(0, count).SelectMany(i => new[] { $"CaptureMove:{i}:0", $"CaptureCompleted:{i}" }).Prepend("Starting").ToArray();

            string goodJob = Job("工件_完成", 6, true);
            var good = new ProductionRunRecord
            {
                DryRun = false, RequestedExposureMs = 3000, Phase = "Completed", Completed = true, CapturedSteps = 6, StartedAt = at, FinishedAt = at.AddMinutes(2),
                LaserOffConfirmed = true, NcExitConfirmed = true, ResultArchivePath = "本件资料.zip", Events = [.. Captured(6), "CaptureMove:5:1"],
                Holes = Enumerable.Range(1, 18).ToDictionary(i => i, _ => ProductionHolePhase.Completed),
                HoleTimings = Enumerable.Range(1, 18).ToDictionary(i => i, i => new ProductionHoleTiming { X = i, Y = -i, StartedAt = at, FinishedAt = at.AddSeconds(4) })
            };
            ProductionJobArchive.Begin(goodJob, good); string goodRun = Run(goodJob, good);
            var steps = ProcessTrace.Read(goodJob);
            Check(steps.Count == 29 && steps.Select(s => s.Name).SequenceEqual(new[] { "启动检查" }.Concat(names.Select(n => "采图 " + n))
                .Concat(["圆环拟合", "参考孔方向", "18孔坐标与落点补偿"]).Concat(Enumerable.Range(1, 18).Select(i => $"孔 {i}")).Append("收尾")) &&
                steps.All(s => s.State == ProcessStepState.Passed), "完成的一件按加工顺序列出29步且全部通过");
            Check(steps[1].ImageFile == Path.Combine(goodJob, names[0] + ".bmp") && steps[1].Summary.Contains("曝光 5000µs") && steps[1].Summary.Contains("参考孔已识别") &&
                steps[6].Summary.Contains("纠偏重拍 1 次"), "采图步骤给出曝光、参考孔结论和重拍次数；照片路径失效时改用工件目录里的同名文件");
            Check(steps[7].Summary.Contains("RMS 24.4µm") && steps[7].Summary.Contains("取边点 3 个") && steps[8].Summary.Contains("91.2500°") &&
                steps[9].Summary.Contains("18 个") && steps[9].DataFile == Path.Combine(goodRun, "定位与补偿.json"), "圆环、方向和补偿步骤给出关键数值并指向原始数据文件");
            Check(steps[10].Summary.Contains("目标 X=1.0000 Y=-1.0000") && steps[10].Summary.Contains("用时 4.0秒") && steps[10].Summary.Contains("关光已确认") &&
                steps[10].DataFile == Path.Combine(goodRun, "hole_1", "trial.json") && steps[10].ImageFile == Path.Combine(goodRun, "hole_1", "after.bmp") &&
                steps[28].Summary.Contains("资料包已生成"), "逐孔步骤给出目标、用时和关光确认，并指向单孔记录和加工后照片");

            string darkJob = Job("工件_11点太暗", 6, true, "参考11点实体圆边未通过识别", elevenRejected: true);
            var dark = new ProductionRunRecord
            {
                DryRun = false, RequestedExposureMs = 3000, Phase = "Failed", StoppedDuring = "CaptureMove:5:2", Error = "采图未通过或超过有限纠偏次数/范围。", CapturedSteps = 5,
                StartedAt = at, FinishedAt = at.AddSeconds(50), LaserOffConfirmed = true, Events = [.. Captured(5), "CaptureMove:5:0", "CaptureMove:5:1", "CaptureMove:5:2"]
            };
            Run(darkJob, dark, located: false); steps = ProcessTrace.Read(darkJob);
            Check(steps.Take(6).All(s => s.State == ProcessStepState.Passed) && steps[6].State == ProcessStepState.Failed && steps[6].Summary.Contains("画面太暗") &&
                steps[6].Summary.Contains("纠偏重拍 2 次") && steps[6].Summary.Contains("超过有限纠偏次数") && steps[8].State == ProcessStepState.Failed &&
                steps[9].State == ProcessStepState.NotRun && steps.Skip(10).Take(18).All(s => s.State == ProcessStepState.NotRun) && steps[28].State == ProcessStepState.Failed,
                "第6处采图失败：该步标红并带原因，方向标红，之后的补偿和18孔显示未执行");

            string holeJob = Job("工件_孔7中断", 6, true);
            var hole = new ProductionRunRecord
            {
                DryRun = false, RequestedExposureMs = 3000, Phase = "HoleStarted:7", CapturedSteps = 6, CurrentHoleId = 7, StartedAt = at, UpdatedAt = at.AddMinutes(1), Events = [.. Captured(6)],
                Holes = Enumerable.Range(1, 7).ToDictionary(i => i, i => i < 7 ? ProductionHolePhase.Completed : ProductionHolePhase.Started)
            };
            Run(holeJob, hole); steps = ProcessTrace.Read(holeJob);
            Check(steps.Skip(10).Take(6).All(s => s.State == ProcessStepState.Passed) && steps[16].State == ProcessStepState.Failed && steps[16].Summary.Contains("待核对") &&
                steps.Skip(17).Take(11).All(s => s.State == ProcessStepState.NotRun) && steps[28].State == ProcessStepState.Failed && steps[28].Summary.Contains("中断"),
                "加工到孔7时程序中断：前6孔通过，孔7标红为待核对，后面的孔未执行，收尾标红");

            string partialJob = Job("工件_只拍了三处", 3, false);
            steps = ProcessTrace.Read(partialJob);
            Check(steps.Count == 8 && steps.Take(3).All(s => s.State == ProcessStepState.Passed) && steps.Skip(3).All(s => s.State == ProcessStepState.NotRun) &&
                ProcessTrace.DefaultRunFolder(partialJob) is null, "没有运行记录的工件只列出采图和定位步骤，没拍的显示未执行");
            string dryRun = Run(partialJob, new ProductionRunRecord { DryRun = true, Phase = "Canceled", StoppedDuring = "Starting", Error = "人工停止", StartedAt = at, FinishedAt = at }, startup: false, located: false);
            steps = ProcessTrace.Read(partialJob);
            Check(ProcessTrace.DefaultRunFolder(partialJob) == dryRun && ProcessTrace.DefaultRunFolder(goodJob) == goodRun && steps.Count == 29 &&
                steps[0].State == ProcessStepState.Failed && steps[0].Summary.Contains("人工停止"), "默认取真实加工轮次，否则取最近一次试走；启动阶段停止标在启动检查上");
            // 这件加工结束后被恢复并重新检查：工件目录的检查文件变成了事后的内容。
            string goodReport = Path.Combine(goodJob, ProductionSummary.JobReportName);
            var regenerated = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(goodReport))!;
            regenerated["GeneratedAt"] = DateTimeOffset.Now.AddHours(1); regenerated["Ring"]!["CenterX"] = -150.0001;
            File.WriteAllText(goodReport, regenerated.ToJsonString());
            steps = ProcessTrace.Read(goodJob);
            Check(steps[7].State == ProcessStepState.NotRun && steps[7].Summary.Contains("不代表本轮") && !steps[7].Summary.Contains("-150") &&
                steps[1].State == ProcessStepState.Passed && steps[10].State == ProcessStepState.Passed, "检查文件在本轮结束后被重新生成时，不把它显示成本轮的圆环结果");
            ProductionSummary.SaveLocatingSnapshot(goodRun, names.Select((n, i) => new RingScanFrame(n, Path.Combine(goodJob, n + ".bmp"), 10 + i, 20, 0, 0)).ToArray(),
                new DialAlignmentPreview(new RingScanResult(-147.2222, -196.4337, 14.4665, 0.0244, 0.0063, 0, 0, [new(0, 1, 1, 0, 0), new(1, 1, 1, 0, 0)], []),
                    new DatumDirectionResult(91.25, 121.25, 61.25, 60, 0, default!, default!), [], new DatumDirectionOptions(), []));
            steps = ProcessTrace.Read(goodJob);
            Check(steps[7].State == ProcessStepState.Passed && steps[7].Summary.Contains("X=-147.2222") && steps[7].Summary.Contains("取边点 2 个") &&
                steps[8].Summary.Contains("91.2500°") && steps[7].DataFile == Path.Combine(goodRun, ProductionSummary.LocatingSnapshotName) &&
                steps[1].ImageFile == Path.Combine(goodJob, names[0] + ".bmp"), "此时改用运行目录里保存的本轮定位结果");

            string[] Files() => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(f => f + "|" + File.GetLastWriteTimeUtc(f).Ticks).ToArray();
            var before = Files();
            foreach (string job in Directory.GetDirectories(root)) ProcessTrace.Read(job);
            Check(Files().SequenceEqual(before), "流程检查只读，不在记录目录里新增或改写文件");
            Console.WriteLine($"Process trace checks: {passed} passed.");
            return 0;
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
