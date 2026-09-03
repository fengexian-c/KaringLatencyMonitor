using KaringLatencyMonitor.Core.Data;
using KaringLatencyMonitor.Core.Models;

namespace KaringLatencyMonitor.Core.Services;

public sealed class CollectionService
{
    private readonly KaringApiClient _api;
    private readonly SqliteRepository _repository;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public CollectionService(KaringApiClient api, SqliteRepository repository)
    {
        _api = api;
        _repository = repository;
    }

    public async Task<IReadOnlyList<NodeGroupDescriptor>> RefreshGroupsAsync(
        ControllerOptions options,
        CancellationToken cancellationToken = default)
    {
        var groups = await _api.GetGroupsAsync(options, cancellationToken).ConfigureAwait(false);
        _repository.UpsertGroups(groups, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return groups;
    }

    public async Task<NodeGroupDescriptor> RefreshGroupAsync(
        ControllerOptions options,
        string groupName,
        CancellationToken cancellationToken = default)
    {
        var group = await _api.GetGroupAsync(options, groupName, cancellationToken)
            .ConfigureAwait(false);
        _repository.UpsertGroup(group, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return group;
    }

    public async Task<CollectionResult> RunOnceAsync(
        ControllerOptions rawOptions,
        string groupName,
        IProgress<ProbeOutcome>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return CollectionResult.Empty("上一轮采集尚未结束。");
        }

        try
        {
            return await RunCoreAsync(
                rawOptions.Normalize(),
                groupName,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<CollectionResult> RunCoreAsync(
        ControllerOptions options,
        string groupName,
        IProgress<ProbeOutcome>? progress,
        CancellationToken cancellationToken)
    {
        var startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        NodeGroupDescriptor group;
        try
        {
            group = await _api.GetGroupAsync(options, groupName, cancellationToken)
                .ConfigureAwait(false);
            _repository.UpsertGroup(group, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (KaringControllerUnavailableException exception)
        {
            return RecordControllerFailure(groupName, startedAtMs, exception.Message);
        }
        catch (KaringUnauthorizedException exception)
        {
            return RecordFailedRun(groupName, startedAtMs, exception.Message);
        }
        catch (KaringApiException exception)
        {
            return RecordFailedRun(groupName, startedAtMs, exception.Message);
        }

        var tags = _repository.GetSelectedPresentTags(groupName);
        var runId = _repository.CreateProbeRun(groupName, startedAtMs, tags.Count);
        if (tags.Count == 0)
        {
            _repository.CompleteProbeRun(
                runId,
                ProbeRunStatus.Complete,
                Array.Empty<ProbeOutcome>(),
                0,
                null);
            return new CollectionResult(runId, ProbeRunStatus.Complete, 0, 0, 0, null);
        }

        using var throttle = new SemaphoreSlim(options.MaxConcurrency);
        var tasks = tags.Select(async tag =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ProbeOutcome outcome;
                try
                {
                    outcome = await _api.ProbeAsync(options, tag, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (KaringApiException exception)
                {
                    outcome = new ProbeOutcome(
                        tag,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        null,
                        ProbeOutcomeKind.ControllerUnavailable,
                        exception.Message,
                        0);
                }

                progress?.Report(outcome);
                return outcome;
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();

        ProbeOutcome[] outcomes;
        try
        {
            outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _repository.CompleteProbeRun(
                runId,
                ProbeRunStatus.Cancelled,
                Array.Empty<ProbeOutcome>(),
                tags.Count,
                "采集已取消。");
            throw;
        }

        var successCount = outcomes.Count(item => item.IsSuccess);
        var failureCount = outcomes.Count(item => item.Kind == ProbeOutcomeKind.NodeFailure);
        var controllerFailureCount = outcomes.Count(item => item.Kind == ProbeOutcomeKind.ControllerUnavailable);
        var status = controllerFailureCount switch
        {
            0 => ProbeRunStatus.Complete,
            _ when controllerFailureCount == outcomes.Length => ProbeRunStatus.ControllerOffline,
            _ => ProbeRunStatus.Partial
        };
        var error = controllerFailureCount == 0
            ? null
            : $"{controllerFailureCount} 个探测未能连接 Karing 控制器。";

        _repository.CompleteProbeRun(runId, status, outcomes, tags.Count, error);
        return new CollectionResult(
            runId,
            status,
            tags.Count,
            successCount,
            failureCount,
            error);
    }

    private CollectionResult RecordControllerFailure(
        string groupName,
        long startedAtMs,
        string error)
    {
        var runId = _repository.CreateProbeRun(groupName, startedAtMs, 0);
        _repository.CompleteProbeRun(
            runId,
            ProbeRunStatus.ControllerOffline,
            Array.Empty<ProbeOutcome>(),
            0,
            error);
        return new CollectionResult(
            runId,
            ProbeRunStatus.ControllerOffline,
            0,
            0,
            0,
            error);
    }

    private CollectionResult RecordFailedRun(
        string groupName,
        long startedAtMs,
        string error)
    {
        var runId = _repository.CreateProbeRun(groupName, startedAtMs, 0);
        _repository.CompleteProbeRun(
            runId,
            ProbeRunStatus.Failed,
            Array.Empty<ProbeOutcome>(),
            0,
            error);
        return new CollectionResult(runId, ProbeRunStatus.Failed, 0, 0, 0, error);
    }
}
