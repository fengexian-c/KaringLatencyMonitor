using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using KaringLatencyMonitor.Core.Models;

namespace KaringLatencyMonitor.Core.Services;

public sealed class KaringApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public KaringApiClient(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task CheckControllerAsync(
        ControllerOptions options,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            options,
            HttpMethod.Get,
            "/version",
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        await EnsureControllerResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NodeGroupDescriptor>> GetGroupsAsync(
        ControllerOptions options,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            options,
            HttpMethod.Get,
            "/group/",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        await EnsureControllerResponseAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("proxies", out var proxies)
            || proxies.ValueKind != JsonValueKind.Array)
        {
            throw new KaringApiException("Karing 返回的节点组列表格式无效。") ;
        }

        var nativeGroups = new List<NodeGroupDescriptor>();
        foreach (var item in proxies.EnumerateArray())
        {
            var group = ParseGroup(item);
            if (!string.IsNullOrWhiteSpace(group.Name))
            {
                nativeGroups.Add(group);
            }
        }

        var proxyGroups = await GetProxyGroupsAsync(options, cancellationToken)
            .ConfigureAwait(false);
        var nativeByName = nativeGroups.ToDictionary(
            group => group.Name,
            StringComparer.Ordinal);
        var merged = new List<NodeGroupDescriptor>(proxyGroups.Count + nativeGroups.Count);
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proxyGroup in proxyGroups)
        {
            var group = nativeByName.GetValueOrDefault(proxyGroup.Name) ?? proxyGroup;
            if (added.Add(group.Name))
            {
                merged.Add(group);
            }
        }

        foreach (var nativeGroup in nativeGroups)
        {
            if (added.Add(nativeGroup.Name))
            {
                merged.Add(nativeGroup);
            }
        }

        return merged;
    }

    public async Task<NodeGroupDescriptor> GetGroupAsync(
        ControllerOptions options,
        string groupName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        var path = "/group/" + Uri.EscapeDataString(groupName);
        using var response = await SendAsync(
            options,
            HttpMethod.Get,
            path,
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var proxyGroups = await GetProxyGroupsAsync(options, cancellationToken)
                .ConfigureAwait(false);
            return proxyGroups.FirstOrDefault(group =>
                       string.Equals(group.Name, groupName, StringComparison.Ordinal))
                   ?? throw new KaringApiException($"Karing 中不存在运行时节点组“{groupName}”。");
        }

        await EnsureControllerResponseAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var group = ParseGroup(document.RootElement);
        if (string.IsNullOrWhiteSpace(group.Name))
        {
            throw new KaringApiException("Karing 返回的节点组格式无效。") ;
        }

        return group;
    }

    private async Task<IReadOnlyList<NodeGroupDescriptor>> GetProxyGroupsAsync(
        ControllerOptions options,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            options,
            HttpMethod.Get,
            "/proxies",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        await EnsureControllerResponseAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("proxies", out var proxies)
            || proxies.ValueKind != JsonValueKind.Object)
        {
            throw new KaringApiException("Karing 返回的代理列表格式无效。");
        }

        var groups = new List<NodeGroupDescriptor>();
        foreach (var property in proxies.EnumerateObject())
        {
            if (!property.Value.TryGetProperty("all", out var all)
                || all.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var group = ParseGroup(property.Value, property.Name) with
            {
                SelectNewNodesByDefault = false
            };
            if (!string.IsNullOrWhiteSpace(group.Name))
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    public async Task<ProbeOutcome> ProbeAsync(
        ControllerOptions rawOptions,
        string tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var options = rawOptions.Normalize();
        var query = "?url=" + Uri.EscapeDataString(options.TargetUrl)
                    + "&timeout=" + options.TimeoutSeconds;
        var path = "/proxies/" + Uri.EscapeDataString(tag) + "/delay" + query;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await SendAsync(
                options,
                HttpMethod.Get,
                path,
                TimeSpan.FromSeconds(options.TimeoutSeconds + 3),
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new KaringUnauthorizedException();
            }

            if (!response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                return Failure(tag, stopwatch, $"HTTP {(int)response.StatusCode}: {responseText}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;

            if (root.TryGetProperty("delay", out var delayElement)
                && delayElement.TryGetInt32(out var delay)
                && delay > 0)
            {
                stopwatch.Stop();
                return new ProbeOutcome(
                    tag,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    delay,
                    ProbeOutcomeKind.Success,
                    null,
                    ToMilliseconds(stopwatch.Elapsed));
            }

            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            return Failure(tag, stopwatch, message ?? "响应中没有有效的 delay 字段。");
        }
        catch (KaringUnauthorizedException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(tag, stopwatch, "探测超时。");
        }
        catch (KaringControllerUnavailableException exception)
        {
            stopwatch.Stop();
            return new ProbeOutcome(
                tag,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                null,
                ProbeOutcomeKind.ControllerUnavailable,
                exception.Message,
                ToMilliseconds(stopwatch.Elapsed));
        }
        catch (JsonException exception)
        {
            return Failure(tag, stopwatch, "无法解析 Karing 响应：" + exception.Message);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        ControllerOptions rawOptions,
        HttpMethod method,
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var options = rawOptions.Normalize();
        using var request = new HttpRequestMessage(method, options.BaseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(options.Secret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Secret);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new KaringControllerUnavailableException("连接 Karing 控制器超时。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new KaringControllerUnavailableException("无法连接 Karing 控制器。", exception);
        }
    }

    private static async Task EnsureControllerResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new KaringUnauthorizedException();
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            throw new KaringApiException(
                $"Karing 控制器返回 HTTP {(int)response.StatusCode}: {responseText}");
        }
    }

    private static NodeGroupDescriptor ParseGroup(
        JsonElement item,
        string? fallbackName = null)
    {
        var name = ReadString(item, "name") ?? fallbackName ?? string.Empty;
        var type = ReadString(item, "type") ?? string.Empty;
        var current = ReadString(item, "now");
        var nodes = new List<string>();

        if (item.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in all.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var tag = node.GetString();
                if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag))
                {
                    nodes.Add(tag);
                }
            }
        }

        return new NodeGroupDescriptor(name, type, current, nodes);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static ProbeOutcome Failure(string tag, Stopwatch stopwatch, string error)
    {
        stopwatch.Stop();
        return new ProbeOutcome(
            tag,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            null,
            ProbeOutcomeKind.NodeFailure,
            error,
            ToMilliseconds(stopwatch.Elapsed));
    }

    private static int ToMilliseconds(TimeSpan value) =>
        (int)Math.Min(int.MaxValue, Math.Max(0, Math.Round(value.TotalMilliseconds)));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }
}
