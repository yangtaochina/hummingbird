using System.Globalization;
using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private Func<RingScanSession?>? _readReusableCaptureBaseline;
    private TabPage BuildRingScanTab()
    {
        var tab = new TabPage("加工");
        _machiningPage=tab;
        var session = new RingScanSession { DirectionOptions = new() { BoundaryMode = DatumBoundaryMode.CircularFaceEdge } };
        _ringSettingsSnapshot = () => JsonSerializer.Deserialize<RingScanSettings>(JsonSerializer.Serialize(session.Settings))!;
        RingScanResult? fit = null; DialAlignmentPreview? alignment = null;
        RingExposureSelection? ringExposure = null;
        string? ringExposureSignature = null;
        var inspections = new Dictionary<string, DatumHoleInspection>(StringComparer.OrdinalIgnoreCase);
        var inspectionCache = new Dictionary<string, (string Signature, DatumHoleInspection Review)>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<HoleTarget>(); IReadOnlyList<CaptureWaypoint>? route = null;
        RingScanSettings? routeSettings = null;
        DatumDirectionOptions? routeDirectionOptions = null;
        var referenceTemplates = new Dictionary<int, RingScanFrame>();
        string? jobFolder = null; bool updating = false, monitoring = false;
        Action invalidateLaser = () => { };
        Action showDetails = () => { };
        Action showPhoto = () => { };
        Action showPreview = () => { };
        var photo = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        var liveHost = new GroupBox { Dock=DockStyle.Fill,Text="实时画面 · 自动定位与加工",Padding=new Padding(6) };
        var enlarge=Btn("放大实时画面",(_,_)=>ShowLiveMagnifier());enlarge.Dock=DockStyle.Top;
        liveHost.Controls.Add(_livePicture);liveHost.Controls.Add(_liveStatus);liveHost.Controls.Add(enlarge);
        var photoTab = new TabPage("已采照片／框选"); photoTab.Controls.Add(photo);
        var output = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        var jobLabel = new Label { AutoSize = true, MaximumSize = new Size(1100, 0), Text = "点击一键加工，自动检查并建立本次加工记录。" };
        var captureFeedback=new Label {Name="CaptureFeedback",AutoSize=true,MaximumSize=new Size(1100,0),ForeColor=Color.DarkOrange,Text="已保存0/6张；步骤编号不代表已采数量。"};
        var captureConfirmation=new CheckBox {AutoSize=true,Text="本次装夹、参考状态和固定C已核对",Checked=_fixedCConfirmed.Checked};
        captureConfirmation.CheckedChanged+=(_,_)=>_fixedCConfirmed.Checked=captureConfirmation.Checked;
        _fixedCConfirmed.CheckedChanged+=(_,_)=>{captureConfirmation.Checked=_fixedCConfirmed.Checked;captureFeedback.Text=_fixedCConfirmed.Checked?"装夹/C已确认，停稳后可拍照。":"拍照未就绪：请核对当前装夹、机床参考和面板C，勾选确认。";};
        var instruction = new Label { AutoSize = true, MaximumSize = new Size(1100, 0), Font = new Font(Font, FontStyle.Bold) };
        var parameters = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = session.Settings, ToolbarVisible = false };
        var plot = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var previewStatus = new TextBox { Name = "RingPreviewStatus", Dock = DockStyle.Top, Height = 105,
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Text = "18孔未生成：当前尚无检查结果。请打开完整六步记录，完成两个参考圆边的框选后检查。" };
        string? checkFailure = null;
        var stepChoice = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
        stepChoice.Items.AddRange(GuidedCapture.Steps.Select((s, i) => (object)$"步骤{i + 1}：{s.Name}").ToArray()); stepChoice.SelectedIndex = 0;
        var table = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        table.RowTemplate.Height = 23;
        foreach (string text in new[] { "步骤", "采集／检查状态", "MCS X", "MCS Y" })
        {int column=table.Columns.Add(text,text);table.Columns[column].SortMode=DataGridViewColumnSortMode.NotSortable;}
        table.Columns[0].FillWeight = 80; table.Columns[1].FillWeight = 190;
        for (int i = 0; i < 6; i++) table.Rows.Add(GuidedCapture.Steps[i].Name, "未采集", "", "");
        var status = new Dictionary<int, string>();
        var autoTimer = new System.Windows.Forms.Timer { Interval = 200 }; var gate = new CapturePositionGate();
        bool monitorTickBusy = false;
        var watchButton = Btn("启动到位自动拍照", (_, _) => { });
        var stopButton = Btn("停止自动拍照", (_, _) => { });
        CancellationTokenSource? exposureSearch = null;
        CancellationTokenSource? microStop = null;
        XyMicroMoveSession? microSession = null;
        int microPreparedIndex = -1;
        bool microRequested = false;
        DatumCircleSeed? productionSeed=null;
        DatumCircleSeed? productionFallbackSeed=null;
        var autoExposure = new CheckBox { AutoSize = true, Text = "自动选择曝光", Checked = _state.AutoExposureSelection };
        autoExposure.CheckedChanged += (_, _) => { _state.AutoExposureSelection = autoExposure.Checked; SaveOperatorState(); };

        RingScanFrame? Frame(int index) => session.Frames.FirstOrDefault(f => GuidedCapture.Index(f) == index);
        string ExposureSignature() => JsonSerializer.Serialize(new {session.Settings,
            Frames=session.Frames.Where(f=>f.Role==RingFrameRole.RingArc)});
        void StopMonitor()
        {
            monitoring = false; autoTimer.Stop(); gate.Reset(); watchButton.Text = "启动到位自动拍照";
        }
        void RememberRoute(RingScanSession source)
        {
            if(_state.OpticalChangedAt is {} changed && source.Frames.Any(f=>f.Capture?.HostCaptureTime is not {} time || new DateTimeOffset(time)<changed))
                throw new InvalidDataException("六步基准早于本次光学调整，请在新光学条件下重新建立采图基准。");
            // 先完整复核，成功后才替换已知可用路线；恢复失败记录不能污染原基准。
            var checkedBaseline=ReusableCaptureBaseline.Prepare(source,_template);
            route = GuidedCapture.Route(checkedBaseline.Session);
            routeSettings = JsonSerializer.Deserialize<RingScanSettings>(JsonSerializer.Serialize(checkedBaseline.Session.Settings));
            routeDirectionOptions = checkedBaseline.Session.DirectionOptions;
            referenceTemplates.Clear();
            foreach (var frame in checkedBaseline.Session.Frames) referenceTemplates[GuidedCapture.Index(frame)] = frame;
            if(checkedBaseline.RebuiltRegions)_log.Info("采图基准已用现有六图恢复参考框，并通过两参考及18孔预览；原记录未改写。");
        }
        _readReusableCaptureBaseline=()=>routeSettings is null?null:JsonSerializer.Deserialize<RingScanSession>(JsonSerializer.Serialize(
            new RingScanSession {Settings=routeSettings,DirectionOptions=routeDirectionOptions!,Frames=referenceTemplates.OrderBy(p=>p.Key).Select(p=>p.Value).ToList()}));
        void CheckRouteOptics()
        {
            if (routeSettings is null || routeSettings.ImageWidth != session.Settings.ImageWidth || routeSettings.ImageHeight != session.Settings.ImageHeight ||
                new[]{routeSettings.Jxx-session.Settings.Jxx,routeSettings.Jxy-session.Settings.Jxy,
                    routeSettings.Jyx-session.Settings.Jyx,routeSettings.Jyy-session.Settings.Jyy}.Any(v => Math.Abs(v) > 1e-6))
                throw new InvalidOperationException("当前光学参数与采图路线来源不同，请重新采集路线。");
        }
        _stopCaptureMonitor = () => { exposureSearch?.Cancel(); microStop?.Cancel(); _cancelProductionDemo?.Invoke(); _evidenceStop?.Cancel(); _productionStop?.Cancel(); _stationReturnStop?.Cancel(); _automaticOpticsStop?.Cancel(); StopMonitor(); };
        stopButton.Click += (_, _) => { exposureSearch?.Cancel(); microStop?.Cancel(); _cancelProductionDemo?.Invoke(); _evidenceStop?.Cancel(); _productionStop?.Cancel(); _stationReturnStop?.Cancel(); _automaticOpticsStop?.Cancel(); StopMonitor(); };
        void InvalidateResult()
        {
            if(ringExposureSignature!=ExposureSignature()){ringExposure=null;ringExposureSignature=null;}
            fit = null; alignment = null; targets.Clear(); invalidateLaser(); plot.Invalidate();
            checkFailure = null;
            previewStatus.ForeColor = Color.DarkOrange;
            previewStatus.Text = "18孔未生成：记录已改变，需要重新检查。";
        }
        void RefreshRows()
        {
            bool previousUpdating=updating;updating = true;
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    var frame = Frame(i); table.Rows[i].Cells[1].Value = status.GetValueOrDefault(i, frame is null ? "未采集" : "已采集，待检查");
                    table.Rows[i].Cells[2].Value = frame?.X.ToString("F4", CultureInfo.InvariantCulture) ?? "";
                    table.Rows[i].Cells[3].Value = frame?.Y.ToString("F4", CultureInfo.InvariantCulture) ?? "";
                }
            }
            finally { updating = previousUpdating; }
            instruction.Text = GuidedCapture.Steps[stepChoice.SelectedIndex].Instruction + "  （C/Z保持不动，软件读取XYZ并自动存档）";
            jobLabel.Text = jobFolder is null ? "放好全新待加工件，点击一键加工。" : $"本件：{Path.GetFileName(jobFolder)} · 采图{session.Frames.Count}/6张";
        }
        void SaveSession()
        {
            if (jobFolder is null) return;
            string path = Path.Combine(jobFolder, RingCaptureArchive.AutosaveName); session.Save(path);
            _state.LastSessionPath = path; SaveOperatorState();
            EvidenceAction(()=>_fieldEvidence!.CaptureSession(session));RecordEvidenceArtifacts(jobFolder);
        }
        void SaveReport()
        {
            if (jobFolder is null) return;
            File.WriteAllText(Path.Combine(jobFolder, "定位检查与预览.json"), JsonSerializer.Serialize(new
            {
                Kind = "GuidedCaptureInspection",
                GeneratedAt = DateTimeOffset.Now,
                PreviewOnly = true,
                AlignmentCompleted = alignment is not null && targets.Count == 18,
                TargetCount = targets.Count,
                TargetStability = alignment?.TargetStability,
                Rejection = checkFailure,
                Summary = previewStatus.Text,
                Settings = session.Settings,
                DirectionOptions = session.DirectionOptions,
                SourceFrames = session.Frames,
                Ring = fit,
                RingExposure = ringExposure,
                ReferenceInspections = inspections.Values.ToArray(),
                Result = alignment
            }, new JsonSerializerOptions { WriteIndented = true }));
            EvidenceAction(()=>_fieldEvidence!.AddFile(Path.Combine(jobFolder,"定位检查与预览.json"),"定位/定位检查与预览.json","定位检查及18孔结果"));
        }
        async Task Run(Func<Task> action)
        {
            if (_workspaceBusy || _laserReadBusy || _laserExposureTask is { IsCompleted: false }) {captureFeedback.ForeColor=Color.DarkOrange;captureFeedback.Text="正在执行其他操作，请等待完成或先停止。";return;}
            var restore = new List<(Control Control, bool Enabled)>();
            void LockActions(Control parent)
            {
                foreach (Control child in parent.Controls)
                {
                    if(child.Name=="GlobalProductionStop")continue;
                    if (child == stopButton || child == _laserStopButton || child == _productionStopButton || child == _stationReturnStopButton || child == _automaticOpticsStopButton) continue;
                    if(child.Name is "ProductionStatus" or "ProductionHoles" or "StationReturnStatus" or "AutomaticOpticsStatus")continue;
                    if (child is Button or TextBox or NumericUpDown or ComboBox or CheckBox or PropertyGrid or DataGridView)
                    { restore.Add((child, child.Enabled)); child.Enabled = false; }
                    else LockActions(child);
                }
            }
            LockActions(this); _workspaceBusy = true; _cameraChange?.Cancel(); UpdateLaserCalibrationButtons();
            _liveStatus.ForeColor=Color.DarkOrange;_liveStatus.Text="正在采图／检查，实时预览暂停；请保持机床位置不动。";
            captureFeedback.ForeColor=Color.DarkOrange;captureFeedback.Text=_stationReturnUiAction?"正在处理工位恢复操作，请勿手动操作轴……":"正在处理，请保持位置不动……";
            try { _workspaceTask = action(); await _workspaceTask; }
            catch (Exception ex) { StopMonitor(); output.Text = ex.Message;captureFeedback.ForeColor=Color.DarkRed;captureFeedback.Text="操作未完成："+ex.Message;if(_stationReturnUiAction)_selectStationReturnPanel?.Invoke();else if(_productionUiAction)_selectProductionPanel?.Invoke();else if(_opticalUiAction)SelectPage(_opticsPage);else showDetails(); _log.Error("[定位采集] " + ex.Message); if(_fieldEvidence is not null){AddEvidenceIssue("Acquisition",ex);RefreshEvidencePackage();} }
            finally
            {
                _workspaceTask = null; _workspaceBusy = false;
                foreach (var item in restore) if (!item.Control.IsDisposed) item.Control.Enabled = item.Enabled;
                UpdateLaserCalibrationButtons(); if (!IsDisposed) RefreshRows();
                if(captureFeedback.Text=="正在处理，请保持位置不动……") {captureFeedback.ForeColor=Color.DarkGreen;captureFeedback.Text=$"操作结束；已保存{session.Frames.Count}/6张。";}
                else if(captureFeedback.Text=="正在处理工位恢复操作，请勿手动操作轴……") {captureFeedback.ForeColor=Color.DarkGreen;captureFeedback.Text="工位恢复操作结束，结果见工位恢复页。";}
            }
        }
        void ShowSelected()
        {
            int index = stepChoice.SelectedIndex; var frame = Frame(index); if (frame is null) return;
            try
            {
                var image = RingScanSolver.LoadFrame(frame.ImagePath); ShowFrame(photo, image, null);
                using var graphics = Graphics.FromImage(photo.Image!);
                if (inspections.TryGetValue(frame.ImagePath, out var inspection) && inspection.Candidates.Count > 0)
                    foreach (var hole in inspection.Candidates) DrawReference(graphics, hole, Color.Yellow);
                else if (frame.FaceSeed is { } seed)
                {
                    using var pen = new Pen(Color.Orange, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                    graphics.DrawEllipse(pen, (float)(seed.Px - seed.RadiusPx), (float)(seed.Py - seed.RadiusPx), (float)seed.RadiusPx * 2, (float)seed.RadiusPx * 2);
                }
                photo.Invalidate();
            }
            catch (Exception ex) { output.Text = ex.Message; }
        }
        void SelectStep(int index, bool savedPhoto = false, bool synchronizeGrid = true)
        {
            if(index<0 || index>=GuidedCapture.Steps.Count)throw new ArgumentOutOfRangeException(nameof(index));
            bool previousUpdating=updating;updating=true;
            // CurrentCellChanged来自原生单元格切换，回调期间不能再写CurrentCell。
            try{stepChoice.SelectedIndex=index;if(synchronizeGrid && table.CurrentRow?.Index!=index)table.CurrentCell=table.Rows[index].Cells[0];}
            finally{updating=previousUpdating;}
            RefreshRows();
            ShowSelected(); if (savedPhoto && _productionStop is null && Frame(index) is not null) showPhoto();
        }
        _selectCaptureStep=index=>SelectStep(index);
        void SuggestNext()
        {
            int next = Enumerable.Range(0, 6).FirstOrDefault(i => Frame(i) is null, -1);
            if (next < 0) next = new[] { 0, 5 }.FirstOrDefault(i => Frame(i) is { } f &&
                (!inspections.TryGetValue(f.ImagePath, out var value) || value.Status != DatumHoleInspectionStatus.Detected), -1);
            if (next >= 0)
            {
                SelectStep(next, Frame(next) is not null);
            }
        }
        async Task CheckAsync(bool suggest = true)
        {
            InvalidateResult(); inspections.Clear(); status.Clear(); SaveReport();
            previewStatus.Text = "正在检查六步记录和参考圆边……";
            try
            {
            foreach (var frame in session.Frames.Where(f => f.Role != RingFrameRole.RingArc))
            {
                RingCaptureArchive.VerifyImage(frame);
                string signature = JsonSerializer.Serialize(new
                {
                    session.Settings,
                    session.DirectionOptions,
                    frame.FaceSeed,
                    frame.Capture,
                    ImageHash = LaserSpotCalibration.Hash(frame.ImagePath)
                });
                var review = inspectionCache.TryGetValue(frame.ImagePath, out var cached) && cached.Signature == signature
                    ? cached.Review : await Task.Run(() => new DatumDirectionSolver().Inspect(frame, session.Settings, session.DirectionOptions));
                inspectionCache[frame.ImagePath] = (signature, review);
                inspections[frame.ImagePath] = review; int index = GuidedCapture.Index(frame);
                status[index] = review.Status == DatumHoleInspectionStatus.Detected ? "实体圆边通过预览筛选" : frame.FaceSeed is null ? "请框选实体小圆面" : "实体圆边未通过，需调整图像／范围";
            }
            var arcs = session.Frames.Where(f => f.Role == RingFrameRole.RingArc).ToList();
            if(arcs.Count!=4 || !autoExposure.Checked){ringExposure=null;ringExposureSignature=null;}
            if (arcs.Count == 4)
            {
                try
                {
                    foreach(var arc in arcs)RingCaptureArchive.VerifyImage(arc);
                    if(autoExposure.Checked)
                    {
                        if(ringExposure is null || ringExposureSignature!=ExposureSignature())
                        {
                            captureFeedback.Text="正在比较已存曝光候选的圆环几何，无需移动或重新拍照……";
                            using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_productionStop?.Token??CancellationToken.None);
                            exposureSearch=cancellation;
                            try{ringExposure=await Task.Run(()=>new RingScanSolver().SelectExposure(session,cancellation.Token));}
                            finally{exposureSearch=null;}
                            if(ringExposure.Fit is not null && ringExposure.Changed)
                            {
                                foreach(var chosen in ringExposure.SelectedFrames)
                                {
                                    session.Frames.RemoveAll(f=>GuidedCapture.Index(f)==GuidedCapture.Index(chosen));
                                    session.Frames.Add(chosen);
                                    if(chosen.Capture?.RequestedExposureUs is { } exposure)_state.StepExposureUs[chosen.Name]=exposure;
                                }
                                session.Frames=session.Frames.OrderBy(GuidedCapture.Index).ToList();
                                SaveOperatorState();
                            }
                        }
                        fit=ringExposure.Fit??throw new InvalidDataException(ringExposure.Message+"\r\n"+
                            string.Join("\r\n",ringExposure.CandidateIssues.Take(3)));
                    }
                    else fit = await Task.Run(() => new RingScanSolver().Analyze(new RingScanSession { Settings = session.Settings, Frames = arcs }));
                    if (fit.Initialization is { } seed) { session.Settings.HintX = seed.HintX; session.Settings.HintY = seed.HintY; parameters.Refresh(); }
                    ringExposureSignature=ExposureSignature();
                    foreach (var f in session.Frames.Where(f=>f.Role==RingFrameRole.RingArc))
                        status[GuidedCapture.Index(f)] = autoExposure.Checked?$"圆边通过 · {f.Capture?.RequestedExposureUs:F0}µs":"圆边已参与拟合";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { output.Text = "圆环检查未通过：" + ex.Message;captureFeedback.ForeColor=Color.DarkRed;
                    checkFailure = output.Text;
                    previewStatus.ForeColor=Color.DarkRed;previewStatus.Text="18孔未生成："+checkFailure;
                    captureFeedback.Text=$"已保存{session.Frames.Count}/6张；18孔未生成，圆环检查未通过。";
                    SaveReport();RefreshRows(); if (suggest && _productionStop is null) SuggestNext(); if(_productionStop is null)showDetails(); return; }
            }
            if (session.Frames.Count == 6 && fit is not null && new[] { 0, 5 }.All(i => Frame(i) is { } f &&
                inspections.TryGetValue(f.ImagePath, out var check) && check.Status == DatumHoleInspectionStatus.Detected))
            {
                alignment = await Task.Run(() => new DialAlignmentSolver().Analyze(session, _template)); fit = alignment.Ring; targets = alignment.Targets.ToList();
                _refreshLaserCompensation?.Invoke();
                output.Text = $"圆心 X={fit.CenterX:F4} Y={fit.CenterY:F4}；12点={alignment.Direction.TwelveAngleDeg:F4}°；18孔预览已生成。\r\n"
                    + string.Join("\r\n", alignment.Warnings);
            }
            else
            {
                var pending = Enumerable.Range(0,6).Where(i => Frame(i) is null)
                    .Select(i => GuidedCapture.Steps[i].Name + "未采集").ToList();
                pending.AddRange(new[] {0,5}.Where(i => Frame(i) is { } f &&
                    (!inspections.TryGetValue(f.ImagePath,out var review) || review.Status != DatumHoleInspectionStatus.Detected))
                    .Select(i => GuidedCapture.Steps[i].Name + (Frame(i)!.FaceSeed is null ? "尚未框选实体圆边" : "实体圆边未通过识别")));
                checkFailure = string.Join("；", pending);
                if (string.IsNullOrEmpty(checkFailure)) checkFailure = "尚未完成圆环与参考方向检查";
                output.Text = "18孔未生成：" + checkFailure + "。\r\n照片已保存，不需要因此重新拍六处。\r\n"
                    + string.Join("\r\n", inspections.Values.Select(v => v.Frame.Name + "：" + v.Message + "\r\n"
                        + string.Join("\r\n",v.Diagnostics)))
                    + (fit is null ? "" : "\r\n四方位圆环已拟合；等待参考圆边完成后生成方向和18孔。");
            }
            if(ringExposure is not null) output.AppendText("\r\n"+ringExposure.Message);
            bool complete = alignment is not null && targets.Count == 18;
            previewStatus.ForeColor = captureFeedback.ForeColor = complete ? Color.DarkGreen : Color.DarkOrange;
            previewStatus.Text = complete ? $"18孔预览已生成（18/18）。\r\n圆环RMS={fit!.RmsMm*1000:F1}µm；12点方向={alignment!.Direction.TwelveAngleDeg:F4}°。\r\n仅供定位预览，加工精度尚未验收。"
                : "18孔未生成：" + checkFailure + "。\r\n" + (fit is null ? "" : $"圆环已通过，RMS={fit.RmsMm*1000:F1}µm。\r\n")
                    + "请选择未通过的参考照片，点“放大框选实体圆边”；具体原因见“检查与归档”。";
            captureFeedback.Text = $"已保存{session.Frames.Count}/6张；" + (complete ? "18孔预览已生成。" : "18孔未生成："+checkFailure+"。");
            SaveSession(); SaveReport(); RefreshRows(); ShowSelected(); plot.Invalidate(); if (suggest && _productionStop is null) SuggestNext();
            if(_productionStop is null){if (complete) showPreview(); else showDetails();}
            }
            catch (Exception ex)
            {
                // 方向计算等后续失败也要归档，不能只留下已通过的圆环结果。
                alignment = null; targets.Clear(); invalidateLaser();
                checkFailure = ex.Message;
                previewStatus.ForeColor = Color.DarkRed; previewStatus.Text = "18孔未生成："+ex.Message;
                SaveReport(); plot.Invalidate(); throw;
            }
        }
        async Task LoadSession(string path)
        {
            await Run(async () =>
            {
                var loaded = RingScanSession.Load(path); StopMonitor();
                if (loaded.Frames.Any(f => GuidedCapture.Index(f) < 0) || loaded.Frames.GroupBy(GuidedCapture.Index).Any(g => g.Count() > 1))
                    throw new InvalidDataException("这份记录无法对应六步固定C采集；请使用含12/3/6/9圆环和11/1参考图的记录。");
                loaded.DirectionOptions = loaded.DirectionOptions with { BoundaryMode = DatumBoundaryMode.CircularFaceEdge };
                session = loaded; jobFolder = Path.GetDirectoryName(Path.GetFullPath(path)); status.Clear(); InvalidateResult(); parameters.SelectedObject = session.Settings;
                _restoreProductionJobView?.Invoke(jobFolder);
                _state.LastSessionPath = Path.GetFullPath(path); SaveOperatorState(); _fixedCConfirmed.Checked = false;
                if (session.Frames.Count == 6)
                {
                    try { RememberRoute(session); _state.LastRouteSessionPath = Path.GetFullPath(path); SaveOperatorState(); }
                    catch (InvalidDataException ex) { _log.Warn("所打开记录未替换已核对采图基准："+ex.Message); }
                }
                await CheckAsync();
                output.Text = "已恢复历史记录；继续采图前核对当前装夹、参考状态和固定C。\r\n" + output.Text;
            });
        }
        _loadWorkspace = LoadSession;
        async Task<MachineStatus> ReadMachine()
        {
            if (!_cnc.IsConnected) throw new InvalidOperationException("请连接数控；采图必须读取实际XYZ。");
            var s = await _cnc.GetStatusAsync();
            if (!HasValidCoordinates(s))
                throw new InvalidOperationException("未读到有效MCS XYZ：" + s.Message);
            return s;
        }
        void Stable(MachineStatus before, MachineStatus after, double c1, double c2)
        {
            if (Math.Abs(before.PosX - after.PosX) > .002 || Math.Abs(before.PosY - after.PosY) > .002 || Math.Abs(before.PosZ - after.PosZ) > .002 ||
                Math.Abs(Math.IEEERemainder(c1 - c2, 360)) > .001) throw new InvalidOperationException("采图期间位置变化，未登记；请停稳后重试。");
        }
        async Task CaptureAsync()=>_ = await CaptureStepAsync(stepChoice.SelectedIndex);
        async Task<RingScanFrame> CaptureStepAsync(int index,CancellationToken captureToken=default)
        {
            if(index<0 || index>=GuidedCapture.Steps.Count)throw new ArgumentOutOfRangeException(nameof(index));
            captureToken.ThrowIfCancellationRequested();
            if (!_fixedCConfirmed.Checked && !_guidedOperationConfirmed) throw new InvalidOperationException("请核对本次装夹、机床参考和当前面板C，勾选拍照按钮旁的确认框（与环境页同步）。");
            if (_cfg.DeviceMode == "Real" && session.Frames.Any(f => f.Capture?.XyzSource == AxisCoordinateSource.Simulator))
                throw new InvalidOperationException("不能在模拟记录中追加真机照片；请新建工件。");
            if (_camera is not { IsOpen: true }) throw new InvalidOperationException("请先连接相机。");
            if (jobFolder is null)
            {
                string? root = EnsureCaptureRoot(); if (root is null) throw new OperationCanceledException("未选择资料根目录，未采图。");
                jobFolder = Path.Combine(root, "工件_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff")); Directory.CreateDirectory(jobFolder);
            }
            var step = GuidedCapture.Steps[index]; var previous = Frame(index);
            if (microRequested)
            {
                CheckRouteOptics();
                previous = referenceTemplates.GetValueOrDefault(index) ?? previous;
                if (previous is null || microSession is null || microPreparedIndex != index)
                    throw new InvalidOperationException("先打开同一步的基准照片，并导出当前点微调NC。");
            }
            if (previous is null && routeSettings is not null)
            {
                try { CheckRouteOptics(); previous = referenceTemplates.GetValueOrDefault(index); } catch (InvalidOperationException) { }
            }
            var before = await ReadMachine(); var c1 = await ReadFixedCAsync(); await Task.Delay(300,captureToken);
            Stable(before, await ReadMachine(), c1.Value, (await ReadFixedCAsync()).Value);
            if (session.Frames.FirstOrDefault() is { } reference && (Math.Abs(before.PosZ - reference.Z) > .010 ||
                Math.Abs(Math.IEEERemainder(c1.Value - reference.C, 360)) > .005)) throw new InvalidOperationException("当前Z/C与这组记录不同，请核对装夹与光学条件后新建工件。");
            CameraFrame image;
            if (autoExposure.Checked)
            {
                double gain = (double)_numGain.Value;
                double basis = _state.StepExposureUs.GetValueOrDefault(step.Name, (double)_numExp.Value);
                string trialFolder = Path.Combine(jobFolder, "曝光选优_" + step.Name + "_" + DateTime.Now.ToString("HHmmssfff"));
                Directory.CreateDirectory(trialFolder);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(captureToken); exposureSearch = cancellation;
                try
                {
                    var selected = await _cameraCapture!.SelectExposureAsync(basis, gain, async (candidate, exposure) =>
                    {
                        Stable(before, await ReadMachine(), c1.Value, (await ReadFixedCAsync()).Value);
                        string trialPath = Path.Combine(trialFolder, exposure.ToString("G17", CultureInfo.InvariantCulture) + "us.bmp");
                        var review = await Task.Run(() =>
                        {
                            if (candidate.Width != session.Settings.ImageWidth || candidate.Height != session.Settings.ImageHeight)
                                throw new InvalidDataException("曝光试拍图像尺寸与标定不一致。");
                            RingScanSolver.SaveFrame(trialPath, candidate);
                            var trial = RingCaptureArchive.BindImage(new RingScanFrame(step.Name, trialPath, before.PosX, before.PosY, before.PosZ, c1.Value)
                                { Role = step.Role }, session.Settings, candidate.Timestamp, candidate.Width, candidate.Height,
                                _cfg.DeviceMode == "Sim" ? AxisCoordinateSource.Simulator : AxisCoordinateSource.OpcUaReadback,
                                c1.Source, c1.Node, "ExposureSelection");
                            var seed = productionSeed is not null ? productionSeed with {ImageSha256=trial.Capture!.ImageSha256} :
                                previous is null || step.Role == RingFrameRole.RingArc ? null :
                                GuidedCapture.TranslateRegion(previous, session.Settings, trial.X, trial.Y, trial.Capture!.ImageSha256);
                            trial = trial with { FaceSeed = seed, Capture = trial.Capture! with {
                                RequestedExposureUs = exposure, RequestedGain = gain, DeviceMode = _cfg.DeviceMode,
                                CameraParameterSource = "AutoExposureCandidateSentBeforeCapture" } };
                            DatumHoleInspection? referenceCheck = null;
                            if(productionSeed is not null && step.Role!=RingFrameRole.RingArc)
                            {
                                var tracking=ReferenceRegionTracking.Inspect(trial,session.Settings,session.DirectionOptions,cancellation.Token,productionFallbackSeed,reacquireInView:true);
                                trial=tracking.Frame;referenceCheck=tracking.Inspection;
                                File.WriteAllText(Path.ChangeExtension(trialPath,"region.json"),JsonSerializer.Serialize(tracking));
                            }
                            RingCaptureArchive.SaveSidecar(trial, session.Settings, session.DirectionOptions);
                            var quality = ExposureSelectionPolicy.Measure(candidate, trial.FaceSeed);
                            if (seed is not null)
                            {
                                var inspection = referenceCheck??new DatumDirectionSolver().Inspect(trial, session.Settings, session.DirectionOptions);
                                quality = quality with { FeatureDetected = inspection.Status == DatumHoleInspectionStatus.Detected, Detail = inspection.Message };
                            }
                            return quality;
                        });
                        return review;
                    }, cancellation.Token, restoreExposureUs: (double)_numExp.Value);
                    Stable(before, await ReadMachine(), c1.Value, (await ReadFixedCAsync()).Value);
                    image = selected.Frame;
                    _settingSelectedExposure = true;
                    try { _numExp.Value = (decimal)selected.ExposureUs; }
                    finally { _settingSelectedExposure = false; }
                    _state.CameraExposureUs = selected.ExposureUs;
                    _state.StepExposureUs[step.Name] = selected.ExposureUs; SaveOperatorState();
                    _cameraStatus.Text = $"{step.Name} 自动曝光：{selected.ExposureUs:F0}µs（{selected.Trials.Count}档）";
                    File.WriteAllText(Path.Combine(trialFolder, "选优结果.json"), JsonSerializer.Serialize(new {
                        SelectedExposureUs = selected.ExposureUs, Gain = gain, selected.Trials,
                        Purpose = step.Role==RingFrameRole.RingArc?"ProvisionalImageQuality_FourViewGeometryReselectsWhenComplete":"ReferenceBoundarySelection_FinalPositioningChecksStillRequired"
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally { exposureSearch = null; }
            }
            else image = await CaptureFrameAsync();
            Stable(before, await ReadMachine(), c1.Value, (await ReadFixedCAsync()).Value);
            XyMicroAdjustment? microResidual = null;
            if (microRequested)
            {
                RingCaptureArchive.VerifyImage(previous!);
                var referenceImage = RingScanSolver.LoadFrame(previous!.ImagePath);
                string adjustmentFolder = Path.Combine(jobFolder, "XY微调_" + step.Name + "_" + DateTime.Now.ToString("HHmmssfff"));
                Directory.CreateDirectory(adjustmentFolder);
                for (int attempt = 0; attempt <= 2; attempt++)
                {
                    microStop!.Token.ThrowIfCancellationRequested();
                    string attemptPath = Path.Combine(adjustmentFolder, $"image_{attempt}.bmp");
                    RingScanSolver.SaveFrame(attemptPath, image);
                    var evidence = RingCaptureArchive.BindImage(new RingScanFrame(step.Name,attemptPath,before.PosX,before.PosY,before.PosZ,c1.Value) {Role=step.Role},
                        session.Settings,image.Timestamp,image.Width,image.Height,AxisCoordinateSource.OpcUaReadback,c1.Source,c1.Node,"XyMicroAdjustment");
                    evidence = evidence with { Capture=evidence.Capture! with { RequestedExposureUs=(double)_numExp.Value,RequestedGain=(double)_numGain.Value,DeviceMode=_cfg.DeviceMode } };
                    RingCaptureArchive.SaveSidecar(evidence,session.Settings,session.DirectionOptions);
                    microResidual = await Task.Run(() => XyImageRegistration.Measure(referenceImage,image,session.Settings,previous.FaceSeed));
                    File.WriteAllText(Path.Combine(adjustmentFolder,$"correction_{attempt}.json"),JsonSerializer.Serialize(microResidual));
                    if (double.Hypot(microResidual.PixelDx,microResidual.PixelDy)<=2) break;
                    if (attempt==2) throw new InvalidOperationException("两次微调后图像仍未对齐，已停止本点；请查看归档，不继续盲目找位。");
                    Stable(before,await ReadMachine(),c1.Value,(await ReadFixedCAsync()).Value);
                    await microSession!.MoveAsync(before.PosX+microResidual.DeltaX,before.PosY+microResidual.DeltaY,microStop.Token);
                    var afterMove = await ReadMachine(); var cAfterMove = await ReadFixedCAsync();
                    if(Math.Abs(afterMove.PosZ-before.PosZ)>.010 || Math.Abs(Math.IEEERemainder(cAfterMove.Value-c1.Value,360))>.005)
                        throw new InvalidOperationException("微调期间Z/C变化，未继续采图。");
                    before=afterMove; c1=cAfterMove;
                    image=await CaptureFrameAsync();
                    Stable(before,await ReadMachine(),c1.Value,(await ReadFixedCAsync()).Value);
                }
            }
            string path = Path.Combine(jobFolder, step.Name + "_" + DateTime.Now.ToString("HHmmssfff") + ".bmp");
            RingScanSolver.SaveFrame(path, image);
            var frame = new RingScanFrame(step.Name, path, before.PosX, before.PosY, before.PosZ, c1.Value) { Role = step.Role };
            frame = RingCaptureArchive.BindImage(frame, session.Settings, image.Timestamp, image.Width, image.Height,
                _cfg.DeviceMode == "Sim" ? AxisCoordinateSource.Simulator : AxisCoordinateSource.OpcUaReadback, c1.Source, c1.Node, "FixedCScan");
            frame = frame with
            {
                Capture = frame.Capture! with
                {
                    RequestedExposureUs = (double)_numExp.Value,
                    RequestedGain = (double)_numGain.Value,
                    CameraParameterSource = autoExposure.Checked ? "AutoExposureSelectedEvaluatedFrame" : "UiParametersSentBeforeCapture",
                    DeviceMode = _cfg.DeviceMode
                }
            };
            if(productionSeed is not null)
            {
                frame=frame with {FaceSeed=productionSeed with {ImageSha256=frame.Capture!.ImageSha256}};
                var tracking=await Task.Run(()=>ReferenceRegionTracking.Inspect(frame,session.Settings,session.DirectionOptions,captureToken,productionFallbackSeed,reacquireInView:true),captureToken);
                frame=tracking.Frame;
                File.WriteAllText(Path.ChangeExtension(path,"region.json"),JsonSerializer.Serialize(tracking));
            }
            else if (microResidual is not null && previous?.FaceSeed is { } matchedSeed)
                frame=frame with { FaceSeed=matchedSeed with { Px=matchedSeed.Px+microResidual.PixelDx,Py=matchedSeed.Py+microResidual.PixelDy,
                    ImageSha256=frame.Capture!.ImageSha256,SelectionSource="MatchedBaselineRegion_MustBeRefitted" } };
            else if (previous is not null && step.Role != RingFrameRole.RingArc)
                frame = frame with { FaceSeed = GuidedCapture.TranslateRegion(previous, session.Settings, frame.X, frame.Y, frame.Capture!.ImageSha256) };
            session.Frames.RemoveAll(f => GuidedCapture.Index(f) == index); session.Frames.Add(frame);
            session.Frames = session.Frames.OrderBy(GuidedCapture.Index).ToList(); status[index] = "已自动归档";
            new RingScanSession { Settings = session.Settings, DirectionOptions = session.DirectionOptions, Frames = [frame] }.Save(RingCaptureArchive.SidecarPath(path));
            SaveSession();captureFeedback.ForeColor=Color.DarkGreen;captureFeedback.Text=$"{step.Name}照片已保存，正在检查；当前已保存{session.Frames.Count}/6张。"; await CheckAsync();
            captureFeedback.Text=$"{step.Name}照片已保存；当前已保存{session.Frames.Count}/6张。检查结果见右侧。";
            _log.Info($"[定位采集] {step.Name}已保存：{path}");
            if (step.Role != RingFrameRole.RingArc && (!inspections.TryGetValue(path, out var q) || q.Status != DatumHoleInspectionStatus.Detected))
            { StopMonitor(); SelectStep(index, true);captureFeedback.ForeColor=Color.DarkOrange;captureFeedback.Text=$"{step.Name}照片已保存，但参考边界未通过／需要框选；不是未采集。"; output.AppendText("\r\n此参考图需要框选／检查，自动采图已暂停。"); }
            RefreshEvidencePackage();
            var archived=Frame(index)??throw new InvalidDataException($"步骤{index+1}（{step.Name}）未在当前记录中找到；照片目录：{jobFolder}");
            if(GuidedCapture.Index(archived)!=index)throw new InvalidDataException("采图归档用途与指定步骤不一致，已停止。");
            return archived;
        }
        _captureGuidedStep=CaptureStepAsync;
        _runWorkspaceAction=Run;

        var project = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        Task CreateWorkpiece(bool required=false)
        {
            string? root = EnsureCaptureRoot(); if (root is null){if(required)throw new OperationCanceledException("未选择资料目录，已取消开始。");return Task.CompletedTask;} StopMonitor();
            if (jobFolder is not null) SaveSession();
            session = new RingScanSession { Settings = session.Settings, DirectionOptions = session.DirectionOptions };
            jobFolder = Path.Combine(root, "工件_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff")+"_"+Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(jobFolder);
            _restoreProductionJobView?.Invoke(jobFolder);
            _fixedCConfirmed.Checked = false; status.Clear(); inspections.Clear(); InvalidateResult(); parameters.SelectedObject = session.Settings;
            var old = photo.Image; photo.Image = null; old?.Dispose(); SelectStep(0); SaveSession(); output.Text = "新工件已建立，保留光学参数。核对固定C和装夹后从1点开始。";
            return Task.CompletedTask;
        }
        _createOneClickWorkpiece=()=>CreateWorkpiece(true);
        _inspectOneClickWorkpiece=()=>
        {
            if(jobFolder is null)return(true,"将自动建立全新未加工件记录。请放好本次新工件。");
            string recordPath=Path.Combine(jobFolder,ProductionJobArchive.MachiningRecordName);
            if(File.Exists(recordPath))
            {
                var prior=JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(recordPath))??throw new InvalidDataException("本件记录损坏，请先查看本件结果。");
                if(_activeReworkPlan is null && prior is {Completed:true,LaserOffConfirmed:true,NcExitConfirmed:true})
                    return(true,"上一件已完成。只有已更换为全新未加工工件才继续；程序将建立下一件记录，保留上一件资料。同一实物追加请进入同件补加工。");
                throw new InvalidOperationException("当前轮次已加工或状态待核对，不能一键重跑。请先查看本件结果；同件追加或未出光重试进入“同件补加工”。");
            }
            if(session.Frames.Count>0)throw new InvalidOperationException("当前记录已有照片。请在“工件与资料”查看该记录；确认是全新未加工件后新建工件，补加工使用独立补加工入口。");
            return(false,_activeReworkPlan?.Info is {} info?$"已准备同件补加工第{info.Round}轮，沿用关联记录，重新定位后追加加工。":"使用当前尚未加工的空工件记录，自动保存本次全部资料。");
        };
        project.Controls.Add(Btn("新工件", async (_, _) => await Run(()=>CreateWorkpiece())));
        project.Controls.Add(Btn("打开采集记录", async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "采集记录|*.json" }; if (dialog.ShowDialog(this) == DialogResult.OK) await LoadSession(dialog.FileName);
        }));
        project.Controls.Add(Btn("资料根目录", (_, _) => { EnsureCaptureRoot(true); RefreshRows(); }));
        project.Controls.Add(Btn("导出整组记录", async (_, _) => await Run(() =>
        {
            using var dialog = new SaveFileDialog { Filter = "采集记录|*.json", FileName = "ring_scan.json", InitialDirectory = jobFolder ?? "" };
            if (dialog.ShowDialog(this) == DialogResult.OK) session.Save(dialog.FileName); return Task.CompletedTask;
        })));
        var acquisition = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        acquisition.Controls.AddRange([Lbl("当前步骤"), stepChoice]);
        acquisition.Controls.Add(captureConfirmation);
        acquisition.Controls.Add(autoExposure);
        acquisition.Controls.Add(Btn("停稳拍照（自动存档）", async (_, _) => await Run(CaptureAsync)));
        acquisition.Controls.Add(Btn("微调XY并采图",async (_,_)=>await Run(async()=>
        {
            StopMonitor();
            using var cancellation=new CancellationTokenSource();microStop=cancellation;microRequested=true;
            try { await CaptureAsync(); }
            finally
            {
                microRequested=false;microStop=null;
                if(microSession is not null)
                {
                    try { await microSession.FinishAsync(); } catch(Exception ex) { _log.Warn("微调NC退出未确认，请在面板停止："+ex.Message); }
                    microSession=null;microPreparedIndex=-1;
                }
            }
        })));
        acquisition.Controls.Add(Btn("检查并预览18孔", async (_, _) => await Run(() => CheckAsync())));
        acquisition.Controls.Add(watchButton); acquisition.Controls.Add(stopButton);
        var box = Btn("放大框选实体圆边", async (_, _) => await Run(async () =>
        {
            var frame = Frame(stepChoice.SelectedIndex);
            if (frame is null || frame.Role == RingFrameRole.RingArc) { output.Text = "请选择已采的1点或11点照片。"; return; }
            StopMonitor(); RingCaptureArchive.VerifyImage(frame);
            RingScanFrame WithRegion(RectangleF region) => frame with
            {
                FaceSeed = new(region.X + region.Width / 2, region.Y + region.Height / 2,
                    (region.Width + region.Height) / 4, LaserSpotCalibration.Hash(frame.ImagePath), "OperatorImageRectangle")
            };
            RectangleF? initial = frame.FaceSeed is { } seed
                ? new((float)(seed.Px-seed.RadiusPx),(float)(seed.Py-seed.RadiusPx),(float)(2*seed.RadiusPx),(float)(2*seed.RadiusPx)) : null;
            using var dialog = new FaceRegionDialog(RingScanSolver.LoadFrame(frame.ImagePath), frame.Name,
                region => Task.Run(() => ReferenceRegionTracking.Inspect(WithRegion(region),session.Settings,session.DirectionOptions).Inspection), initial);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Selection is not { } region) return;
            var reviewed=ReferenceRegionTracking.Inspect(WithRegion(region),session.Settings,session.DirectionOptions);
            if(reviewed.Inspection.Status!=DatumHoleInspectionStatus.Detected)throw new InvalidDataException("本次框选复核未通过，未保存。");
            var changed = reviewed.Frame;
            File.WriteAllText(Path.ChangeExtension(changed.ImagePath,".region.json"),JsonSerializer.Serialize(reviewed,new JsonSerializerOptions{WriteIndented=true}));
            session.Frames[session.Frames.IndexOf(frame)] = changed;
            new RingScanSession { Settings = session.Settings, DirectionOptions = session.DirectionOptions, Frames = [changed] }.Save(RingCaptureArchive.SidecarPath(changed.ImagePath));
            SaveSession(); await CheckAsync();
        })); acquisition.Controls.Add(box);
        watchButton.Click += (_, _) =>
        {
            try
            {
                if (monitoring) { StopMonitor(); return; }
                if (route is null) throw new InvalidOperationException("请先打开有完整六步位置的记录，或使用下方采图路线工具。");
                CheckRouteOptics();
                if (!_fixedCConfirmed.Checked || !_cnc.IsConnected || _camera is not { IsOpen: true }) throw new InvalidOperationException("先连接相机/数控，核对当前装夹与固定C；并在面板执行已经审核的采图路线。");
                if (_cfg.DeviceMode != "Real") throw new InvalidOperationException("复用真机路线的到位采图仅在真机模式启用。");
                monitoring = true; gate.Reset(); watchButton.Text = "到位自动拍照运行中"; autoTimer.Start();
                output.Text = "只读位置自动采图已启动。机床由面板启动，每个M0停稳后自动拍照；相机不会启动机床或激光。";
            }
            catch (Exception ex) { output.Text = ex.Message; }
        };
        autoTimer.Tick += async (_, _) =>
        {
            if (!monitoring || monitorTickBusy || _workspaceBusy || _laserExposureTask is { IsCompleted: false }) return; monitorTickBusy = true;
            try
            {
                var s = await ReadMachine(); var c = await ReadFixedCAsync(); var point = route![stepChoice.SelectedIndex];
                if (Math.Abs(s.PosZ - point.Z) > .010 || Math.Abs(Math.IEEERemainder(c.Value - point.C, 360)) > .005)
                    throw new InvalidOperationException("当前Z/C与复用路线不符，自动拍照已停止。");
                if (gate.Observe(point, s.PosX, s.PosY, s.PosZ, c.Value, DateTimeOffset.UtcNow))
                {
                    int index = stepChoice.SelectedIndex; gate.Reset(); await Run(CaptureAsync);
                    if (monitoring)
                    {
                        if (index == 5) { StopMonitor(); output.AppendText("\r\n六步自动采图结束，检查结果已保存。"); }
                        else SelectStep(index + 1);
                    }
                }
            }
            catch (Exception ex) { StopMonitor(); output.Text = ex.Message; }
            finally { monitorTickBusy = false; }
        };
        stepChoice.SelectedIndexChanged += (_, _) => { if (updating) return; gate.Reset(); SelectStep(stepChoice.SelectedIndex); };
        table.CurrentCellChanged += (_, _) => { if (!updating && table.CurrentRow is { } row) {gate.Reset();SelectStep(row.Index, true, synchronizeGrid:false);} };
        plot.Paint += (_, e) => PaintRingPreview(e.Graphics, plot.ClientRectangle, fit, targets, alignment?.Direction);
        plot.Resize += (_, _) => plot.Invalidate();

        var routeTools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        routeTools.Controls.Add(Btn("导出当前点微调MPF",async (_,_)=>await Run(async()=>
        {
            if(_cfg.DeviceMode!="Real" || _cnc is not ISinumerikVarAccess access)
                throw new InvalidOperationException("微调NC需要真机R参数接口；离线使用专用模拟检查。");
            if(!_fixedCConfirmed.Checked)throw new InvalidOperationException("先核对当前装夹、参考状态与固定C。");
            CheckRouteOptics();
            int index=stepChoice.SelectedIndex;
            if(!referenceTemplates.ContainsKey(index))throw new InvalidOperationException("缺少当前步骤基准图，请先打开完整采集记录。");
            var position=await ReadMachine();var c=await ReadFixedCAsync();
            var anchor=new CaptureWaypoint(GuidedCapture.Steps[index].Name,GuidedCapture.Steps[index].Role,position.PosX,position.PosY,position.PosZ,c.Value);
            int token=Random.Shared.Next(1,100000000);
            using var dialog=new SaveFileDialog {Filter="XY微调NC|*.MPF",FileName="HM_MICRO.MPF",InitialDirectory=jobFolder??""};
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            File.WriteAllText(dialog.FileName,XyMicroMoveSession.Program(anchor,token),System.Text.Encoding.ASCII);
            RecordEvidenceNc(dialog.FileName);
            microSession=new XyMicroMoveSession(_cnc,access,anchor,token,async ct=>(await ReadFixedCAsync()).Value);microPreparedIndex=index;
            output.Text="当前点微调NC已导出。先确认R200～R209未被其他程序占用，面板审核并启动本次HM_MICRO，再点“微调XY并采图”。每次/累计偏移限0.20mm，最多两次；不移动Z/C、不控制激光。该单点NC须先结束，才能继续原采图路线。";
            showDetails();
        })));
        routeTools.Controls.Add(Btn("保存当前六步为采图路线", async (_, _) => await Run(() =>
        {
            RememberRoute(session); SaveSession(); _state.LastRouteSessionPath = _state.LastSessionPath; SaveOperatorState();
            output.Text = "已记住采图位置。复用前必须核对夹具、光学、C/Z和XY路径；它不是新工件的定位结果。"; return Task.CompletedTask;
        })));
        routeTools.Controls.Add(Btn("导出采图路线MPF", async (_, _) => await Run(() =>
        {
            if (route is null) throw new InvalidOperationException("尚无完整采图路线。");
            CheckRouteOptics();
            if (!_fixedCConfirmed.Checked) throw new InvalidOperationException("请核对当前夹具、参考状态和固定C，复用路线须人工确认适用。");
            using var dialog = new SaveFileDialog { Filter = "相机采图路线|*.MPF", FileName = "HM_CAPTURE.MPF", InitialDirectory = jobFolder ?? "" };
            if (dialog.ShowDialog(this) == DialogResult.OK) {File.WriteAllText(dialog.FileName, GuidedCapture.Program(route), System.Text.Encoding.ASCII);RecordEvidenceNc(dialog.FileName);}
            return Task.CompletedTask;
        })));
        routeTools.Controls.Add(Btn("核对当前与测量基准位置", async (_, _) => await Run(async () =>
        {
            if (route is null) throw new InvalidOperationException("请先打开完整六步记录，或保存当前六步为采图路线。");
            CheckRouteOptics();
            var current = await ReadMachine(); var c = await ReadFixedCAsync(); var target = route[stepChoice.SelectedIndex];
            bool matches = Math.Abs(current.PosX - target.X) <= .005 && Math.Abs(current.PosY - target.Y) <= .005 &&
                Math.Abs(current.PosZ - target.Z) <= .010 && Math.Abs(Math.IEEERemainder(c.Value - target.C, 360)) <= .005;
            output.Text = $"测量基准 {target.Name}：MCS X={target.X:F4} Y={target.Y:F4} Z={target.Z:F4} C={target.C:F4}°\r\n"
                + $"当前差值：ΔX={current.PosX-target.X:F4} ΔY={current.PosY-target.Y:F4} ΔZ={current.PosZ-target.Z:F4} ΔC={Math.IEEERemainder(c.Value-target.C,360):F4}°\r\n"
                + (matches ? "位置与基准相符，可在该位置拍照复核；无需仅因轴曾移动就重做标定。" : "位置尚未恢复到所选测量基准。")
                + (c.Source == AxisCoordinateSource.ManualPanelEntry ? "\r\nC为人工面板确认值，未自动读回。" : "");
            showDetails();
        })));
        routeTools.Controls.Add(Btn("导出18孔相机试走MPF", async (_, _) => await Run(() =>
        {
            if (alignment is null || fit is null || targets.Count != 18) throw new InvalidOperationException("请先完成本次圆心、参考方向和18孔预览。");
            if (!_fixedCConfirmed.Checked) throw new InvalidOperationException("请核对当前装夹、参考状态与固定C；历史坐标只能查看，不能直接用于新装夹。");
            using var dialog = new SaveFileDialog { Filter = "18孔相机试走|*.MPF", FileName = "HM_RING_CAMERA.MPF", InitialDirectory = jobFolder ?? "" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {File.WriteAllText(dialog.FileName, RingScanPreview.CameraTrial(fit, targets), System.Text.Encoding.ASCII);RecordEvidenceNc(dialog.FileName);}
            return Task.CompletedTask;
        })));
        routeTools.Controls.Add(Lbl("只导出XY定位＋逐点M0；C/Z不动，不控制激光。先在面板核对，软件只负责停稳后拍照。"));
        var right = new TabControl { Dock = DockStyle.Fill }; var previewTab = new TabPage("圆心／18孔预览"); previewTab.Controls.Add(plot); previewTab.Controls.Add(previewStatus);
        var settingsTab = new TabPage("光学参数"); settingsTab.Controls.Add(parameters); parameters.PropertyValueChanged += (_, _) =>
        { StopMonitor(); InvalidateResult(); status.Clear(); inspections.Clear(); SaveSession(); SaveReport(); RefreshRows(); };
        var routeTab = new TabPage("复用采图路线"); routeTab.Controls.Add(routeTools);
        var detailsTab = new TabPage("检查与归档"); detailsTab.Controls.Add(output);
        showDetails = () => right.SelectedTab = detailsTab;
        showPhoto = () => right.SelectedTab = photoTab;
        showPreview = () => right.SelectedTab = previewTab;
        RingScanSession ReadSession() => session;
        async Task PrepareRework(string parentRecord)
        {
            var selected=JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(parentRecord))??throw new InvalidDataException("加工记录为空。");
            ReworkZeroExposureEvidence? retry=null;
            if(!selected.Completed && selected.Rework is not null)
                retry=await Task.Run(()=>ReworkZeroExposureEvidence.Load(parentRecord));
            var source=await Task.Run(()=>ProductionReworkSource.Load(retry is null?parentRecord:selected.Rework!.ParentRecordPath));
            if(retry is null)source.EnsureNoRework();
            var reusable=_state.OpticalChangedAt is not null
                ?_readReusableCaptureBaseline?.Invoke()??throw new InvalidOperationException("光学条件已调整，请先建立当前光学条件下的新六步基准，再准备同件补加工。")
                :source.Session;
            var baseline=await Task.Run(()=>ReusableCaptureBaseline.Prepare(reusable,_template));
            var preset=_laserPreset;
            var plan=retry is null?ProductionReworkPlan.Create(source,preset.RequestedExposureMs,preset.Settings.Power):
                ProductionReworkPlan.CreateRetry(source,retry,preset.RequestedExposureMs,preset.Settings.Power);
            string root=EnsureCaptureRoot()??throw new OperationCanceledException("未选择补加工资料目录。");
            string next=Path.Combine(root,"补加工_"+source.Record.RunId.ToString("N")[..8]+"_"+DateTime.Now.ToString("yyyyMMdd_HHmmssfff"));
            Directory.CreateDirectory(next);
            File.WriteAllText(Path.Combine(next,"补加工计划.json"),JsonSerializer.Serialize(plan,new JsonSerializerOptions {WriteIndented=true}));
            string baselinePath=Path.Combine(next,"复用采图基准.json");baseline.Session.Save(baselinePath);
            StopMonitor();if(jobFolder is not null)SaveSession();RememberRoute(baseline.Session);
            session=new RingScanSession {Settings=baseline.Session.Settings,DirectionOptions=baseline.Session.DirectionOptions};jobFolder=next;
            _state.LastRouteSessionPath=baselinePath;
            _fixedCConfirmed.Checked=false;status.Clear();inspections.Clear();InvalidateResult();parameters.SelectedObject=session.Settings;
            var old=photo.Image;photo.Image=null;old?.Dispose();SelectStep(0);SaveSession();SaveReport();
            _restoreProductionJobView?.Invoke(jobFolder);
            output.Text=$"已为同件建立全18孔补加工{(retry is null?"":"重试")}记录，前轮{source.Record.RunId}。本轮每孔追加{preset.RequestedExposureMs/1000.0:0.###}秒、功率原始{preset.Settings.Power}；只建立记录，未运动或出光。";
        }
        _prepareProductionRework=PrepareRework;
        void RestoreOptics(RingScanSettings settings)
        {
            string? root=EnsureCaptureRoot();
            if(root is null)throw new OperationCanceledException("未选择资料目录，未改变当前定位记录。");
            StopMonitor(); SaveSession(); session = new RingScanSession { Settings = settings, DirectionOptions = session.DirectionOptions }; jobFolder = null;
            jobFolder=Path.Combine(root,"工件_"+DateTime.Now.ToString("yyyyMMdd_HHmmssfff"));Directory.CreateDirectory(jobFolder);
            _restoreProductionJobView?.Invoke(jobFolder);
            _fixedCConfirmed.Checked = false; status.Clear(); inspections.Clear(); InvalidateResult(); parameters.SelectedObject = settings; RefreshRows();
            SaveSession();SaveReport();
        }
        _invalidateOpticalBaseline=()=>
        {
            route=null;routeSettings=null;routeDirectionOptions=null;referenceTemplates.Clear();
            _state.LastRouteSessionPath="";InvalidateResult();SaveOperatorState();
        };
        _adoptOpticalSettings=RestoreOptics;
        var laserTab = BuildLaserCompensationTab(ReadSession, () => session.Settings, () => fit, () => targets, RestoreOptics, out invalidateLaser);
        _inspectProductionInputs=async profile=>
        {
            profile.Validate();CheckRouteOptics();
            var calibration=(_readCalibrationForStartup??throw new InvalidOperationException("落点检查未接入。"))();
            var bounds=profile.Envelope!;
            var captureRoute=route??throw new InvalidOperationException("缺少六步基准，请在维护中建立或载入当前工位的六步基准；日常无需重新配置。");
            if(captureRoute.Count!=6 || captureRoute.Any(p=>!bounds.Contains(p.X,p.Y)||Math.Abs(p.Z-bounds.Z)>.010||Math.Abs(Math.IEEERemainder(p.C-bounds.C,360))>.005))
                throw new InvalidOperationException("六步采图路线与主控工作范围或固定Z/C不一致，请在维护中核对基准。");
            var original=new RingScanSession{Settings=session.Settings,DirectionOptions=session.DirectionOptions,
                Frames=referenceTemplates.OrderBy(p=>p.Key).Select(p=>p.Value).ToList()};
            await Task.Run(()=>ReusableCaptureBaseline.Prepare(original,_template));
            calibration.ValidateReference(bounds.Z,bounds.C);
            return "自动资料检查通过：配套主控配置、六步采图基准、光学参数及落点来源。";
        };
        async Task<ProductionPreparation> PrepareConnectedProduction(ProductionStationProfile profile,bool dryRun)
        {
            try
            {
            profile.ValidateFor(dryRun);CheckRouteOptics();
            if(_cfg.DeviceMode!="Real" || _cnc is not ISinumerikVarAccess access || !_cnc.IsConnected || _camera is not {IsOpen:true})
                throw new InvalidOperationException("整件联接要求已连接现场相机与数控。");
            if(!_fixedCConfirmed.Checked && !_guidedOperationConfirmed) throw new InvalidOperationException("请通过一键加工完成本次现场确认，或在维护操作中确认本次装夹／参考及固定C。");
            if(jobFolder is null) throw new InvalidOperationException("尚无本次记录：全新件点新工件；同一已加工件请在补加工页建立关联记录。");
            if(session.Frames.Count!=0) throw new InvalidOperationException("当前记录已有照片，不能直接重跑。补加工采图失败且未出光时，请在补加工页使用未出光重试。");
            if(!dryRun)ProductionJobArchive.EnsureUnused(jobFolder);
            var calibration=(_readProductionCalibration??throw new InvalidOperationException("未接入激光补偿。"))();
            var bounds=profile.Envelope!;var captureRoute=route??throw new InvalidOperationException("缺少完整六步采图路线。");
            if(captureRoute.Any(p=>!bounds.Contains(p.X,p.Y)||Math.Abs(p.Z-bounds.Z)>.010||Math.Abs(Math.IEEERemainder(p.C-bounds.C,360))>.005))
                throw new InvalidOperationException("六步采图路线与主控边界或固定Z/C不符。");
            var sourceBaseline=new RingScanSession {Settings=session.Settings,DirectionOptions=session.DirectionOptions,
                Frames=referenceTemplates.OrderBy(p=>p.Key).Select(p=>p.Value).ToList()};
            ReusableCaptureBaselineReview baselineReview;
            try
            {
                baselineReview=await Task.Run(()=>ReusableCaptureBaseline.Prepare(sourceBaseline,_template));
                File.WriteAllText(Path.Combine(jobFolder,"采图基准检查.json"),JsonSerializer.Serialize(new {
                    CheckedAt=DateTimeOffset.Now,Passed=true,SourcePath=_state.LastRouteSessionPath,
                    CurrentWorkpieceCoordinates=false,Review=baselineReview},new JsonSerializerOptions {WriteIndented=true}));
            }
            catch(Exception ex)
            {
                try{File.WriteAllText(Path.Combine(jobFolder,"采图基准检查.json"),JsonSerializer.Serialize(new {
                    CheckedAt=DateTimeOffset.Now,Passed=false,SourcePath=_state.LastRouteSessionPath,
                    Error=ex.Message,OriginalBaseline=sourceBaseline,MachineRequestSubmitted=false},new JsonSerializerOptions {WriteIndented=true}));}
                catch(Exception write){_log.Warn("基准失败诊断未保存："+write.Message);}
                throw;
            }
            foreach(var frame in baselineReview.Session.Frames)referenceTemplates[GuidedCapture.Index(frame)]=frame;
            if(_laser is not Hummingbird.Devices.LaserRtu.ModbusRtuLaserService device || !device.IsOpen)
                throw new InvalidOperationException("先打开激光串口；无出光试走也须读回确认关光。");
            var laserState=await device.ReadRecordedProtocolAsync();
            if(laserState.Emission!=0)throw new InvalidOperationException("激光未读回关光，未开始运动。");
            var preset=_laserPreset;
            ProductionReworkPlan? reworkPlan=null;ProductionReworkSource? reworkSource=null;
            string reworkFile=Path.Combine(jobFolder,"补加工计划.json");
            if(File.Exists(reworkFile))
            {
                reworkPlan=JsonSerializer.Deserialize<ProductionReworkPlan>(File.ReadAllText(reworkFile))??throw new InvalidDataException("补加工计划为空。");
                if(_activeReworkPlan?.Info!=reworkPlan.Info)throw new InvalidOperationException("补加工计划未正确恢复，未开始。");
                reworkSource=await Task.Run(()=>reworkPlan.Verify(preset.RequestedExposureMs,preset.Settings.Power));reworkSource.EnsureCanStart(reworkPlan.Info!);
            }
            if(!dryRun && (!laserState.CanExpose || !preset.Matches(laserState)))throw new InvalidOperationException("当前激光状态或参数与界面设置不符，未开始运动："+string.Join("；",preset.Differences(laserState))+"。请应用当前参数后核对读回。");
            var nc=new ContinuousNcSession(_cnc,access,bounds,profile.SessionToken,async token=>(await ReadFixedCAsync()).Value);
            var position=await nc.VerifyReadyAsync();
            string checkPath=Path.Combine(jobFolder,"加工前就绪检查.json");
            File.WriteAllText(checkPath,JsonSerializer.Serialize(new {GeneratedAt=DateTimeOffset.Now,DryRun=dryRun,Ready=true,
                Profile=profile,Preset=preset,LaserState=laserState,ActualPosition=position,CaptureRoute=captureRoute,
                OpticalSettings=session.Settings,LaserCalibration=calibration},new JsonSerializerOptions {WriteIndented=true}));
            EvidenceAction(()=>_fieldEvidence!.AddFile(checkPath,"定位/加工前就绪检查.json","整件开始前只读检查"));
            return new(nc,device,calibration,captureRoute.ToArray(),preset,reworkPlan,reworkSource);
            }
            catch(Exception ex)
            {
                if(jobFolder is not null)
                    try{File.WriteAllText(Path.Combine(jobFolder,"整件就绪失败.json"),JsonSerializer.Serialize(new {
                        CheckedAt=DateTimeOffset.Now,Error=ex.Message,RouteSourcePath=_state.LastRouteSessionPath,
                        BaselineFrames=referenceTemplates.OrderBy(p=>p.Key).Select(p=>p.Value).ToArray(),
                        CurrentFrames=session.Frames.Count,MachineRequestSubmitted=false,DeviceMode=_cfg.DeviceMode},
                        new JsonSerializerOptions {WriteIndented=true}));}
                    catch(Exception write){_log.Warn("就绪失败诊断未保存："+write.Message);}
                throw;
            }
        }
        async Task<string> CheckConnectedProduction(ProductionStationProfile profile,bool dryRun)
        {
            var ready=await PrepareConnectedProduction(profile,dryRun);
            return $"就绪：相机/数控/激光已连接；固定Z/C、六处基准和本次落点来源符合；主控会话{profile.SessionToken}读回正常。\r\n"
                +(dryRun?"本配置仅执行无出光试走。":ready.ReworkPlan?.Info is { } rw
                    ?$"本轮同件18孔补加工，每孔追加{rw.AdditionalExposureMs/1000.0:0.###}秒、功率原始{rw.PowerRaw}；累计每孔请求{rw.CumulativeRequestedMs/1000.0:0.###}秒。"
                    :$"本件将连续加工18孔，每孔{ready.Preset.RequestedExposureMs/1000.0:0.###}秒、功率原始值{ready.Preset.Settings.Power}。")
                +"\r\n本次检查不运动、不出光；点击开始时会再次检查。";
        }
        async Task RunConnectedProduction(ProductionStationProfile profile,bool dryRun)
        {
            var ready=await PrepareConnectedProduction(profile,dryRun);
            _guidedOperationStop?.Token.ThrowIfCancellationRequested();
            var bounds=profile.Envelope!;var captureRoute=ready.Route;var calibration=ready.Calibration;var device=ready.Laser;var nc=ready.Nc;
            int? duration=dryRun?null:ready.Preset.RequestedExposureMs;
            var record=new ProductionRunRecord {DryRun=dryRun,RequestedExposureMs=duration,Rework=ready.ReworkPlan?.Info};
            string activeJob=jobFolder!;
            string folder=Path.Combine(activeJob,"整件_"+record.RunId.ToString("N"));Directory.CreateDirectory(folder);
            SaveGuidedStartupEvidence(folder);
            if(File.Exists(Path.Combine(folder,"自动启动检查.json")))EvidenceAction(()=>_fieldEvidence!.AddFile(Path.Combine(folder,"自动启动检查.json"),"整件/"+record.RunId+"/自动启动检查.json","本轮自动检查及集中现场确认"));
            File.WriteAllText(Path.Combine(folder,"运行来源.json"),JsonSerializer.Serialize(new {DeviceMode=_cfg.DeviceMode,Profile=profile,CaptureRoute=captureRoute,
                Settings=session.Settings,DirectionOptions=session.DirectionOptions,LaserCalibration=calibration,LaserPreset=ready.Preset,RequestedExposureMs=duration,Rework=record.Rework,
                Purpose=dryRun?"ActualMotionWithoutEmission":"ActualMachining",GeneratedAt=DateTimeOffset.Now},new JsonSerializerOptions {WriteIndented=true}));
            EvidenceAction(()=>_fieldEvidence!.AddFile(Path.Combine(folder,"运行来源.json"),"整件/"+record.RunId+"/运行来源.json","整件配置与标定来源"));
            if(!dryRun)ProductionJobArchive.Begin(activeJob,record);
            StopMonitor();using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_guidedOperationStop?.Token??CancellationToken.None);_productionStop=cancellation;
            var ct=cancellation.Token;
            var cell=new ConnectedProductionCell(device.VerifyEmissionOffAsync,
                async(x,y,token)=>
                {
                    await nc.MoveAndSettleAsync(x,y,token);
                    if(dryRun && record.Phase.StartsWith("HoleMove:"))
                        await RunSingleSpotTrial(true,propagateErrors:true,folderOverride:Path.Combine(folder,"hole_"+record.Phase.Split(':')[1]),ct:token,
                            presetOverride:ready.Preset,productionRunId:record.RunId,holeId:record.CurrentHoleId);
                },
                async(index,token)=>
                {
                    token.ThrowIfCancellationRequested();SelectStep(index);
                    productionSeed=referenceTemplates.GetValueOrDefault(index)?.FaceSeed is { } seed
                        ?seed with {SelectionSource="ProductionBaselineProposal_MustBeRefitted"}:null;
                    productionFallbackSeed=null;
                    RingScanFrame frame;
                    try
                    {
                        if(index==5)
                        {
                            var one=session.Frames.Single(f=>f.Role==RingFrameRole.Reference1);
                            var current=await ReadMachine();var c=await ReadFixedCAsync();
                            var target=new CaptureWaypoint(GuidedCapture.Steps[index].Name,RingFrameRole.Reference11,current.PosX,current.PosY,current.PosZ,c.Value);
                            var prediction=await Task.Run(()=>CurrentReferenceRegion.PredictEleven(one,session.Settings,session.DirectionOptions,
                                fit??throw new InvalidOperationException("本件四方位圆环尚未通过，不能预测11点。"),target,
                                productionSeed??throw new InvalidOperationException("缺少11点参考框。")),token);
                            productionFallbackSeed=prediction.Seed;
                            File.WriteAllText(Path.Combine(folder,"参考11点框预测_"+DateTime.Now.ToString("HHmmssfff")+".json"),
                                JsonSerializer.Serialize(prediction,new JsonSerializerOptions {WriteIndented=true}));
                        }
                        frame=await CaptureStepAsync(index,token);
                    }
                    finally{productionSeed=null;productionFallbackSeed=null;}
                    token.ThrowIfCancellationRequested();
                    if(frame.Role==RingFrameRole.RingArc || inspections.TryGetValue(frame.ImagePath,out var check)&&check.Status==DatumHoleInspectionStatus.Detected)
                        return new CaptureCorrection(0,0,true,frame.ImagePath);
                    var reference=referenceTemplates.GetValueOrDefault(index)??throw new InvalidOperationException("缺少纠偏基准图。");
                    RingCaptureArchive.VerifyImage(reference);
                    XyMicroAdjustment correction;
                    try{correction=await Task.Run(()=>XyImageRegistration.Measure(RingScanSolver.LoadFrame(reference.ImagePath),RingScanSolver.LoadFrame(frame.ImagePath),session.Settings,reference.FaceSeed));}
                    catch(InvalidDataException ex)
                    {
                        var diagnosis=new {Step=index+1,frame.Name,frame.ImagePath,ReferenceImage=reference.ImagePath,
                            Boundary=inspections.GetValueOrDefault(frame.ImagePath),CorrectionError=ex.Message,CorrectionMotionRequested=false};
                        File.WriteAllText(Path.Combine(folder,"采图诊断.json"),JsonSerializer.Serialize(diagnosis,new JsonSerializerOptions {WriteIndented=true}));
                        throw new InvalidDataException($"{frame.Name}照片已保存，但实体圆边及自动纠偏未通过：{ex.Message} 本轮仍在采图阶段，未开始18孔出光。照片：{frame.ImagePath}",ex);
                    }
                    return new CaptureCorrection(correction.DeltaX,correction.DeltaY,false,frame.ImagePath);
                },
                async token=>
                {
                    token.ThrowIfCancellationRequested();var located=await Task.Run(()=>new DialAlignmentSolver().Analyze(session,_template));
                    File.WriteAllText(Path.Combine(folder,"定位稳定性检查.json"),JsonSerializer.Serialize(new{located.Ring.CompatibilityRecovery,located.TargetStability},new JsonSerializerOptions{WriteIndented=true}));
                    var compensated=LaserSpotCompensation.Preview(calibration,session.Settings,located.Ring.ReferenceZ,located.Ring.ReferenceC,located.Targets);
                    if(compensated.Targets.Any(p=>!bounds.Contains(p.LaserCommandX,p.LaserCommandY)))throw new InvalidOperationException("补偿孔位超出核定边界。");
                    ready.ReworkSource?.VerifyPattern(compensated.Targets.Select(p=>new ProductionPoint(p.Id,p.LaserCommandX,p.LaserCommandY)).ToArray());
                    File.WriteAllText(Path.Combine(folder,"定位与补偿.json"),JsonSerializer.Serialize(compensated,new JsonSerializerOptions {WriteIndented=true}));
                    EvidenceAction(()=>_fieldEvidence!.AddFile(Path.Combine(folder,"定位与补偿.json"),"整件/"+record.RunId+"/定位与补偿.json","18孔定位与补偿结果"));
                    return (IReadOnlyList<ProductionPoint>)compensated.Targets.Select(p=>new ProductionPoint(p.Id,p.LaserCommandX,p.LaserCommandY)).ToArray();
                },
                async(milliseconds,token)=>
                {
                    if(duration!=milliseconds)throw new InvalidOperationException("整件请求时长不一致。");
                    int id=record.Holes.Single(p=>p.Value==ProductionHolePhase.Started).Key;
                    await RunSingleSpotTrial(false,propagateErrors:true,folderOverride:Path.Combine(folder,$"hole_{id}"),ct:token,
                        presetOverride:ready.Preset,productionRunId:record.RunId,holeId:id);
                },
                async state=>
                {
                    ProductionJobArchive.Save(activeJob,folder,state);
                    string target=Path.Combine(folder,"run.json");
                    EvidenceAction(()=>_fieldEvidence!.AddFile(target,"整件/"+record.RunId+"/run.json","整件进度及失败孔"));
                    _reportProductionProgress?.Invoke(state,folder);
                    if(state.FinishedAt is not null)EvidenceAction(()=>_fieldEvidence!.AddFile(Path.Combine(folder,"本件结果.md"),"整件/"+record.RunId+"/本件结果.md","本件工序完成或停止结果"));
                    await Task.CompletedTask;
                },nc.EndAsync);
            try{await new ContinuousProduction(cell).RunAsync(captureRoute,record,ct);}
            finally
            {
                _productionStop=null;productionSeed=null;productionFallbackSeed=null;
                try
                {
                    _reportProductionDetail?.Invoke("正在生成本件资料包……");
                    record.ResultArchivePath=Path.Combine(folder,"本件资料.zip");
                    ProductionJobArchive.Save(activeJob,folder,record);
                    await Task.Run(()=>ExportProductionResult(folder,session,record,calibration));
                    _reportProductionDetail?.Invoke("资料包："+record.ResultArchivePath);
                }
                catch(Exception ex)
                {
                    record.ResultArchivePath=null;record.ArchiveError=ex.Message;
                    _log.Error("本件资料包未生成，原始记录保留："+ex.Message);
                    _reportProductionDetail?.Invoke("本件资料包未生成："+ex.Message+"；原始目录仍保留。");
                    try{ProductionJobArchive.Save(activeJob,folder,record);}catch(Exception save){_log.Error("本件结果更新失败："+save.Message);}
                }
                _reportProductionProgress?.Invoke(record,folder);
                RefreshEvidencePackage();UpdateLaserCalibrationButtons();
            }
        }
        var progressTab=new TabPage("六步采集进度");progressTab.Controls.Add(table);
        var productionTab=BuildConnectedProductionTab(RunConnectedProduction,CheckConnectedProduction,Run);
        // 六步状态固定可见；原始照片、圆心与加工详情在旁边切换。
        right.Multiline=true;
        right.TabPages.AddRange([productionTab,photoTab,previewTab,detailsTab,routeTab]);
        _captureDetails=right;
        _selectProductionPanel=()=>{SelectPage(tab);right.SelectedTab=productionTab;};
        _returnPage=BuildStationReturnTab(()=>route,Run); _returnPage.Text="工位恢复";
        _selectStationReturnPanel=()=>SelectPage(_returnPage);
        _opticsPage=BuildOpticalWorkflowPage(laserTab,settingsTab,Run);
        _toolsPage=new TabPage("维护");
        var tools=new TabControl {Dock=DockStyle.Fill,Multiline=true};
        tools.TabPages.AddRange([BuildSpotTrialTab(),BuildNcFilesTab(),BuildMaintenanceTab(),BuildProductionDemoTab(Run),BuildFieldEvidenceTab(ReadSession,()=>stepChoice.SelectedIndex,Run)]);
        _toolsPage.Controls.Add(tools);
        var left=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};
        left.RowStyles.Add(new RowStyle(SizeType.Percent,100));left.RowStyles.Add(new RowStyle(SizeType.Absolute,175));
        left.Controls.Add(liveHost,0,0);left.Controls.Add(table,0,1);
        var body = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1280,630), SplitterDistance=610, Panel1MinSize=400, Panel2MinSize=490 };
        body.Panel1.Controls.Add(left); body.Panel2.Controls.Add(right);
        _captureProjectControls=project;
        var manual=new TabPage("手动采图");
        var manualLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};
        manualLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));manualLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));manualLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        manualLayout.Controls.Add(acquisition,0,0);manualLayout.Controls.Add(instruction,0,1);
        manualLayout.Controls.Add(Lbl("整件加工会自动完成六处采图、拟合圆心和18孔定位。手动入口用于建基准或排查失败照片。"),0,2);
        manual.Controls.Add(manualLayout);right.TabPages.Add(manual);
        project.Visible=false;
        var projectToggle=Btn("工件与资料（维护）",(_,_)=>project.Visible=!project.Visible);
        var viewToggle=Btn("照片与定位诊断（维护）",(_,_)=>
        {if(right.SelectedTab==productionTab)right.SelectedTab=photoTab;else right.SelectedTab=productionTab;});
        void UpdateProductionView()
        {
            bool production=right.SelectedIndex<=0 || right.SelectedTab==productionTab;
            right.Appearance=production?TabAppearance.FlatButtons:TabAppearance.Normal;
            right.SizeMode=production?TabSizeMode.Fixed:TabSizeMode.Normal;
            right.ItemSize=production?new Size(1,1):new Size(0,22);
            right.Multiline=!production;right.TabStop=!production;
            viewToggle.Text=production?"照片与定位诊断（维护）":"返回一键加工";
            captureFeedback.Visible=!production || captureFeedback.ForeColor==Color.DarkRed;
        }
        right.SelectedIndexChanged+=(_,_)=>UpdateProductionView();captureFeedback.TextChanged+=(_,_)=>UpdateProductionView();
        var viewActions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};viewActions.Controls.AddRange([projectToggle,viewToggle]);
        var top = new TableLayoutPanel { Dock=DockStyle.Fill, AutoSize=true, ColumnCount=1, RowCount=4 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(int row=0;row<4;row++)top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        void FitMaintenanceRows()
        {
            top.RowStyles[1].SizeType=project.Visible?SizeType.AutoSize:SizeType.Absolute;top.RowStyles[1].Height=0;
            top.RowStyles[3].SizeType=captureFeedback.Visible?SizeType.AutoSize:SizeType.Absolute;top.RowStyles[3].Height=0;
        }
        project.VisibleChanged+=(_,_)=>FitMaintenanceRows();captureFeedback.VisibleChanged+=(_,_)=>FitMaintenanceRows();
        top.Controls.Add(viewActions,0,0);top.Controls.Add(project,0,1);top.Controls.Add(jobLabel,0,2);top.Controls.Add(captureFeedback,0,3);
        var layout = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.Controls.Add(top,0,0);layout.Controls.Add(body,0,1);tab.Controls.Add(layout);
        if (File.Exists(_state.LastRouteSessionPath)) try { RememberRoute(RingScanSession.Load(_state.LastRouteSessionPath)); } catch (Exception ex) { _log.Warn("历史采图路线未恢复：" + ex.Message); }
        tab.Disposed += (_, _) => { autoTimer.Stop(); autoTimer.Dispose(); photo.Image?.Dispose(); };
        UpdateProductionView();FitMaintenanceRows();RefreshRows(); return tab;
    }

    private static double RingNumber(string? text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) throw new InvalidDataException("请输入有效小数数值。"); return value;
    }
    private static void DrawReference(Graphics graphics, DatumHoleDetection hole, Color color)
    {
        using var pen = new Pen(color, 2); var state = graphics.Save(); graphics.TranslateTransform((float)hole.Px, (float)hole.Py); graphics.RotateTransform((float)hole.EllipseAngleDeg);
        graphics.DrawEllipse(pen, -(float)hole.EllipseWidthPx / 2, -(float)hole.EllipseHeightPx / 2, (float)hole.EllipseWidthPx, (float)hole.EllipseHeightPx); graphics.Restore(state);
        graphics.DrawLine(pen, (float)hole.Px - 12, (float)hole.Py, (float)hole.Px + 12, (float)hole.Py); graphics.DrawLine(pen, (float)hole.Px, (float)hole.Py - 12, (float)hole.Px, (float)hole.Py + 12);
    }
    private static void PaintRingPreview(Graphics g, Rectangle bounds, RingScanResult? fit, IReadOnlyList<HoleTarget> targets, DatumDirectionResult? direction)
    {
        if (fit is null || bounds.Width < 20 || bounds.Height < 20) return;
        double scale = Math.Min(bounds.Width - 30, bounds.Height - 30) / (fit.RadiusMm * 2);
        PointF Point(double x, double y) => new((float)(bounds.Width / 2.0 + (x - fit.CenterX) * scale), (float)(bounds.Height / 2.0 - (y - fit.CenterY) * scale));
        float radius = (float)(fit.RadiusMm * scale); g.DrawEllipse(Pens.Gray, bounds.Width / 2f - radius, bounds.Height / 2f - radius, radius * 2, radius * 2);
        foreach (var sample in fit.Points) { var p = Point(sample.X, sample.Y); g.FillEllipse(Brushes.Green, p.X - 1, p.Y - 1, 2, 2); }
        foreach (var target in targets) { var p = Point(target.MachineX, target.MachineY); g.FillEllipse(Brushes.Red, p.X - 3, p.Y - 3, 6, 6); g.DrawString(target.Id.ToString(), SystemFonts.DefaultFont, Brushes.Black, p); }
        if (direction is not null) { double a = direction.TwelveAngleDeg * Math.PI / 180; var p = Point(fit.CenterX + fit.RadiusMm * Math.Cos(a), fit.CenterY + fit.RadiusMm * Math.Sin(a)); g.DrawLine(Pens.Blue, Point(fit.CenterX, fit.CenterY), p); g.DrawString("12点", SystemFonts.DefaultFont, Brushes.Blue, p); }
        g.DrawString("定位预览 · 未验收加工精度", SystemFonts.DefaultFont, Brushes.Black, 4, 4);
    }
}
