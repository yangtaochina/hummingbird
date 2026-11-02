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
public sealed class OpcUaCncService : ICncService
{
    /// <summary>程序下发用的网络共享路径（如 \\192.168.214.1\nc），由 App 配置注入。</summary>
    public string? ProgramSharePath { get; set; }
    /// <summary>变量命名空间匹配关键字（连接时在命名空间表里找包含它的 URI）。</summary>
    public string VarNamespaceKeyword { get; set; } = "SinumerikVarProvider";
    /// <summary>轴位置节点整路径（不带 ns；默认 actToolBasePos——该 828D 无 aaPos，2026-11-02 现场核实）。</summary>
    public string PosNodeX { get; set; } = "/Channel/GeometricAxis/actToolBasePos[u1, X]";
    public string PosNodeY { get; set; } = "/Channel/GeometricAxis/actToolBasePos[u1, Y]";
    public string PosNodeZ { get; set; } = "/Channel/GeometricAxis/actToolBasePos[u1, Z1]";

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
        var epDesc = CoreClientUtils.SelectEndpoint(_config, endpoint, false, 15000);
        _log.Info($"[数控] 端点确认：{epDesc.EndpointUrl} 安全策略={epDesc.SecurityPolicyUri}");

        _log.Info("[数控] 连接步骤4/4：建立会话（不通=在828D上信任客户端证书）");
        var configured = new ConfiguredEndpoint(null, epDesc, EndpointConfiguration.Create(_config));
        _session = await Session.CreateAsync(_config, (ReverseConnectManager?)null, configured, false, false, "Hummingbird",
            60000, new UserIdentity(new AnonymousIdentityToken()), null, ct);

        bool ok = IsConnected;
        if (ok)
        {
            _varNs = await ResolveVarNamespaceAsync(ct);
            _log.Info($"[数控] 会话建立成功（变量命名空间 ns={_varNs}）");
            await LogDiagnosticsAsync(ct);   // 现场联调：打全候选节点可读性日志（只读不写）
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
            string[] axes = { "[u1, X]", "[u1, X1]", "[u1, 1]", "" };   // ""=不带索引(默认索引)
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
        }
        catch (Exception ex)
        {
            _log.Warn($"[数控][诊断] 中断：{ex.Message}");
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
            try { await _session.CloseAsync(ct: default); }
            catch { /* 会话已断开，无需处理 */ }
            _session = null;
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
        return Convert.ToDouble(dv.Value, System.Globalization.CultureInfo.InvariantCulture);
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
