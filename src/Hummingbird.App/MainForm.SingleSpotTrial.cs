using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Devices.LaserRtu;
using Hummingbird.Vision;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private readonly TextBox _spotTrialC = new() { Width = 95, PlaceholderText = "当前面板C" };
    private readonly TextBox _spotTrialNotes = new() { Width = 360, PlaceholderText = "位置、表面/材料、原厂参数备注（可写待确认）" };
    private CancellationTokenSource? _spotTrialStop;
    private bool _spotTrialDryRunActive;
    private Func<RingScanSettings>? _ringSettingsSnapshot;

    private Control BuildSingleSpotTrialControls()
    {
        _spotTrialDryRunButton = Btn("无出光演练并归档", async (_, _) =>
            await StartLaserTrialOperation(() => RunSingleSpotTrial(dryRun: true)));
        _spotTrialExposureButton = Btn("单点试打并保存前后图", async (_, _) =>
            await StartLaserTrialOperation(() => RunSingleSpotTrial(dryRun: false), refreshAfter: true));
        _spotTrialExposureButton.Enabled = false;
        var group = GroupBoxOf("单点试打采图归档（不移动机床）", Lbl("备注"), _spotTrialNotes, _spotTrialDryRunButton, _spotTrialExposureButton,
            Lbl("先打开相机、连接数控；相机高度/曝光不变，所有轴保持停止。C无反馈节点时仅手填。"),
            Lbl("演练只拍照，不发送激光指令；真实试打共用上方请求时长，未填不能出光。"));
        group.MaximumSize = new Size(1050, 0);
        return group;
    }

    private async Task<SpotTrialPosition> ReadSpotPosition()
    {
        if (!_cnc.IsConnected) throw new InvalidOperationException("请先连接数控，取得实际MCS XYZ；本流程不会驱动机床。");
        var s = await _cnc.GetStatusAsync();
        if (!HasValidCoordinates(s))
            throw new InvalidOperationException("未读取到有效的MCS XYZ：" + s.Message);
        var c = await ReadFixedCAsync();
        return new(s.PosX, s.PosY, s.PosZ, c.Value, c.Source, c.Node, DateTimeOffset.Now)
        { XyzSource = _cfg.DeviceMode == "Sim" ? "Simulator" : "OpcUaReadback" };
    }

    private async Task RunSingleSpotTrial(bool dryRun,bool propagateErrors=false,string? folderOverride=null,CancellationToken ct=default,
        LaserProcessPreset? presetOverride=null,Guid? productionRunId=null,int? holeId=null)
    {
        var rtu = _laser as ModbusRtuLaserService;
        int? milliseconds = null;
        var requestedPreset=presetOverride??_laserPreset;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _spotTrialStop = cancellation; _spotTrialDryRunActive = dryRun;
        try
        {
            if (_camera is not { IsOpen: true }) throw new InvalidOperationException("先连接相机并调好成像；正式采图会自动下发上方曝光和增益。");
            if (!dryRun)
            {
                milliseconds = requestedPreset.RequestedExposureMs;
                if (_cfg.DeviceMode == "Sim" || rtu is null || !rtu.IsOpen)
                    throw new InvalidOperationException("真实出光要求已连接现场激光器。");
                var currentLaser = await rtu.ReadRecordedProtocolAsync();
                if (!currentLaser.CanExpose || !requestedPreset.Matches(currentLaser))
                    throw new InvalidOperationException("当前参数与界面设置不符或未关光，请应用当前参数并核对读回后继续。");
            }
            await ReadSpotPosition();
            cancellation.Token.ThrowIfCancellationRequested();
            string? root = EnsureCaptureRoot(); if (root is null) {if(propagateErrors)throw new OperationCanceledException("未选择资料目录。");return;}
            cancellation.Token.ThrowIfCancellationRequested();
            string folder = folderOverride??Path.Combine(root, (dryRun ? "单点演练_" : "单点试打_") + DateTime.Now.ToString("yyyyMMdd_HHmmssfff"));
            Directory.CreateDirectory(folder);
            var logs = new ConcurrentQueue<string>();
            void Collect(string level, string line) => logs.Enqueue($"[{DateTimeOffset.Now:O}] [{level}] {line}");
            _log.LineAdded += Collect;
            var record = new SpotTrialRecord
            {
                DryRun = dryRun,
                DeviceMode = _cfg.DeviceMode,
                RequestedExposureMs = milliseconds,
                RequestedPreset = requestedPreset,
                OperatorNotes = _spotTrialNotes.Text,
                SoftwareOpticalSettings = _ringSettingsSnapshot?.Invoke(),
                RequestedCameraExposureUs = (double)_numExp.Value,
                RequestedCameraGain = (double)_numGain.Value
            };
            record.ProductionRunId=productionRunId;record.HoleId=holeId;
            try
            {
                _log.Info($"[单点采图] {(dryRun ? "无出光演练" : "真实单点试打")}；保存目录 {folder}；不移动任何轴。");
                async Task<SpotTrialImage> Capture(string phase, SpotTrialPosition position)
                {
                    var image = await CaptureFrameAsync();
                    string file = phase + ".bmp", path = Path.Combine(folder, file);
                    RingScanSolver.SaveFrame(path, image);
                    var result = new SpotTrialImage(file, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                        image.Width, image.Height, image.Timestamp, position);
                    await File.WriteAllTextAsync(Path.Combine(folder, phase + ".capture.json"), JsonSerializer.Serialize(new
                    {
                        Kind = "StationarySpotImage",
                        DryRun = dryRun,
                        Image = result
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    return result;
                }
                async Task Persist(SpotTrialRecord state)
                {
                    state.LogLines = logs.ToArray();
                    string tmp = Path.Combine(folder, "trial.json.tmp"), target = Path.Combine(folder, "trial.json");
                    await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(tmp, target, true);
                    await File.WriteAllLinesAsync(Path.Combine(folder, "trial.log"), state.LogLines);
                    RecordEvidenceTrial(folder,state);
                    if(productionRunId is not null)_reportProductionDetail?.Invoke($"孔{holeId}："+(state.Phase switch
                    {"BeforeImageSaved"=>"打前照片已保存", "ExposureRequested"=>$"正在执行连续{milliseconds/1000.0:0.###}秒请求（含设备通信）",
                     "Completed"=>"打后照片及关光确认已保存", "Failed"=>"本孔异常："+state.Error,"Canceled"=>"本孔已停止",_=>state.Phase}));
                }
                var workflow = new StationarySpotTrial(ReadSpotPosition, Capture, Persist,
                    dryRun ? null : rtu!.Calibration, m => _log.Info("[单点采图] " + m));
                await workflow.RunAsync(record, cancellation.Token);
                _laserCalibrationStatus.Text = dryRun ? "演练归档完成，未发送出光指令。" : "试打及关光读回完成，前后图已归档；下一次须重新读参数。";
                _log.Info($"[单点采图] 完成：{folder}；实际光学时长未测定。");
            }
            finally { _log.LineAdded -= Collect; }
        }
        catch (OperationCanceledException) { _laserCalibrationStatus.Text = "单点流程已取消，查看本次trial.json中的关光及归档状态。"; if(propagateErrors)throw; }
        catch (Exception ex) { _laserCalibrationStatus.Text = "单点流程未完成：" + ex.Message; _log.Error("[单点采图] " + ex); if(propagateErrors)throw; }
        finally { _spotTrialStop = null; _spotTrialDryRunActive = false; }
    }
}
