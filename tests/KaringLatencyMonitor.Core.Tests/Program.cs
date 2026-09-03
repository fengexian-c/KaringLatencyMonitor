using System.Net;
using System.Text;
using KaringLatencyMonitor.Core.Data;
using KaringLatencyMonitor.Core.Models;
using KaringLatencyMonitor.Core.Services;
using Microsoft.Data.Sqlite;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Latency color boundaries", () => RunSynchronous(TestLatencyColorBoundaries)),
    ("Availability color boundaries", () => RunSynchronous(TestAvailabilityColorBoundaries)),
    ("Dashboard anchor ceiling", () => RunSynchronous(TestDashboardAnchorCeiling)),
    ("Karing API groups and delay field", TestKaringApiContractAsync),
    ("SQLite aggregation and availability", () => RunSynchronous(TestSqliteAggregation)),
    ("Dashboard statistic sorting", () => RunSynchronous(TestDashboardStatisticSorting)),
    ("Legacy sort preference migration", () => RunSynchronous(TestLegacySortPreferenceMigration)),
    ("Cross-group node history sharing", () => RunSynchronous(TestCrossGroupNodeHistorySharing)),
    ("Selection persistence", () => RunSynchronous(TestSelectionPersistence))
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} test(s) failed.");
    return 1;
}

Console.WriteLine($"All {tests.Length} tests passed.");
return 0;

static Task RunSynchronous(Action action)
{
    action();
    return Task.CompletedTask;
}

static void TestLatencyColorBoundaries()
{
    Equal(LatencyBand.Green, LatencyBands.FromDelay(99.9), "99.9 ms");
    Equal(LatencyBand.LightGreen, LatencyBands.FromDelay(100), "100 ms");
    Equal(LatencyBand.LightGreen, LatencyBands.FromDelay(199.9), "199.9 ms");
    Equal(LatencyBand.Yellow, LatencyBands.FromDelay(200), "200 ms");
    Equal(LatencyBand.Yellow, LatencyBands.FromDelay(299.9), "299.9 ms");
    Equal(LatencyBand.Orange, LatencyBands.FromDelay(300), "300 ms");
    Equal(LatencyBand.Orange, LatencyBands.FromDelay(399.9), "399.9 ms");
    Equal(LatencyBand.Red, LatencyBands.FromDelay(400), "400 ms");
}

static void TestAvailabilityColorBoundaries()
{
    Equal(AvailabilityBand.None, AvailabilityBands.FromCounts(0, 0), "no attempts");
    Equal(AvailabilityBand.Failed, AvailabilityBands.FromCounts(4, 0), "0 percent");
    Equal(AvailabilityBand.Red, AvailabilityBands.FromCounts(4, 1), "25 percent");
    Equal(AvailabilityBand.Red, AvailabilityBands.FromCounts(100, 49), "49 percent");
    Equal(AvailabilityBand.Yellow, AvailabilityBands.FromCounts(2, 1), "50 percent");
    Equal(AvailabilityBand.Yellow, AvailabilityBands.FromCounts(4, 3), "75 percent");
    Equal(AvailabilityBand.Yellow, AvailabilityBands.FromCounts(100, 99), "99 percent");
    Equal(AvailabilityBand.Green, AvailabilityBands.FromCounts(4, 4), "100 percent");
}

static void TestDashboardAnchorCeiling()
{
    var boundary = new DateTimeOffset(
        2026,
        9,
        2,
        14,
        45,
        0,
        TimeSpan.Zero).ToUnixTimeMilliseconds();
    var nextBoundary = boundary + DashboardTimeAlignment.FiveMinutesMs;

    Equal(boundary, DashboardTimeAlignment.CeilToFiveMinutes(boundary), "exact boundary");
    Equal(nextBoundary, DashboardTimeAlignment.CeilToFiveMinutes(boundary + 1), "one millisecond after boundary");
    Equal(nextBoundary, DashboardTimeAlignment.CeilToFiveMinutes(nextBoundary - 1), "one millisecond before next boundary");
    True(boundary + 2 * 60_000 < DashboardTimeAlignment.CeilToFiveMinutes(boundary + 2 * 60_000),
        "current sample is below the ceiling anchor");
}

static async Task TestKaringApiContractAsync()
{
    var requests = new List<RequestSnapshot>();
    using var handler = new StubHttpMessageHandler(request =>
    {
        requests.Add(new RequestSnapshot(
            request.RequestUri?.PathAndQuery ?? string.Empty,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter));

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var json = path switch
        {
            "/group/" => """
                {"proxies":[{"name":"自动 组","type":"Selector","now":"香港","all":["香港","美国","香港"]}]}
                """,
            "/group/%E8%87%AA%E5%8A%A8%20%E7%BB%84" => """
                {"name":"自动 组","type":"Selector","now":"香港","all":["香港","美国"]}
                """,
            "/proxies" => """
                {"proxies":{
                  "GLOBAL":{"name":"GLOBAL","type":"Fallback","now":"自动 组","all":["香港","美国","自动 组"]},
                  "自动 组":{"name":"自动 组","type":"Selector","now":"香港","all":["香港","美国"]},
                  "香港":{"name":"香港","type":"VLESS"}
                }}
                """,
            _ when path.EndsWith("/delay", StringComparison.Ordinal) =>
                "{\"delay\":123,\"delay2\":999,\"packetLoss\":0.25}",
            _ => "{\"message\":\"not found\"}"
        };
        var status = json.Contains("not found", StringComparison.Ordinal)
            ? HttpStatusCode.NotFound
            : HttpStatusCode.OK;
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    });
    using var client = new KaringApiClient(handler);
    var options = ControllerOptions.Default with
    {
        BaseUrl = "http://127.0.0.1:3057",
        Secret = "secret-value",
        TargetUrl = "https://example.com/generate 204"
    };

    var groups = await client.GetGroupsAsync(options);
    Equal(2, groups.Count, "merged group count");
    var global = groups.Single(group => group.Name == "GLOBAL");
    Equal("Fallback", global.Type, "synthetic group type");
    Equal(3, global.Nodes.Count, "synthetic group nodes");
    True(!global.SelectNewNodesByDefault, "synthetic group defaults to no selection");
    var automatic = groups.Single(group => group.Name == "自动 组");
    Equal(2, automatic.Nodes.Count, "deduplicated native nodes");
    True(automatic.SelectNewNodesByDefault, "native group keeps default selection");

    var group = await client.GetGroupAsync(options, "自动 组");
    Equal("香港", group.CurrentTag!, "current node");

    var fallbackGroup = await client.GetGroupAsync(options, "GLOBAL");
    Equal(3, fallbackGroup.Nodes.Count, "synthetic group fallback refresh");

    var outcome = await client.ProbeAsync(options, "香港 A");
    Equal(ProbeOutcomeKind.Success, outcome.Kind, "probe outcome");
    Equal(123, outcome.DelayMs!.Value, "delay field is authoritative");
    True(requests.Any(request => request.PathAndQuery.StartsWith(
        "/group/%E8%87%AA%E5%8A%A8%20%E7%BB%84",
        StringComparison.OrdinalIgnoreCase)), "group name is escaped");
    True(requests.Any(request => request.PathAndQuery.Contains(
        "/proxies/%E9%A6%99%E6%B8%AF%20A/delay",
        StringComparison.OrdinalIgnoreCase)),
        "node tag is escaped");
    True(requests.Any(request => request.PathAndQuery.Contains(
        "url=https%3A%2F%2Fexample.com%2Fgenerate%20204",
        StringComparison.OrdinalIgnoreCase)),
        "target URL is escaped");
    True(requests.All(request => request.AuthorizationScheme == "Bearer"
                                 && request.AuthorizationParameter == "secret-value"),
        "bearer authorization");
}

static void TestSqliteAggregation()
{
    WithRepository(repository =>
    {
        const long anchor = 1_800_000_000_000;
        const string groupName = "自动选择";
        repository.UpsertGroup(
            new NodeGroupDescriptor(groupName, "URLTest", "A", ["A", "B", "C"]),
            anchor - 100_000);

        CompleteRun(
            repository,
            groupName,
            anchor - 30 * 60 * 1000,
            [
                Success("A", anchor - 29 * 60 * 1000, 50),
                Failure("B", anchor - 28 * 60 * 1000)
            ]);
        CompleteRun(
            repository,
            groupName,
            anchor - 90 * 60 * 1000,
            [
                Success("A", anchor - 89 * 60 * 1000, 150),
                Success("B", anchor - 88 * 60 * 1000, 350)
            ]);

        var offlineRun = repository.CreateProbeRun(
            groupName,
            anchor - 150 * 60 * 1000,
            0);
        repository.CompleteProbeRun(
            offlineRun,
            ProbeRunStatus.ControllerOffline,
            Array.Empty<ProbeOutcome>(),
            0,
            "offline");

        var dashboard = repository.LoadDashboard(groupName, anchor);
        Equal(3, dashboard.Rows.Count, "selected rows");

        var a = dashboard.Rows.Single(row => row.Tag == "A");
        Equal(24, a.HourCells.Count, "A hour cell count");
        Near(100, a.Hours24.AverageDelayMs, 0.001, "A 24h delay");
        Near(100, a.Hours24.AvailabilityPercent, 0.001, "A availability");
        Equal(LatencyBand.Green, a.HourCells[23].Band, "A latest bucket band");

        var b = dashboard.Rows.Single(row => row.Tag == "B");
        Near(350, b.Hours24.AverageDelayMs, 0.001, "B 24h delay");
        Near(50, b.Hours24.AvailabilityPercent, 0.001, "B availability");
        Equal(HeatmapCellState.Failed, b.HourCells[23].State, "B latest bucket failure");

        var c = dashboard.Rows.Single(row => row.Tag == "C");
        Equal(HeatmapCellState.NoData, c.HourCells[23].State, "C no data");
        True(c.HourCells.Any(cell => cell.ControllerWasOffline), "offline run is represented");
        Equal(0, c.Hours24.Attempts, "controller offline does not create node attempts");
    });
}

static void TestSelectionPersistence()
{
    WithRepository(repository =>
    {
        const string groupName = "选择器";
        repository.UpsertGroup(
            new NodeGroupDescriptor(groupName, "Selector", null, ["A", "B", "C"]),
            1000);
        repository.SaveSelection(groupName, ["B"]);

        var selected = repository.GetSelectedPresentTags(groupName);
        Equal(1, selected.Count, "selected count");
        Equal("B", selected[0], "selected tag");

        var nodes = repository.GetSelectableNodes(groupName);
        True(nodes.Single(node => node.Tag == "B").IsSelected, "B selected");
        True(!nodes.Single(node => node.Tag == "A").IsSelected, "A cleared");

        repository.SaveDefaultNodeOrder(groupName, ["C", "A", "B"]);
        Equal(
            "C,A,B",
            string.Join(',', repository.GetSelectableNodes(groupName).Select(node => node.Tag)),
            "manual default order");
        repository.SaveSelection(groupName, ["A", "B", "C"]);
        Equal(
            "C,A,B",
            string.Join(',', repository.LoadDashboard(groupName, 1200).Rows.Select(row => row.Tag)),
            "dashboard follows manual default order");
        repository.SaveSelection(groupName, ["B"]);

        repository.UpsertGroup(
            new NodeGroupDescriptor(groupName, "Selector", "B", ["B"]),
            1500);
        var remainingNodes = repository.GetSelectableNodes(groupName);
        Equal(1, remainingNodes.Count, "removed nodes are hidden from selection");
        Equal("B", remainingNodes[0].Tag, "present node remains in selection");
        var remainingRows = repository.LoadDashboard(groupName, 2000).Rows;
        Equal(1, remainingRows.Count, "removed nodes are hidden from dashboard");
        Equal("B", remainingRows[0].Tag, "present node remains in dashboard");

        repository.UpsertGroup(
            new NodeGroupDescriptor(groupName, "Selector", "B", ["A", "B", "C"]),
            1750);
        True(repository.GetSelectableNodes(groupName).Single(node => node.Tag == "B").IsSelected,
            "reappearing group restores the prior selection");
        Equal(
            "C,A,B",
            string.Join(',', repository.GetSelectableNodes(groupName).Select(node => node.Tag)),
            "reappearing nodes restore manual order");

        repository.UpsertGroup(
            new NodeGroupDescriptor(groupName, "Selector", "B", ["B", "C", "A", "D"]),
            1800);
        Equal(
            "C,A,B,D",
            string.Join(',', repository.GetSelectableNodes(groupName).Select(node => node.Tag)),
            "new nodes append after manual order");

        var preference = new DashboardSortPreference(DashboardSortKey.Days7Availability, true);
        repository.SaveDashboardSortPreference(groupName, preference);
        Equal(preference, repository.GetDashboardSortPreference(groupName),
            "group sort preference persists");

        repository.UpsertGroup(
            new NodeGroupDescriptor(
                "GLOBAL",
                "Fallback",
                groupName,
                ["A", "B", groupName],
                SelectNewNodesByDefault: false),
            2000);
        Equal(0, repository.GetSelectedPresentTags("GLOBAL").Count,
            "synthetic group starts with no selected nodes");
        True(repository.GetSelectableNodes("GLOBAL").All(node => !node.IsSelected),
            "synthetic group nodes are unchecked");
    });
}

static void TestDashboardStatisticSorting()
{
    var rows = new[]
    {
        StatisticsRow("A", 200, 50, 300, 10, 5, 7, 9),
        StatisticsRow("B", null, null, null, 10, 0, 10, 5),
        StatisticsRow("C", 100, 300, 50, 10, 10, 2, 1),
        StatisticsRow("D", null, null, null, attempts: 0)
    };

    Equal(
        "A,B,C,D",
        Tags(DashboardRowSorter.Sort(rows, DashboardSortPreference.Default)),
        "default order remains stable");
    Equal(
        "C,A,B,D",
        Tags(DashboardRowSorter.Sort(
            rows,
            new DashboardSortPreference(DashboardSortKey.Hours24Delay, false))),
        "24h ascending");
    Equal(
        "A,C,B,D",
        Tags(DashboardRowSorter.Sort(
            rows,
            new DashboardSortPreference(DashboardSortKey.Hours24Delay, true))),
        "24h descending");
    Equal(
        "A,C,B,D",
        Tags(DashboardRowSorter.Sort(
            rows,
            new DashboardSortPreference(DashboardSortKey.Days7Delay, false))),
        "7d ascending");
    Equal(
        "C,A,B,D",
        Tags(DashboardRowSorter.Sort(
            rows,
            new DashboardSortPreference(DashboardSortKey.Days30Delay, false))),
        "30d ascending");
    Equal(
        "B,A,C,D",
        Tags(DashboardRowSorter.Sort(
            rows,
            new DashboardSortPreference(DashboardSortKey.Hours24Availability, false))),
        "24h availability ascending");
    Equal(
        "C,A,B,D",
        Tags(DashboardRowSorter.Sort(
            rows,
            new DashboardSortPreference(DashboardSortKey.Hours24Availability, true))),
        "24h availability descending");
    Equal(
        "C,A,B,D",
        Tags(DashboardRowSorter.Sort(
            rows,
            new DashboardSortPreference(DashboardSortKey.Days7Availability, false))),
        "7d availability ascending");
    Equal(
        "C,B,A,D",
        Tags(DashboardRowSorter.Sort(
            rows,
            new DashboardSortPreference(DashboardSortKey.Days30Availability, false))),
        "30d availability ascending");
}

static void TestLegacySortPreferenceMigration()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "KaringLatencyMonitor.Tests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "latency.db");
    try
    {
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE node_group (
                    name             TEXT PRIMARY KEY COLLATE BINARY,
                    type             TEXT NOT NULL DEFAULT '',
                    current_tag      TEXT,
                    last_seen_at_ms  INTEGER NOT NULL
                );
                CREATE TABLE group_sort_preference (
                    group_name       TEXT PRIMARY KEY,
                    sort_key         TEXT NOT NULL CHECK (
                        sort_key IN ('default', 'delay_24h', 'delay_7d', 'delay_30d')),
                    descending       INTEGER NOT NULL CHECK (descending IN (0, 1)),
                    FOREIGN KEY (group_name) REFERENCES node_group(name) ON DELETE CASCADE
                );
                INSERT INTO node_group(name, last_seen_at_ms) VALUES ('Legacy', 1);
                INSERT INTO group_sort_preference(group_name, sort_key, descending)
                VALUES ('Legacy', 'delay_7d', 1);
                """;
            command.ExecuteNonQuery();
        }

        var repository = new SqliteRepository(databasePath);
        repository.Initialize();
        Equal(
            new DashboardSortPreference(DashboardSortKey.Days7Delay, true),
            repository.GetDashboardSortPreference("Legacy"),
            "legacy delay preference survives migration");

        var availabilityPreference =
            new DashboardSortPreference(DashboardSortKey.Days30Availability, false);
        repository.SaveDashboardSortPreference("Legacy", availabilityPreference);
        Equal(
            availabilityPreference,
            repository.GetDashboardSortPreference("Legacy"),
            "availability preference saves after migration");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}

static void TestCrossGroupNodeHistorySharing()
{
    WithRepository(repository =>
    {
        const long anchor = 1_800_000_000_000;
        const string groupA = "组 A";
        const string groupB = "组 B";
        repository.UpsertGroups(
            [
                new NodeGroupDescriptor(groupA, "Selector", "共享节点", ["共享节点", "仅 A"]),
                new NodeGroupDescriptor(groupB, "URLTest", "共享节点", ["共享节点", "仅 B"])
            ],
            anchor - 100_000);

        CompleteRun(
            repository,
            groupA,
            anchor - 40 * 60 * 1000,
            [
                Success("共享节点", anchor - 39 * 60 * 1000, 80),
                Success("仅 A", anchor - 38 * 60 * 1000, 120)
            ]);
        CompleteRun(
            repository,
            groupB,
            anchor - 20 * 60 * 1000,
            [Failure("共享节点", anchor - 19 * 60 * 1000)]);

        var offlineRun = repository.CreateProbeRun(
            groupA,
            anchor - 150 * 60 * 1000,
            0);
        repository.CompleteProbeRun(
            offlineRun,
            ProbeRunStatus.ControllerOffline,
            Array.Empty<ProbeOutcome>(),
            0,
            "offline");

        var dashboardA = repository.LoadDashboard(groupA, anchor);
        var dashboardB = repository.LoadDashboard(groupB, anchor);
        var sharedA = dashboardA.Rows.Single(row => row.Tag == "共享节点");
        var sharedB = dashboardB.Rows.Single(row => row.Tag == "共享节点");

        Equal(2, sharedA.Hours24.Attempts, "group A sees both shared-node attempts");
        Equal(2, sharedB.Hours24.Attempts, "group B sees both shared-node attempts");
        Near(80, sharedA.Hours24.AverageDelayMs, 0.001, "group A shared delay");
        Near(80, sharedB.Hours24.AverageDelayMs, 0.001, "group B shared delay");
        Near(50, sharedA.Hours24.AvailabilityPercent, 0.001, "group A shared availability");
        Near(50, sharedB.Hours24.AvailabilityPercent, 0.001, "group B shared availability");

        var onlyB = dashboardB.Rows.Single(row => row.Tag == "仅 B");
        Equal(0, onlyB.Hours24.Attempts, "unrelated node history does not leak");
        True(sharedA.HourCells.Any(cell => cell.ControllerWasOffline),
            "source group keeps its controller-offline marker");
        True(sharedB.HourCells.All(cell => !cell.ControllerWasOffline),
            "other group does not inherit controller-offline markers");

        repository.SaveSelection(groupB, Array.Empty<string>());
        True(repository.GetSelectedPresentTags(groupA).Contains("共享节点", StringComparer.Ordinal),
            "group A selection remains enabled");
        Equal(0, repository.GetSelectedPresentTags(groupB).Count,
            "group B selection remains independent");
    });
}

static NodeStatisticsRow StatisticsRow(
    string tag,
    double? hours24,
    double? days7,
    double? days30,
    int attempts = 1,
    int? hours24Successes = null,
    int? days7Successes = null,
    int? days30Successes = null) =>
    new(
        tag,
        0,
        true,
        Array.Empty<HeatmapCell>(),
        new PeriodStatistics(
            hours24,
            attempts,
            hours24Successes ?? (hours24 is null ? 0 : attempts)),
        new PeriodStatistics(
            days7,
            attempts,
            days7Successes ?? (days7 is null ? 0 : attempts)),
        new PeriodStatistics(
            days30,
            attempts,
            days30Successes ?? (days30 is null ? 0 : attempts)));

static string Tags(IReadOnlyList<NodeStatisticsRow> rows) =>
    string.Join(',', rows.Select(row => row.Tag));

static void CompleteRun(
    SqliteRepository repository,
    string groupName,
    long startedAtMs,
    IReadOnlyCollection<ProbeOutcome> outcomes)
{
    var runId = repository.CreateProbeRun(groupName, startedAtMs, outcomes.Count);
    repository.CompleteProbeRun(
        runId,
        ProbeRunStatus.Complete,
        outcomes,
        outcomes.Count,
        null);
}

static ProbeOutcome Success(string tag, long measuredAtMs, int delayMs) =>
    new(tag, measuredAtMs, delayMs, ProbeOutcomeKind.Success, null, delayMs);

static ProbeOutcome Failure(string tag, long measuredAtMs) =>
    new(tag, measuredAtMs, null, ProbeOutcomeKind.NodeFailure, "timeout", 15_000);

static void WithRepository(Action<SqliteRepository> test)
{
    var directory = Path.Combine(Path.GetTempPath(), "KaringLatencyMonitor.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var repository = new SqliteRepository(Path.Combine(directory, "latency.db"));
        repository.Initialize();
        test(repository);
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}

static void Equal<T>(T expected, T actual, string name)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }
}

static void Near(double expected, double? actual, double tolerance, string name)
{
    if (actual is null || Math.Abs(expected - actual.Value) > tolerance)
    {
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }
}

static void True(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException(name);
    }
}

internal sealed record RequestSnapshot(
    string PathAndQuery,
    string? AuthorizationScheme,
    string? AuthorizationParameter);

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(responder(request));
}
