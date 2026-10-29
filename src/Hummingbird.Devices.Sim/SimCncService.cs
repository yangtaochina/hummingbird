using System.Globalization;
using System.Text.RegularExpressions;
using Hummingbird.Core;

namespace Hummingbird.Devices.Sim;

/// <summary>
/// 模拟 828D：解析下发的 SPF 中的 "G0 X=.. Y=.." 定位点序列，
/// 模拟轴运动（每孔 1.2s 移动 + 0.8s 停顿），通过 SubscribePosition 上报。
/// StartSimulatedRun() = 模拟“操作工按下了 NC 启动”。
/// </summary>
public sealed class SimCncService : ICncService
{
    private static readonly Regex G0 = new(@"G0\s+X=(-?[\d.]+)\s+Y=(-?[\d.]+)", RegexOptions.Compiled);

    private readonly Action<string> _trace;
    private readonly List<(double X, double Y)> _targets = new();
    private readonly System.Threading.Timer? _timer;
    private event Action<AxisPosition>? _posChanged;

    private AxisPosition _pos = new(0, 0, 0);
    private int _idx = -1;
    private DateTime _segStart;
    private (double X, double Y) _segFrom;

    public SimCncService(Action<string>? trace = null)
    {
        _trace = trace ?? (_ => { });
        _timer = new System.Threading.Timer(_ => Tick(), null, 50, 50);
    }

    public string Endpoint { get; private set; } = "sim://local";
    public bool IsConnected { get; private set; }
    public string? CurrentProgram { get; private set; }
    public IReadOnlyList<(double X, double Y)> Targets => _targets;

    public Task<bool> ConnectAsync(string endpoint, CancellationToken ct = default)
    { Endpoint = endpoint; IsConnected = true; _trace("数控已连接（模拟）"); return Task.FromResult(true); }

    public Task DisconnectAsync() { IsConnected = false; StopRun(); return Task.CompletedTask; }

    public Task<MachineStatus> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new MachineStatus(IsConnected, "AUTO", CurrentProgram ?? "-",
            _pos.X, _pos.Y, _pos.Z, IsConnected ? "运行中" : "未连接"));

    public Task<bool> WriteRAsync(int r, double v, CancellationToken ct = default)
    { _trace($"写 R{r} = {v}"); return Task.FromResult(true); }

    public Task<bool> UploadProgramAsync(string name, string content, CancellationToken ct = default)
    {
        CurrentProgram = name;
        _targets.Clear();
        foreach (Match m in G0.Matches(content))
            _targets.Add((double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                          double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
        _trace($"程序上传：{name}，解析到 {_targets.Count} 个定位点");
        return Task.FromResult(true);
    }

    public IDisposable SubscribePosition(Action<AxisPosition> onChange, int intervalMs = 200)
    {
        _posChanged += onChange;
        return new Unsubscriber(() => _posChanged -= onChange);
    }

    /// <summary>模拟“操作工按下 NC 启动”：开始依次走位。</summary>
    public void StartSimulatedRun()
    {
        if (_targets.Count == 0) throw new InvalidOperationException("没有已下发的程序，先执行“下发程序”");
        _idx = 0; _segStart = DateTime.UtcNow; _segFrom = (_pos.X, _pos.Y);
        _trace($"模拟 NC 启动：开始走 {_targets.Count} 个定位点");
    }

    public void StopRun() => _idx = -1;

    private void Tick()
    {
        if (_idx < 0 || _idx >= _targets.Count) return;
        var (tx, ty) = _targets[_idx];
        const double moveMs = 1200, dwellMs = 800;
        double t = (DateTime.UtcNow - _segStart).TotalMilliseconds;
        if (t < moveMs)
        {
            double k = t / moveMs;
            _pos = _pos with { X = _segFrom.X + (tx - _segFrom.X) * k, Y = _segFrom.Y + (ty - _segFrom.Y) * k };
        }
        else if (t < moveMs + dwellMs)
        {
            _pos = _pos with { X = tx, Y = ty };
        }
        else
        {
            _segFrom = (tx, ty); _segStart = DateTime.UtcNow; _idx++;
            if (_idx >= _targets.Count) { _trace("模拟 NC：程序执行完毕（M2）"); _idx = -1; }
        }
        _posChanged?.Invoke(_pos);
    }

    public void Dispose() { _timer?.Dispose(); _posChanged = null; }

    private sealed class Unsubscriber(Action act) : IDisposable
    {
        public void Dispose() => act();
    }
}
