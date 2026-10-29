using Hummingbird.Core;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Hummingbird.Devices.OpcUa;

/// <summary>
/// SINUMERIK 828D 数控服务（OPC UA，端口4840，需 Access MyMachine /OPC UA 授权）。
/// 节点映射（ns=3，Siemens AMM 标准）——现场需用 UaExpert 核对后微调节点表。
/// </summary>
public sealed class OpcUaCncService : ICncService
{
    /// <summary>程序下发用的网络共享路径（如 \\192.168.214.1\nc），由 App 配置注入。</summary>
    public string? ProgramSharePath { get; set; }

    private Session? _session;
    private ApplicationConfiguration _config = null!;
    private Timer? _poll;
    private event Action<AxisPosition>? _posChanged;

    public string Endpoint { get; private set; } = "";
    public bool IsConnected => _session is { Connected: true };

    // ---- 节点映射（现场核对 TODO）----
    private static NodeId R(int n) => new($"/Channel/Parameter/R[u1, {n}]", 3);
    private static readonly NodeId NodeX = new("/Channel/MachineAxis/aaPos[u1, X]", 3);
    private static readonly NodeId NodeY = new("/Channel/MachineAxis/aaPos[u1, Y]", 3);
    private static readonly NodeId NodeZ = new("/Channel/MachineAxis/aaPos[u1, Z1]", 3);

#pragma warning disable CS0618 // Validate/CreateAsync 系列新旧 API 混用，先按 1.5.378 可用形态实现
    public async Task<bool> ConnectAsync(string endpoint, CancellationToken ct = default)
    {
        Endpoint = endpoint;
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
                AutoAcceptUntrustedCertificates = true
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
        };
        await _config.Validate(ApplicationType.Client);

        var app = new ApplicationInstance
        { ApplicationName = "Hummingbird", ApplicationType = ApplicationType.Client, ApplicationConfiguration = _config };
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct);

        var epDesc = CoreClientUtils.SelectEndpoint(_config, endpoint, false, 15000);
        var configured = new ConfiguredEndpoint(null, epDesc, EndpointConfiguration.Create(_config));
        _session = await Session.CreateAsync(_config, (ReverseConnectManager?)null, configured, false, false, "Hummingbird",
            60000, new UserIdentity(new AnonymousIdentityToken()), null, ct);
        return IsConnected;
    }

    public async Task DisconnectAsync()
    {
        _poll?.Dispose(); _poll = null;
        if (_session != null)
        {
            try { await _session.CloseAsync(ct: default); } catch { /* 会话已断 */ }
            _session = null;
        }
    }
#pragma warning restore CS0618

    public async Task<MachineStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return new MachineStatus(false, "-", "-", 0, 0, 0, "未连接");
        try
        {
            double x = await ReadDoubleAsync(NodeX, ct);
            double y = await ReadDoubleAsync(NodeY, ct);
            double z = await ReadDoubleAsync(NodeZ, ct);
            return new MachineStatus(true, "AUTO", "-", x, y, z, "OK");
        }
        catch (Exception ex)
        {
            return new MachineStatus(true, "?", "-", 0, 0, 0, "读取失败：" + ex.Message);
        }
    }

    public Task<bool> WriteRAsync(int rNumber, double value, CancellationToken ct = default)
    {
        if (!IsConnected) return Task.FromResult(false);
        var wv = new WriteValue
        {
            NodeId = R(rNumber),
            AttributeId = Attributes.Value,
            Value = new DataValue(new Variant(value))
        };
        _session!.Write(null, new WriteValueCollection { wv }, out var statuses, out _);
        return Task.FromResult(statuses.Count == 1 && StatusCode.IsGood(statuses[0]));
    }

    public Task<bool> UploadProgramAsync(string name, string content, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ProgramSharePath))
                throw new InvalidOperationException("未配置程序共享路径（CncProgramShare）");
            // TODO：现场也可改用 OPC UA 文件对象传输（828D 支持），当前走 SMB 共享盘最简
            File.WriteAllText(Path.Combine(ProgramSharePath, name), content);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public IDisposable SubscribePosition(Action<AxisPosition> onChange, int intervalMs = 200)
    {
        _posChanged += onChange;
        _poll ??= new Timer(async _ =>
        {
            try
            {
                if (!IsConnected) return;
                double x = await ReadDoubleAsync(NodeX, default);
                double y = await ReadDoubleAsync(NodeY, default);
                double z = await ReadDoubleAsync(NodeZ, default);
                _posChanged?.Invoke(new AxisPosition(x, y, z));
            }
            catch { /* 读取失败不中断订阅 */ }
        }, null, intervalMs, intervalMs);
        return new Unsubscriber(() => _posChanged -= onChange);
    }

    private async Task<double> ReadDoubleAsync(NodeId id, CancellationToken ct)
    {
        var dv = await _session!.ReadValueAsync(id, ct);
        return Convert.ToDouble(dv.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose() => DisconnectAsync().GetAwaiter().GetResult();

    private sealed class Unsubscriber(Action act) : IDisposable
    {
        public void Dispose() => act();
    }
}
