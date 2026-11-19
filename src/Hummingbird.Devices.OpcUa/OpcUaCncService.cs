using System.Text;
using Hummingbird.Core;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Hummingbird.Devices.OpcUa;

/// <summary>
/// SINUMERIK 828D 数控服务（OPC UA，端口4840，需 Access MyMachine /OPC UA 授权）。
///
/// 连接流程（ConnectAsync 内部按此顺序记日志，出问题时对照日志定位卡在哪一步）：
///   1. 组装客户端配置（应用名/证书存储目录 %LocalApplicationData%\Hummingbird\pki）
///   2. 校验配置 + 生成本机自签名证书（首次运行会生成，828D 端需信任该证书）
///   3. SelectEndpoint 发现服务器端点（此步不通 = 网络不通/IP错/服务未开）
///   4. 建立会话（此步不通 = 证书未被 828D 信任，需在 828D 上信任客户端证书）
///
/// 节点映射：变量命名空间在会话建立后从服务器命名空间表自动解析（按关键字匹配
/// "SinumerikVarProvider"，可由 config.json 的 CncVarNamespace 覆盖）。
/// 现场核实（2026-11-02，UaExpert）：这台 828D 的变量节点在 ns=2，
/// ns=0=OPC UA 标准、ns=1=服务器实例（SinumerikServer）、ns=2=SinumerikVarProvider。
/// 轴名可配（config.json 的 CncAxisX/Y/ZName），以 UaExpert 实际展开结果为准。
/// </summary>
public sealed partial class OpcUaCncService : ICncService, ISinumerikVarAccess, ISinumerikVarProbe
{
    /// <summary>
    /// 连接成功后是否自动跑一遍通用连接诊断（命名空间表 + XYZ 位置候选扫描）。默认 true。
    /// 现场「只读探测」按钮会显式置 false（MainForm.ProbeOpcUaAsync）改跑更完整的转台专项探测，
    /// 避免对真机重复读上百个节点。
    /// </summary>
    public bool RunConnectDiagnostics { get; set; } = true;
    /// <summary>程序下发用的网络共享路径（如 \\192.168.214.1\nc），由 App 配置注入。</summary>
    public string? ProgramSharePath { get; set; }
    /// <summary>变量命名空间匹配关键字（连接时在命名空间表里找包含它的 URI）。</summary>
    public string VarNamespaceKeyword { get; set; } = "SinumerikVarProvider";
    /// <summary>OPC UA 登录用户名（空 = 匿名）。828D SW4.7+ 匿名只能建会话、读变量被拒，需 HMI 侧创建用户。</summary>
    public string? UserName { get; set; }
    /// <summary>OPC UA 登录密码。</summary>
    public string? Password { get; set; }
    /// <summary>true = 选加密端点（Basic256Sha256 - Sign）；false = None。None 下密码明文传输，部分服务器拒绝用户名登录。</summary>
    public bool UseSecureEndpoint { get; set; }
    /// <summary>轴位置节点整路径（不带 ns）。现场核实（2026-11-02 实测日志）：该 828D 轴索引用数字
    /// [u1, 1|2|3]（几何轴1=X/2=Y/3=Z），字母写法 [u1, X] 无效；actToolBasePos[u1, 1] 实测可读且随轴动。</summary>
    public string PosNodeX { get; set; } = "/Channel/GeometricAxis/actToolBasePos[u1, 1]";
    public string PosNodeY { get; set; } = "/Channel/GeometricAxis/actToolBasePos[u1, 2]";
    public string PosNodeZ { get; set; } = "/Channel/GeometricAxis/actToolBasePos[u1, 3]";

    private readonly IAppLogger _log;
    private Session? _session;
    private ApplicationConfiguration _config = null!;
    private Timer? _poll;
    private event Action<AxisPosition>? _posChanged;

    public OpcUaCncService(IAppLogger? log = null)
    {
        _log = log ?? new SilentLogger();
    }

    public string Endpoint { get; private set; } = "";
    public bool IsConnected => _session is { Connected: true };

    // ---- 节点映射（命名空间索引连接时解析，位置节点路径可配）----
    private ushort _varNs = 2;   // 默认 2（现场核实值），连接后 ResolveVarNamespaceAsync 会再确认

    /// <summary>R 参数节点：/Channel/Parameter/R[u1, n]，u1=通道1。
    /// 该路径已被西门子 VarProvider 文档原样证实（R-Parameter n in Channel 1）。</summary>
    private NodeId R(int n) => new($"/Channel/Parameter/R[u1, {n}]", _varNs);
    /// <summary>轴位置节点（只读，整路径可配，ns 连接时解析）。</summary>
    private NodeId Node(string path) => new(path, _varNs);

#pragma warning disable CS0618 // Validate/CreateAsync 系列新旧 API 混用，先按 1.5.378 可用形态实现

    public async Task<bool> ConnectAsync(string endpoint, CancellationToken ct = default)
    {
        Endpoint = endpoint;
        _log.Info($"[数控] 连接步骤1/4：组装客户端配置 → {endpoint}");

        // ---- 步骤1：客户端应用配置 ----
        // 证书存在本机 %LocalApplicationData%\Hummingbird\pki，首次连接自动生成，
        // 之后要在 828D 的"调试→OPC UA→受信任的客户端"里信任它（一次性操作）。
        _config = new ApplicationConfiguration
        {
            ApplicationName = "Hummingbird",
            ApplicationUri = Utils.Format("urn:{0}:Hummingbird", System.Net.Dns.GetHostName()),
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = @"Directory",
                    StorePath = @"%LocalApplicationData%\Hummingbird\pki\own",
                    SubjectName = "CN=Hummingbird"
                },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = @"Directory", StorePath = @"%LocalApplicationData%\Hummingbird\pki\issuer" },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = @"Directory", StorePath = @"%LocalApplicationData%\Hummingbird\pki\trusted" },
                RejectedCertificateStore = new CertificateTrustList { StoreType = @"Directory", StorePath = @"%LocalApplicationData%\Hummingbird\pki\rejected" },
                AutoAcceptUntrustedCertificates = true   // 现场调试期自动信任服务器证书；正式产线可改 false 收紧
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
        };
        await _config.Validate(ApplicationType.Client);

        _log.Info("[数控] 连接步骤2/4：生成/检查本机证书");
        var app = new ApplicationInstance
        { ApplicationName = "Hummingbird", ApplicationType = ApplicationType.Client, ApplicationConfiguration = _config };
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct);

        _log.Info("[数控] 连接步骤3/4：发现服务器端点（不通=检查网线/IP/4840端口）");
        var epDesc = CoreClientUtils.SelectEndpoint(_config, endpoint, UseSecureEndpoint, 15000);
        _log.Info($"[数控] 端点确认：{epDesc.EndpointUrl} 安全策略={epDesc.SecurityPolicyUri}");

        _log.Info("[数控] 连接步骤4/4：建立会话（不通=在828D上信任客户端证书）");
        UserIdentity identity = string.IsNullOrWhiteSpace(UserName)
            ? new UserIdentity(new AnonymousIdentityToken())
            : new UserIdentity(UserName, System.Text.Encoding.UTF8.GetBytes(Password ?? ""));
        _log.Info(string.IsNullOrWhiteSpace(UserName)
            ? "[数控] 登录方式：匿名（828D SW4.7+ 匿名读 NC 变量会被拒，生产用请配置 CncUserName）"
            : $"[数控] 登录方式：用户名 {UserName}（读取被拒=检查该用户在 HMI 里是否被赋变量读写权限）");
        var configured = new ConfiguredEndpoint(null, epDesc, EndpointConfiguration.Create(_config));
        _session = await Session.CreateAsync(_config, (ReverseConnectManager?)null, configured, false, false, "Hummingbird",
            60000, identity, null, ct);

        bool ok = IsConnected;
        if (ok)
        {
            _varNs = await ResolveVarNamespaceAsync(ct);
            _log.Info($"[数控] 会话建立成功（变量命名空间 ns={_varNs}）");
            // 现场探测按钮自带专项扫描，这里跳过通用诊断以免对真机重复读上百个节点
            if (RunConnectDiagnostics) await LogDiagnosticsAsync(ct);   // 现场联调：打全候选节点可读性日志（只读不写）
        }
        else
        {
            _log.Info("[数控] 会话建立失败（返回未连接）");
        }
        return ok;
    }

    /// <summary>
    /// 连接后诊断（现场联调用）：把"哪个节点能读、值是什么"全部打进日志，
    /// 用户把日志贴回开发侧即可定死节点路径。全程只读、绝不写机床。
    /// </summary>
    private async Task LogDiagnosticsAsync(CancellationToken ct)
    {
        try
        {
            // 1) 命名空间表
            var ns = (await _session!.ReadValueAsync(new NodeId(2255), ct)).Value as string[];
            if (ns is not null)
                for (int i = 0; i < ns.Length; i++)
                    _log.Info($"[数控][诊断] 命名空间 ns={i}: {ns[i]}");

            // 2) R 参数节点（生产链路关键，读 R1 验证）
            await TryReadLog("[数控][诊断] R参数路径验证 R1: /Channel/Parameter/R[u1, 1]",
                             "/Channel/Parameter/R[u1, 1]", ct);

            // 3) 当前配置的三个位置节点
            await TryReadLog($"[数控][诊断] 配置的位置节点 X: {PosNodeX}", PosNodeX, ct);
            await TryReadLog($"[数控][诊断] 配置的位置节点 Y: {PosNodeY}", PosNodeY, ct);
            await TryReadLog($"[数控][诊断] 配置的位置节点 Z: {PosNodeZ}", PosNodeZ, ct);

            // 4) 候选扫描：目录 × 变量 × 轴写法（X 轴定模式，Y/Z 后续类推）
            string[] dirs = { "/Channel/GeometricAxis", "/Channel/MachineAxis" };
            string[] vars = { "actToolBasePos", "aaPos", "aaIm", "aaMm", "aaMa", "actPos" };
            string[] axes = { "[u1, 1]", "[u1, 2]", "[u1, 3]", "[u1, X]", "" };   // 数字索引为实测有效写法；""=不带索引(默认索引)
            int hits = 0;
            foreach (var dir in dirs)
                foreach (var v in vars)
                    foreach (var ax in axes)
                    {
                        string p = $"{dir}/{v}{ax}";
                        if (await TryReadLog($"[数控][诊断] 候选命中 {p}", p, ct, quietFail: true))
                            hits++;
                    }
            _log.Info($"[数控][诊断] 候选扫描完成：可读 {hits} 个（上面\"候选命中\"行即可用路径）");

            // 5) 目录浏览：把真实存在的子节点名全部列出来（登录后权限足够才有意义）
            LogChildren("/Channel");
            LogChildren("/Channel/GeometricAxis");
            LogChildren("/Channel/MachineAxis");
        }
        catch (Exception ex)
        {
            _log.Warn($"[数控][诊断] 中断：{ex.Message}");
        }
    }

    /// <summary>列出一个目录节点的全部子节点名（Browser 辅助类，内部自动翻页），打进日志。位置变量名以此为准，不靠猜。</summary>
    private void LogChildren(string dirPath)
    {
        try
        {
#pragma warning disable CS0618 // Browser.Browse(同步) 在此版本仍可用，内部自动处理续页
            var browser = new Browser(_session!)
            {
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = 0,
                ResultMask = (uint)BrowseResultMask.All
            };
            var names = browser.Browse(new NodeId(dirPath, _varNs))
                                 .Select(r => r.DisplayName?.Text ?? "?").ToList();
#pragma warning restore CS0618
            _log.Info($"[数控][诊断] 目录 {dirPath} 共 {names.Count} 个子节点：");
            for (int i = 0; i < names.Count; i += 40)   // 每行 40 个，避免单行过长
                _log.Info($"[数控][诊断]   {string.Join(", ", names.Skip(i).Take(40))}");
        }
        catch (Exception ex)
        {
            _log.Info($"[数控][诊断] 目录 {dirPath} 浏览异常：{ex.Message}");
        }
    }

    /// <summary>读一个节点：成功打值+类型，失败打错误（quietFail=true 时失败不刷屏）。</summary>
    private async Task<bool> TryReadLog(string label, string path, CancellationToken ct, bool quietFail = false)
    {
        try
        {
            var dv = await _session!.ReadValueAsync(new NodeId(path, _varNs), ct);
            _log.Info($"{label} = {dv.Value} ({dv.Value?.GetType().Name}, 状态 {dv.StatusCode})");
            return StatusCode.IsGood(dv.StatusCode) && dv.Value is not null;
        }
        catch (Exception ex)
        {
            if (!quietFail)
                _log.Info($"{label} 读取失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 解析变量命名空间索引：读标准节点 NamespaceArray（ns=0, i=2255，数组下标=命名空间索引）。
    /// 优先找 URI 含 VarNamespaceKeyword 的（默认 SinumerikVarProvider）；
    /// 找不到再退而找任何含 "Sinumerik" 的（跳过 ns=0 标准和 ns=1 服务器实例）；
    /// 还找不到就维持默认 ns=2 并警告（现场用 UaExpert 看命名空间表后改 config.json 的 CncVarNamespace）。
    /// </summary>
    private async Task<ushort> ResolveVarNamespaceAsync(CancellationToken ct)
    {
        const ushort fallback = 2;
        try
        {
            var val = (await _session!.ReadValueAsync(new NodeId(2255), ct)).Value as string[];
            if (val is null || val.Length == 0)
            {
                _log.Warn("[数控] 命名空间数组读取为空，按默认 ns=2 使用");
                return fallback;
            }
            for (int i = 0; i < val.Length; i++)
            {
                if (!string.IsNullOrEmpty(val[i]) &&
                    val[i]!.Contains(VarNamespaceKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Info($"[数控] 变量命名空间：ns={i}（{val[i]}）");
                    return (ushort)i;
                }
            }
            for (int i = 2; i < val.Length; i++)   // ns=1=服务器实例(SinumerikServer)，跳过
            {
                if (!string.IsNullOrEmpty(val[i]) &&
                    val[i]!.Contains("Sinumerik", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Info($"[数控] 变量命名空间（兜底匹配）：ns={i}（{val[i]}）");
                    return (ushort)i;
                }
            }
            _log.Warn($"[数控] 命名空间表未找到 \"{VarNamespaceKeyword}\"，按默认 ns=2 使用。命名空间表：{string.Join(" | ", val)}");
        }
        catch (Exception ex)
        {
            _log.Warn($"[数控] 命名空间表读取失败：{ex.Message}，按默认 ns=2 使用");
        }
        return fallback;
    }

    public async Task DisconnectAsync()
    {
        _poll?.Dispose(); _poll = null;
        if (_session != null)
        {
            var s = _session;
            _session = null;              // 先置空：断开过程中其他线程调用 IsConnected 立即生效
            try { await s.CloseAsync(ct: default); }
            catch { /* 会话已断开，无需处理 */ }
            try { s.Dispose(); }          // 彻底释放底层通道/证书句柄，防止下次连不上
            catch { /* 已释放则忽略 */ }
            _log.Info("[数控] 已断开连接");
        }
    }
#pragma warning restore CS0618

    /// <summary>读一次机床状态（模式/程序/三轴位置）。读取失败时 Message 带原因，不抛异常。</summary>
    public async Task<MachineStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return new MachineStatus(false, "-", "-", 0, 0, 0, "未连接");
        try
        {
            double x = await ReadDoubleAsync(Node(PosNodeX), ct);
            double y = await ReadDoubleAsync(Node(PosNodeY), ct);
            double z = await ReadDoubleAsync(Node(PosNodeZ), ct);
            return new MachineStatus(true, "AUTO", "-", x, y, z, "OK");
        }
        catch (Exception ex)
        {
            _log.Warn($"[数控] 状态读取失败：{ex.Message}");
            return new MachineStatus(true, "?", "-", 0, 0, 0, "读取失败：" + ex.Message);
        }
    }

    /// <summary>写 R 参数（如 R1=25.3）。逐条写并记日志，返回是否成功。</summary>
    public Task<bool> WriteRAsync(int rNumber, double value, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            _log.Warn($"[数控] 写 R{rNumber} 失败：未连接");
            return Task.FromResult(false);
        }
        var wv = new WriteValue
        {
            NodeId = R(rNumber),
            AttributeId = Attributes.Value,
            Value = new DataValue(new Variant(value))
        };
#pragma warning disable CS0618 // 同步 Write 在此版本可用；R 参数单条写，无需异步
        _session!.Write(null, new WriteValueCollection { wv }, out var statuses, out _);
#pragma warning restore CS0618
        bool ok = statuses.Count == 1 && StatusCode.IsGood(statuses[0]);
        if (ok) _log.Info($"[数控] R{rNumber} = {value} 写入成功");
        else _log.Error($"[数控] R{rNumber} = {value} 写入失败（状态码 {statuses.FirstOrDefault()}）");
        return Task.FromResult(ok);
    }

    /// <summary>
    /// 下发刻孔程序。当前走 SMB 网络共享盘（最简可靠），路径由 ProgramSharePath 配置。
    /// TODO：现场也可改用 OPC UA 文件对象传输（828D 支持），当前不启用。
    /// </summary>
    public Task<bool> UploadProgramAsync(string name, string content, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ProgramSharePath))
                throw new InvalidOperationException("未配置程序共享路径（config.json 的 CncProgramShare）");
            string full = Path.Combine(ProgramSharePath, name);
            File.WriteAllText(full, content);
            _log.Info($"[数控] 程序已写入共享盘：{full}（{content.Length} 字符）");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _log.Error($"[数控] 程序下发失败：{ex.Message}（检查共享路径/账号权限）");
            return Task.FromResult(false);
        }
    }

    /// <summary>订阅轴位置（UI 实时显示用）。内部定时轮询读节点，读失败静默跳过本次。</summary>
    public IDisposable SubscribePosition(Action<AxisPosition> onChange, int intervalMs = 200)
    {
        _posChanged += onChange;
        _poll ??= new Timer(async _ =>
        {
            try
            {
                if (!IsConnected) return;
                double x = await ReadDoubleAsync(Node(PosNodeX), default);
                double y = await ReadDoubleAsync(Node(PosNodeY), default);
                double z = await ReadDoubleAsync(Node(PosNodeZ), default);
                _posChanged?.Invoke(new AxisPosition(x, y, z));
            }
            catch { /* 单次读取失败不中断订阅，也不刷屏 */ }
        }, null, intervalMs, intervalMs);
        return new Unsubscriber(() => _posChanged -= onChange);
    }

    /// <summary>读一个节点并转 double（828D 数值节点可能是 Float/Double/Int32，统一转）。</summary>
    private async Task<double> ReadDoubleAsync(NodeId id, CancellationToken ct)
    {
        var dv = await _session!.ReadValueAsync(id, ct);
        if (!StatusCode.IsGood(dv.StatusCode) || dv.Value is null)
            throw new InvalidDataException($"位置节点 {id} 未返回有效数值，状态 {dv.StatusCode}。");
        double value = Convert.ToDouble(dv.Value, System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(value)) throw new InvalidDataException($"位置节点 {id} 返回了非有限值。");
        return value;
    }

    // ==================================================================
    // ISinumerikVarAccess：转台服务复用同一个 OPC UA 会话读写任意节点。
    // 节点路径由 config.json 提供（现场 UaExpert 核实），本类不做任何臆造映射。
    // ==================================================================

    /// <summary>写一个 SINUMERIK 变量（NC 变量或 PLC 位）。value 支持 double / bool / int。</summary>
    public Task<bool> WriteVarAsync(string nodePath, object value, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            _log.Warn($"[数控] 写变量失败（未连接）：{nodePath}");
            return Task.FromResult(false);
        }
        try
        {
            var wv = new WriteValue
            {
                NodeId = Node(nodePath),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(value))
            };
#pragma warning disable CS0618 // 1.5.378 的同步 Write 在此版本可用
            _session!.Write(null, new WriteValueCollection { wv }, out var statuses, out _);
#pragma warning restore CS0618
            bool ok = statuses.Count == 1 && StatusCode.IsGood(statuses[0]);
            if (ok) _log.Info($"[数控] 写变量成功 {nodePath} = {value}");
            else _log.Error($"[数控] 写变量失败 {nodePath}（状态码 {statuses.FirstOrDefault()}）");
            return Task.FromResult(ok);
        }
        catch (Exception ex)
        {
            _log.Error($"[数控] 写变量异常 {nodePath}：{ex.Message}");
            return Task.FromResult(false);
        }
    }

    /// <summary>读一个 SINUMERIK 变量的原始值（调用方自行转换 bool / double）。读不到返回 null。</summary>
    public async Task<object?> ReadVarAsync(string nodePath, CancellationToken ct = default)
    {
        if (!IsConnected) return null;
        try
        {
            var dv = await _session!.ReadValueAsync(Node(nodePath), ct);
            if (!StatusCode.IsGood(dv.StatusCode)) return null;
            return dv.Value;
        }
        catch (Exception ex)
        {
            _log.Warn($"[数控] 读变量失败 {nodePath}：{ex.Message}");
            return null;
        }
    }

    // ==================================================================
    // ISinumerikVarProbe：OPC UA 已联通之后的「第一步」——只读探测
    // 2026-11-06 客户确认网络打通。要清楚：**网络通 ≠ 能自动分度**，还差：
    //   ① R 参数是否可写（GudWrite）② 转台实际角度节点路径
    //   ③ PLC 触发位/到位位节点路径    ④ 机床侧 NC 子程序与 PLC 握手
    // 本方法把 ①②③ 能自动查的都查出来并生成中文报告；④ 只能由机床侧提供。
    // ⚠ 全程只读：读变量值 + 读节点 AccessLevel 属性 + 浏览目录，
    //   绝不调用 Write、绝不写 R 参数、绝不触发任何轴运动。
    // ==================================================================

    /// <summary>转台自动分度前置条件的只读探测（要求已建立会话）。</summary>
    public async Task<OpcUaProbeReport> ProbeRotaryAsync(int targetRNumber, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var candidates = new List<string>();
        sb.AppendLine("SINUMERIK 828D OPC UA 现场只读探测报告（转台自动分度前置条件）");
        sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("端点：" + Endpoint);
        sb.AppendLine("变量命名空间：ns=" + _varNs);
        sb.AppendLine("★ 安全声明：本次探测只读变量值 / 节点属性 / 地址空间目录，未写入任何变量、未触发任何轴运动。");
        sb.AppendLine();

        if (!IsConnected)
        {
            sb.AppendLine("结果：OPC UA 会话未连接，探测中止（检查网线、IP、4840 端口、客户端证书是否被 828D 信任、账号权限）。");
            _log.Warn("[探测] OPC UA 会话未连接，探测中止");
            return new OpcUaProbeReport(false, Endpoint, sb.ToString(), candidates);
        }

        // ---- 1. 命名空间表：决定所有节点路径前缀的 ns 索引 ----
        sb.AppendLine("=== 1. 服务器命名空间表（config.json 的 CncVarNamespace 以此为准）===");
        try
        {
            var raw = (await _session!.ReadValueAsync(new NodeId(2255), ct)).Value as string[];
            if (raw is not null)
                for (int i = 0; i < raw.Length; i++) sb.AppendLine($"  ns={i}: {raw[i]}");
            else sb.AppendLine("  读取为空（账号权限不足？）");
        }
        catch (Exception ex) { sb.AppendLine("  读取失败：" + ex.Message); }
        sb.AppendLine();

        // ---- 2. 目标角 R 参数：存在性 / 当前值 / 当前用户写权限 ----
        string rPath = $"/Channel/Parameter/R[u1, {targetRNumber}]";
        sb.AppendLine($"=== 2. 目标角 R 参数 R{targetRNumber} ===");
        sb.AppendLine("  路径：" + rPath + $"（ns={_varNs}）");
        var rRead = await ReadNodeTextAsync(rPath, ct);
        sb.AppendLine("  读取：" + rRead.Text);
        string access = ReadAccessText(rPath);
        sb.AppendLine("  权限：" + access);
        bool rWritable = access.Contains("当前用户可写", StringComparison.Ordinal);
        sb.AppendLine(rWritable
            ? "  ✔ 当前用户对该 R 参数有写权限 → 目标角可用标准路径写入，RotaryTargetRNode 留空即可。"
            : "  ✘ 当前用户不可写 → HMI【调试→网络→OPC UA→设置】给该用户加 StateWrite/GudWrite 权限；或请机床厂换传值方式。");
        if (!rRead.Ok)
            sb.AppendLine("  ⚠ 该 R 参数节点没读到：请用 UaExpert 展开 /Channel/Parameter，把实际节点路径填到 RotaryTargetRNode。");
        sb.AppendLine();

        // ---- 3. 轴实际位置候选：找转台角度反馈节点 ----
        sb.AppendLine("=== 3. 轴实际位置候选节点（找转台角度反馈）===");
        sb.AppendLine("  判据：OPC UA 能读到且值为数值。请与 HMI 上 C/A 轴实际角度对照，挑数值一致的那条填 RotaryActualAngleNode。");
        string[] dirs = { "/Channel/GeometricAxis", "/Channel/MachineAxis" };
        string[] vars = { "actToolBasePos", "aaPos", "aaIm", "actPos" };
        string[] axes = { "[u1, 1]", "[u1, 2]", "[u1, 3]", "[u1, 4]", "[u1, 5]",
                          "[u1, 6]", "[u1, C]", "[u1, A]", "[u1, B]", "[u1, SP]" };
        int hit = 0, tried = 0;
        foreach (var dir in dirs)
            foreach (var v in vars)
                foreach (var ax in axes)
                {
                    if (ct.IsCancellationRequested) break;
                    string p = $"{dir}/{v}{ax}";
                    tried++;
                    var rr = await ReadNodeTextAsync(p, ct);
                    if (!rr.Ok) continue;
                    candidates.Add(p);
                    hit++;
                    sb.AppendLine($"  命中 {p} = {rr.Text}");
                }
        sb.AppendLine($"  扫描 {tried} 个候选，命中 {hit} 个。⚠ 命中≠就是转台轴：X/Y/Z 线性轴也会命中，务必对照 HMI 数值。");
        sb.AppendLine();

        // ---- 3b. 轴名 / 轴类型识别（2026-11-06 现场报告复盘后新增）----
        // 只列 18 个数值无法判断哪个索引是回转轴，现场报告只能写"请对照 HMI"。
        // 但人不在现场时 HMI 也看不了，所以这里尽量把轴名/类型节点一起读出来，
        // 读得到就能远程认出转台轴；读不到就明确写"仍需 HMI 对照"，绝不臆造索引→轴名映射。
        // type / spec / displayAxis / index 这几个名字在本机 828D 的轴目录里是真实存在的
        // （见第 4 节目录列表），axisName / axName 只是常见写法候选，读不到属正常。
        sb.AppendLine("=== 3b. 轴名 / 轴类型识别（用来远程认出哪个索引是回转轴）===");
        string[] metaVars = { "type", "spec", "displayAxis", "index", "axisName", "axName" };
        int metaHit = 0;
        foreach (var dir in dirs)
            foreach (var v in metaVars)
                for (int n = 1; n <= 6; n++)
                {
                    if (ct.IsCancellationRequested) break;
                    string p = $"{dir}/{v}[u1, {n}]";
                    var rr = await ReadNodeTextAsync(p, ct);
                    if (!rr.Ok) continue;
                    sb.AppendLine($"  命中 {p} = {rr.Text}");
                    metaHit++;
                }
        if (metaHit == 0)
            sb.AppendLine("  未读到轴名/类型节点 → 仍需现场对照 HMI，或在 MDA 敲 G0 C10 看哪个索引变化，才能确认转台轴。");
        else
            sb.AppendLine($"  共命中 {metaHit} 条。⚠ type/spec 的枚举含义由机床厂定义，不要凭数值猜；以 HMI 轴名为准。");
        sb.AppendLine();

        // ---- 4. 目录浏览：列出真实子节点名，用于拼 config 节点路径 ----
        sb.AppendLine("=== 4. 地址空间目录浏览（真实子节点名，用来拼 config 里的节点路径）===");
        foreach (var dir in new[] { "/Channel", "/Channel/Parameter", "/Channel/GeometricAxis",
                                   "/Channel/MachineAxis",
                                   // 2026-11-06 现场报告实测：PLC 区的真实前缀是 /Plc（不是 /PLC），
                                   // 下面几个子目录展开后能看到真实的位/DB 变量，供机床厂对号入座。
                                   "/Plc", "/Plc/M", "/Plc/MB", "/Plc/DB9000",
                                   "/PLC", "/Channel/PLC" })
        {
            var names = BrowseChildNames(dir);
            if (names.Count == 0) { sb.AppendLine($"  {dir}：浏览不到（节点不存在或权限不足）"); continue; }
            sb.AppendLine($"  {dir}：{names.Count} 个子节点");
            for (int i = 0; i < Math.Min(names.Count, 60); i++) sb.AppendLine("    " + names[i]);
            if (names.Count > 60) sb.AppendLine($"    …（其余 {names.Count - 60} 个省略，用 UaExpert 展开看全）");
        }
        sb.AppendLine("  ✔ 2026-11-06 实测：PLC 变量区真实前缀是 /Plc（大写 P + 小写 lc），可浏览；/PLC 不存在。");
        sb.AppendLine("    该区含 I/IB/IW/ID、Q/QB/QW/QD、M/MB/MW/MD 及 DB9000~DB9905 等数据块 —— 位空间其实能访问。");
        sb.AppendLine("  ⚠ 但「目录能浏览」≠「知道哪一位是触发/到位位」。具体 DB 地址（如 DB9xxx.DBXx.y）");
        sb.AppendLine("    必须由机床厂给出，再用 UaExpert 核实读写权限后填 RotaryTriggerNode / RotaryDoneNode。程序绝不臆造。");
        sb.AppendLine();

        // ---- 5. 结论：把「网络已通」之后还差什么一次讲清 ----
        sb.AppendLine("=== 5. 结论 / 下一步（把本报告发回开发侧即可定死节点路径）===");
        sb.AppendLine("  ① OPC UA 网络：已联通 ✔（本次会话建立成功，服务器地址空间可读）。");
        sb.AppendLine($"  ② 目标角 R 参数：{(rRead.Ok ? "节点存在 ✔" : "节点未读到 ✘")}，当前用户{(rWritable ? "可写 ✔" : "不可写 ✘")}。");
        sb.AppendLine($"  ③ 转台实际角度：{(candidates.Count > 0 ? $"有 {candidates.Count} 个数值候选，看第 3b 节能否远程认出回转轴" : "没扫到候选，用 UaExpert 展开轴目录")}。");
        sb.AppendLine("  ④ PLC 触发位 / 到位位：程序绝不臆造 NodeId，需机床厂给 DB 地址，UaExpert 核实后填 RotaryTriggerNode / RotaryDoneNode。");
        sb.AppendLine("  ⑤ 机床侧还要：预置 NC 子程序（读 R90 → G0 C=R90 / POS[C]=R90）+ PLC 握手 + 安全联锁（门/夹紧/急停）。");
        sb.AppendLine("  ★ 2026-11-06 17:38 现场实测已确认：ns=2（SinumerikVarProvider）；R90=/Channel/Parameter/R[u1, 90] 可读、");
        sb.AppendLine("    当前值 0、AccessLevel/UserAccessLevel=0x03（当前用户可写）；PLC 区前缀 /Plc 可浏览。");
        sb.AppendLine("    ⚠ **R 参数可写 ≠ 转台能自动转**：现在没有任何节点会去执行运动，写 R90 只是改了一个数字。");
        sb.AppendLine("      绝对不要因为「可写」就把 DeviceMode/RotaryMode 切成 Real/OpcUaRPlc —— 缺 ④⑤ 时触发位为空，程序会明确拒绝动作。");
        sb.AppendLine("      也绝不能试图写轴位置变量或直接启动程序来驱动轴：828D 轴位置只读，且那样会绕过机床 PLC 的安全联锁。");
        sb.AppendLine("  ⑥ 在 ②~⑤ 齐备前：DeviceMode 保持 Sim、RotaryMode 保持 ManualEntry（人工抄角照样能采多帧，不耽误视觉验证）。");
        sb.AppendLine("     齐备后改 DeviceMode=Real、RotaryMode=OpcUaRPlc，先用 5° 小角度空跑验证握手，再上工件。");

        _log.Info($"[探测] 只读探测完成：R 参数读得到={rRead.Ok}，当前用户可写={rWritable}，轴角度候选 {candidates.Count} 条");
        return new OpcUaProbeReport(true, Endpoint, sb.ToString(), candidates);
    }

    /// <summary>读一个变量节点，返回「是否读成功 + 人话结果」。失败原因进报告，方便现场排错。</summary>
    private async Task<(bool Ok, string Text)> ReadNodeTextAsync(string path, CancellationToken ct)
    {
        try
        {
            var dv = await _session!.ReadValueAsync(Node(path), ct);
            if (!StatusCode.IsGood(dv.StatusCode))
                return (false, $"状态码 {dv.StatusCode}（节点不存在 / 无读权限 / 索引写法不对）");
            if (dv.Value is null) return (false, "值为空");
            return (true, $"{dv.Value}（{dv.Value.GetType().Name}）");
        }
        catch (Exception ex) { return (false, "读取异常：" + ex.Message); }
    }

    /// <summary>
    /// 读节点的 AccessLevel / UserAccessLevel 属性（**只读属性，不是写变量**），
    /// 用来判断当前 OPC UA 账号有没有写 R 参数 / PLC 位的权限。
    /// 位定义：0x01=CurrentRead，0x02=CurrentWrite。
    /// </summary>
    private string ReadAccessText(string path)
    {
        try
        {
            var id = Node(path);
            var toRead = new ReadValueIdCollection
            {
                new ReadValueId { NodeId = id, AttributeId = Attributes.AccessLevel },
                new ReadValueId { NodeId = id, AttributeId = Attributes.UserAccessLevel }
            };
#pragma warning disable CS0618 // 1.5.378 的同步 Read 在此版本可用
            _session!.Read(null, 0, TimestampsToReturn.Neither, toRead,
                           out DataValueCollection results, out _);
#pragma warning restore CS0618
            if (results.Count < 2) return "属性读取结果不完整";
            byte a = Convert.ToByte(results[0].Value);
            byte u = Convert.ToByte(results[1].Value);
            string wa = (a & 0x02) != 0 ? "可写" : "只读";
            string wu = (u & 0x02) != 0 ? "当前用户可写" : "当前用户不可写";
            return $"AccessLevel=0x{a:X2}（{wa}），UserAccessLevel=0x{u:X2}（{wu}）";
        }
        catch (Exception ex) { return "权限属性读取失败：" + ex.Message; }
    }

    /// <summary>浏览一个目录的子节点，返回「显示名 | 节点标识符」列表，供现场拼节点路径。</summary>
    private List<string> BrowseChildNames(string dirPath)
    {
        var list = new List<string>();
        try
        {
#pragma warning disable CS0618 // Browser.Browse(同步) 在此版本仍可用，内部自动处理续页
            var browser = new Browser(_session!)
            {
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = 0,
                ResultMask = (uint)BrowseResultMask.All
            };
            foreach (var r in browser.Browse(new NodeId(dirPath, _varNs)))
                list.Add($"{r.DisplayName?.Text ?? "?"} | {r.NodeId?.Identifier}");
#pragma warning restore CS0618
        }
        catch { /* 目录不存在/无浏览权限：返回空表，由调用方在报告里标注 */ }
        return list;
    }

    public void Dispose() => DisconnectAsync().GetAwaiter().GetResult();

    /// <summary>取消订阅的句柄（SubscribePosition 返回值 Dispose 即退订）。</summary>
    private sealed class Unsubscriber(Action act) : IDisposable
    {
        public void Dispose() => act();
    }

    /// <summary>静默日志器：调用方不传 logger 时用（单元测试/SmokeTest）。</summary>
    private sealed class SilentLogger : IAppLogger
    {
        public void Info(string m) { }
        public void Warn(string m) { }
        public void Error(string m) { }
#pragma warning disable CS0067
        public event Action<string, string>? LineAdded;
#pragma warning restore CS0067
    }
}
