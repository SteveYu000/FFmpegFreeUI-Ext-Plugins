using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoEnhancer;

internal sealed class ModelRepositoryClient
{
    private const int ModelScopeTreePageSize = 500;
    private readonly string _dataset;
    private readonly string? _token;
    private readonly string _toolVersion;
    internal string ResolveRoot => "https://www.modelscope.cn/datasets/" + _dataset + "/resolve/master/";
    private string TreeApi(int pageNumber) => "https://www.modelscope.cn/api/v1/datasets/" + _dataset +
        "/repo/tree?Revision=master&Recursive=true&PageNumber=" + pageNumber + "&PageSize=" + ModelScopeTreePageSize;
    internal ModelRepositoryClient(string dataset, string? token, string toolVersion)
    {
        _dataset = dataset;
        _token = token;
        _toolVersion = toolVersion;
    }

    internal sealed record RemoteModel(string Name, string Path, long Size, string Sha256);

    private static void KeepLatestVersionedArchive(
        List<RemoteModel> models,
        string versionedPathPattern)
    {
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var latest = models
            .Select(model => (Model: model, Match: Regex.Match(model.Path, versionedPathPattern, options)))
            .Where(candidate => candidate.Match.Success)
            .OrderByDescending(
                candidate => candidate.Match.Groups["version"].Value,
                StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(candidate => candidate.Model.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Model)
            .FirstOrDefault();
        if (latest is null) return;

        models.RemoveAll(model =>
            Regex.IsMatch(model.Path, versionedPathPattern, options)
            && !model.Path.Equals(latest.Path, StringComparison.OrdinalIgnoreCase));
    }

    internal List<RemoteModel> FetchRemoteModels()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        ApplyModelScopeAuthentication(client);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VideoEnhancer/" + _toolVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var result = new List<RemoteModel>();
        var allowedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Backend", "BasicVSR++", "Bin", "FlashVSR", "Frame-Interpolation", "ONNX", "Param-Bin", "PTH" };
        var fetchedEntries = 0;
        for (var pageNumber = 1; ; pageNumber++)
        {
            var json = client.GetStringAsync(TreeApi(pageNumber), DownloadCancellation.Token).GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);
            var rootElement = document.RootElement;
            var files = rootElement.GetProperty("Data").GetProperty("Files");
            var returnedEntries = files.GetArrayLength();
            fetchedEntries += returnedEntries;

            foreach (var file in files.EnumerateArray())
            {
                if (!string.Equals(file.GetProperty("Type").GetString(), "blob", StringComparison.OrdinalIgnoreCase)) continue;
                var path = file.GetProperty("Path").GetString()?.Replace('\\', '/').TrimStart('/') ?? "";
                if (path.Length == 0 || path.EndsWith("/.gitkeep", StringComparison.OrdinalIgnoreCase)) continue;
                var slash = path.IndexOf('/');
                var root = slash < 0 ? path : path[..slash];
                if (!allowedRoots.Contains(root)) continue;
                if (root.Equals("Backend", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(path, @"^Backend/python_\d{8}\.7z$", RegexOptions.IgnoreCase)) continue;
                result.Add(new RemoteModel(
                    file.GetProperty("Name").GetString() ?? System.IO.Path.GetFileName(path),
                    path,
                    file.TryGetProperty("Size", out var size) ? size.GetInt64() : 0,
                    file.TryGetProperty("Sha256", out var hash) ? hash.GetString() ?? "" : ""));
            }

            var totalCount = rootElement.TryGetProperty("TotalCount", out var total)
                && total.TryGetInt32(out var parsedTotal)
                    ? parsedTotal
                    : -1;
            // ModelScope 根据总数翻页；响应缺少总数时，
            // 以实际返回数小于请求页大小作为结束条件。
            if (returnedEntries == 0
                || (totalCount >= 0 && fetchedEntries >= totalCount)
                || (totalCount < 0 && returnedEntries < ModelScopeTreePageSize))
            {
                break;
            }
        }
        // 同类运行包只展示最新版本；模型权重使用稳定路径。
        KeepLatestVersionedArchive(result, @"^Backend/python_(?<version>\d{8})\.7z$");
        KeepLatestVersionedArchive(result, @"^Bin/rtx-video/RTXVideoRuntime_(?<version>\d{8})\.7z$");

        return result.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static bool IsNetworkFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or TaskCanceledException or TimeoutException)
                return true;
        }
        return false;
    }

    internal static bool IsAuthenticationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException request
                && request.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return true;
        }
        return false;
    }

    internal void ApplyModelScopeAuthentication(HttpClient client)
    {
        var token = _token;
        if (token is null) return;
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cookie", "m_session_id=" + token + "; modelscope_session=" + token);
    }

    internal static void WriteRemoteFailure(string operation, Exception exception)
    {
        if (IsAuthenticationFailure(exception))
            Console.Error.WriteLine("AUTH_REQUIRED|ModelScope 私有仓库需要有效令牌；请设置 VIDEOENHANCER_MODELSCOPE_TOKEN 或 MODELSCOPE_API_TOKEN");
        else if (IsNetworkFailure(exception))
            Console.Error.WriteLine("NO_NETWORK|无法连接 ModelScope");
        else
            Console.Error.WriteLine("REMOTE_ERROR|ModelScope 返回的数据无法解析");
        Console.Error.WriteLine($"[错误] {operation}：{exception.Message}");
    }

}
