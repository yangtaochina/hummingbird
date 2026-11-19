using Opc.Ua;
using Opc.Ua.Client;

namespace Hummingbird.Devices.OpcUa;

public sealed partial class OpcUaCncService
{
    public async Task<IReadOnlyList<string>> BrowseWorkpieceFilesAsync(string workpiece, CancellationToken ct = default)
    {
        var session = RequireFileSession();
        var files = await BrowseWorkpieceProgramNodesAsync(session, workpiece, ct);
        return files.Select(file => file.Node.Identifier as string ?? file.Name).ToArray();
    }

    public async Task<(string Name, byte[] Data)> DownloadWorkpieceProgramAsync(string workpiece, string program, CancellationToken ct = default)
    {
        var session = RequireFileSession();
        var files = await BrowseWorkpieceProgramNodesAsync(session, workpiece, ct);
        var matches = files.Where(file => string.Equals(Path.GetFileNameWithoutExtension(file.Name), program,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"工件 {workpiece} 中找到 {matches.Length} 个 {program}。可见程序：{string.Join(", ", files.Select(file => file.Name))}");

        var file = matches[0];
        _log.Info($"[NC 文件] 实际节点 {file.Node}");
        var methods = await BrowseFileChildrenAsync(session, file.Node, ct);
        var open = FindMethod(methods, "Open");
        var read = FindMethod(methods, "Read");
        var close = FindMethod(methods, "Close");
        if (open is null || read is null || close is null)
        {
            if (file.Node.Identifier is string path && path.StartsWith("Sinumerik/FileSystem/", StringComparison.OrdinalIgnoreCase))
                return (file.Name, await DownloadNcFileAsync(path, ct));
            throw new InvalidOperationException($"文件节点 {file.Node} 未提供可用的读取方法。");
        }

        var opened = await CallNcMethodAsync(session, file.Node, open, [new Variant((byte)1)], ct);
        if (opened.OutputArguments is not { Count: 1 } handles || handles[0].Value is not uint handle)
            throw new InvalidDataException("机床未返回有效的 NC 文件句柄。");

        using var buffer = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await CallNcMethodAsync(session, file.Node, read, [new Variant(handle), new Variant(16384)], ct);
                if (result.OutputArguments is not { Count: 1 } output || output[0].Value is not byte[] bytes)
                    throw new InvalidDataException("机床未返回有效的 NC 文件数据。");
                if (bytes.Length == 0) break;
                if (buffer.Length + bytes.Length > 16 * 1024 * 1024)
                    throw new InvalidDataException("NC 文件超过 16 MB。");
                buffer.Write(bytes);
            }
        }
        finally
        {
            try { await CallNcMethodAsync(session, file.Node, close, [new Variant(handle)], CancellationToken.None); }
            catch (Exception ex) { _log.Warn("[NC 文件] 关闭读取句柄失败：" + ex.Message); }
        }
        return (file.Name, buffer.ToArray());
    }

    private static async Task<(string Name, NodeId Node)[]> BrowseWorkpieceProgramNodesAsync(
        Session session, string workpiece, CancellationToken ct)
    {
        var sinumerik = await FindChildAsync(session, ObjectIds.ObjectsFolder, "Sinumerik", ct);
        var fileSystem = await FindChildAsync(session, sinumerik, "FileSystem", ct);
        var workPieces = await FindChildAsync(session, fileSystem, "Work Pieces", ct);
        var folders = await BrowseFileChildrenAsync(session, workPieces, ct);
        var folder = folders.FirstOrDefault(r => r.NodeClass == NodeClass.Object &&
            (HasName(r, workpiece + ".WPD") || HasName(r, workpiece) || HasName(r, $"_N_{workpiece}_WPD")));
        if (folder is null)
            throw new InvalidOperationException($"NC 工作区找不到 {workpiece}。可见工件：{string.Join(", ", folders.Select(r => r.BrowseName.Name))}");

        var folderId = LocalNode(session, folder);
        return (await BrowseFileChildrenAsync(session, folderId, ct))
            .Where(r => r.NodeClass == NodeClass.Object)
            .Select(r => (Name: ProgramFileName(r), Node: LocalNode(session, r)))
            .Where(file => file.Name is not null)
            .Select(file => (file.Name!, file.Node))
            .ToArray();
    }

    private static string? ProgramFileName(ReferenceDescription reference)
    {
        foreach (string? label in new[] { reference.NodeId.Identifier as string, reference.BrowseName.Name, reference.DisplayName?.Text })
        {
            string? name = Path.GetFileName(label);
            if (name is null) continue;
            if (name.StartsWith("_N_", StringComparison.OrdinalIgnoreCase) && name.Length > 7 &&
                (name.EndsWith("_MPF", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_SPF", StringComparison.OrdinalIgnoreCase)))
                return name[3..^4] + "." + name[^3..];
            if (name.EndsWith(".MPF", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".SPF", StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }

    public async Task<byte[]> DownloadNcFileAsync(string sourcePath, CancellationToken ct = default)
    {
        var result = await CallFileMethodAsync("CopyFileFromServer", [new Variant(sourcePath)], ct);
        return result.OutputArguments is { Count: 1 } output && output[0].Value is byte[] { Length: > 0 } data
            ? data
            : throw new InvalidDataException("机床未返回非空 NC 文件数据。");
    }

    public async Task UploadNewNcFileAsync(string targetPath, byte[] data, CancellationToken ct = default)
    {
        // 西门子 CopyFileToServer 的第三个参数为 Overwrite。始终由服务端拒绝覆盖。
        await CallFileMethodAsync("CopyFileToServer", [new Variant(targetPath), new Variant(data), new Variant(false)], ct);
    }

    private Session RequireFileSession()
        => _session is { Connected: true } session ? session : throw new InvalidOperationException("OPC UA 未连接。");

    private async Task<CallMethodResult> CallFileMethodAsync(string name, VariantCollection arguments, CancellationToken ct)
    {
        var session = RequireFileSession();
        var sinumerik = await FindChildAsync(session, ObjectIds.ObjectsFolder, "Sinumerik", ct);
        var methods = await FindChildAsync(session, sinumerik, "Methods", ct);
        var children = await BrowseFileChildrenAsync(session, methods, ct);
        var parent = methods;
        var method = FindMethod(children, name);
        if (method is null && children.FirstOrDefault(r => HasName(r, "Filehandling")) is { } fileHandling)
        {
            parent = LocalNode(session, fileHandling);
            method = FindMethod(await BrowseFileChildrenAsync(session, parent, ct), name);
        }
        if (method is null)
            throw new InvalidOperationException($"当前 OPC UA 会话看不到 {name}，请检查该机床的文件功能和账号权限。");

        return await CallNcMethodAsync(session, parent, method, arguments, ct);
    }

    private static ReferenceDescription? FindMethod(ReferenceDescriptionCollection children, string name)
        => children.FirstOrDefault(r => r.NodeClass == NodeClass.Method && HasName(r, name));

    private static async Task<CallMethodResult> CallNcMethodAsync(Session session, NodeId parent,
        ReferenceDescription method, VariantCollection arguments, CancellationToken ct)
    {
        var response = await session.CallAsync(null, [new CallMethodRequest
        {
            ObjectId = parent, MethodId = LocalNode(session, method), InputArguments = arguments
        }], ct);
        var result = response.Results.Single();
        if (!StatusCode.IsGood(result.StatusCode))
            throw new InvalidOperationException($"{method.BrowseName.Name} 返回 {result.StatusCode}。");
        return result;
    }

    private static ValueTask<ReferenceDescriptionCollection> BrowseFileChildrenAsync(Session session, NodeId node, CancellationToken ct)
        => new Browser(session)
        {
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            ResultMask = (uint)BrowseResultMask.All
        }.BrowseAsync(node, ct);

    private static async Task<NodeId> FindChildAsync(Session session, NodeId parent, string name, CancellationToken ct)
    {
        var child = (await BrowseFileChildrenAsync(session, parent, ct)).FirstOrDefault(r => HasName(r, name));
        return child is null
            ? throw new InvalidOperationException($"OPC UA 地址空间中找不到 {name}（父节点 {parent}）。")
            : LocalNode(session, child);
    }

    private static bool HasName(ReferenceDescription reference, string name)
        => string.Equals(reference.BrowseName.Name, name, StringComparison.OrdinalIgnoreCase)
           || string.Equals(reference.DisplayName?.Text, name, StringComparison.OrdinalIgnoreCase)
           || string.Equals(Path.GetFileName(reference.NodeId.Identifier as string), name, StringComparison.OrdinalIgnoreCase);

    private static NodeId LocalNode(Session session, ReferenceDescription reference)
        => ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris)
           ?? throw new InvalidOperationException($"无法解析 OPC UA 节点 {reference.NodeId}。");
}
