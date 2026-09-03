using KaringLatencyMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace KaringLatencyMonitor.Core.Data;

public sealed class SqliteRepository
{
    private const long HourMs = 60L * 60 * 1000;
    private const long DayMs = 24L * HourMs;
    private readonly string _connectionString;

    public SqliteRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS node_group (
                name             TEXT PRIMARY KEY COLLATE BINARY,
                type             TEXT NOT NULL DEFAULT '',
                current_tag      TEXT,
                last_seen_at_ms  INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS node (
                tag              TEXT PRIMARY KEY COLLATE BINARY,
                type             TEXT NOT NULL DEFAULT '',
                first_seen_at_ms INTEGER NOT NULL,
                last_seen_at_ms  INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS group_member (
                group_name       TEXT NOT NULL,
                tag              TEXT NOT NULL,
                ordinal          INTEGER NOT NULL,
                is_present       INTEGER NOT NULL DEFAULT 1 CHECK (is_present IN (0, 1)),
                last_seen_at_ms  INTEGER NOT NULL,
                PRIMARY KEY (group_name, tag),
                FOREIGN KEY (group_name) REFERENCES node_group(name) ON DELETE CASCADE,
                FOREIGN KEY (tag) REFERENCES node(tag) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS monitor_selection (
                group_name       TEXT NOT NULL,
                tag              TEXT NOT NULL,
                enabled          INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                PRIMARY KEY (group_name, tag),
                FOREIGN KEY (group_name, tag)
                    REFERENCES group_member(group_name, tag) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS group_node_order (
                group_name       TEXT NOT NULL,
                tag              TEXT NOT NULL,
                position         INTEGER NOT NULL,
                PRIMARY KEY (group_name, tag),
                FOREIGN KEY (group_name, tag)
                    REFERENCES group_member(group_name, tag) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS group_sort_preference (
                group_name       TEXT PRIMARY KEY,
                sort_key         TEXT NOT NULL CHECK (
                    sort_key IN (
                        'default',
                        'delay_24h', 'availability_24h',
                        'delay_7d', 'availability_7d',
                        'delay_30d', 'availability_30d')),
                descending       INTEGER NOT NULL CHECK (descending IN (0, 1)),
                FOREIGN KEY (group_name) REFERENCES node_group(name) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS probe_run (
                id               INTEGER PRIMARY KEY,
                group_name       TEXT NOT NULL,
                started_at_ms    INTEGER NOT NULL,
                finished_at_ms   INTEGER,
                status           TEXT NOT NULL,
                expected_count   INTEGER NOT NULL,
                success_count    INTEGER NOT NULL DEFAULT 0,
                failure_count    INTEGER NOT NULL DEFAULT 0,
                error            TEXT
            );

            CREATE TABLE IF NOT EXISTS latency_sample (
                -- Samples are global node observations. run_id records the
                -- source collection run, but does not own the sample's group scope.
                run_id           INTEGER NOT NULL,
                tag              TEXT NOT NULL,
                measured_at_ms   INTEGER NOT NULL,
                delay_ms         INTEGER,
                ok               INTEGER NOT NULL CHECK (ok IN (0, 1)),
                error            TEXT,
                request_cost_ms  INTEGER NOT NULL,
                PRIMARY KEY (run_id, tag),
                FOREIGN KEY (run_id) REFERENCES probe_run(id) ON DELETE CASCADE,
                CHECK (
                    (ok = 1 AND delay_ms IS NOT NULL AND delay_ms > 0)
                    OR
                    (ok = 0 AND delay_ms IS NULL)
                )
            );

            CREATE INDEX IF NOT EXISTS idx_group_member_group_order
                ON group_member(group_name, ordinal);
            CREATE INDEX IF NOT EXISTS idx_group_node_order_group_position
                ON group_node_order(group_name, position);
            CREATE INDEX IF NOT EXISTS idx_latency_sample_tag_time
                ON latency_sample(tag, measured_at_ms);
            CREATE INDEX IF NOT EXISTS idx_probe_run_group_time
                ON probe_run(group_name, started_at_ms);
            """;
        command.ExecuteNonQuery();
        EnsureSortPreferenceSchema(connection);
    }

    private static void EnsureSortPreferenceSchema(SqliteConnection connection)
    {
        using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'group_sort_preference';
            """;
        var schema = schemaCommand.ExecuteScalar() as string;
        if (schema is null || schema.Contains("availability_24h", StringComparison.Ordinal))
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using var migrationCommand = connection.CreateCommand();
        migrationCommand.Transaction = transaction;
        migrationCommand.CommandText = """
            CREATE TABLE group_sort_preference_v2 (
                group_name       TEXT PRIMARY KEY,
                sort_key         TEXT NOT NULL CHECK (
                    sort_key IN (
                        'default',
                        'delay_24h', 'availability_24h',
                        'delay_7d', 'availability_7d',
                        'delay_30d', 'availability_30d')),
                descending       INTEGER NOT NULL CHECK (descending IN (0, 1)),
                FOREIGN KEY (group_name) REFERENCES node_group(name) ON DELETE CASCADE
            );

            INSERT INTO group_sort_preference_v2(group_name, sort_key, descending)
            SELECT group_name, sort_key, descending
            FROM group_sort_preference;

            DROP TABLE group_sort_preference;
            ALTER TABLE group_sort_preference_v2 RENAME TO group_sort_preference;
            """;
        migrationCommand.ExecuteNonQuery();
        transaction.Commit();
    }

    public void UpsertGroups(IReadOnlyList<NodeGroupDescriptor> groups, long observedAtMs)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var group in groups)
        {
            UpsertGroup(connection, transaction, group, observedAtMs);
        }

        transaction.Commit();
    }

    public void UpsertGroup(NodeGroupDescriptor group, long observedAtMs)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        UpsertGroup(connection, transaction, group, observedAtMs);
        transaction.Commit();
    }

    public IReadOnlyList<NodeGroupDescriptor> GetCachedGroups()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, type, current_tag
            FROM node_group
            ORDER BY name COLLATE NOCASE;
            """;

        var groupHeaders = new List<(string Name, string Type, string? CurrentTag)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            groupHeaders.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        reader.Close();
        return groupHeaders
            .Select(group => new NodeGroupDescriptor(
                group.Name,
                group.Type,
                group.CurrentTag,
                GetGroupNodeTags(connection, group.Name)))
            .ToArray();
    }

    public IReadOnlyList<SelectableNode> GetSelectableNodes(string groupName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT gm.tag, gm.ordinal, gm.is_present, COALESCE(ms.enabled, 1)
            FROM group_member gm
            LEFT JOIN monitor_selection ms
              ON ms.group_name = gm.group_name AND ms.tag = gm.tag
            LEFT JOIN group_node_order custom_order
              ON custom_order.group_name = gm.group_name AND custom_order.tag = gm.tag
            WHERE gm.group_name = $group_name
              AND gm.is_present = 1
            ORDER BY
                CASE WHEN custom_order.position IS NULL THEN 1 ELSE 0 END,
                custom_order.position,
                gm.ordinal,
                gm.tag COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$group_name", groupName);

        var nodes = new List<SelectableNode>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            nodes.Add(new SelectableNode(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2) != 0,
                reader.GetInt32(3) != 0));
        }

        return nodes;
    }

    public IReadOnlyList<string> GetSelectedPresentTags(string groupName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT gm.tag
            FROM group_member gm
            JOIN monitor_selection ms
              ON ms.group_name = gm.group_name AND ms.tag = gm.tag
            LEFT JOIN group_node_order custom_order
              ON custom_order.group_name = gm.group_name AND custom_order.tag = gm.tag
            WHERE gm.group_name = $group_name
              AND gm.is_present = 1
              AND ms.enabled = 1
            ORDER BY
                CASE WHEN custom_order.position IS NULL THEN 1 ELSE 0 END,
                custom_order.position,
                gm.ordinal,
                gm.tag COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$group_name", groupName);

        var tags = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    public void SaveSelection(string groupName, IReadOnlyCollection<string> selectedTags)
    {
        var selected = selectedTags.ToHashSet(StringComparer.Ordinal);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var selectCommand = connection.CreateCommand();
        selectCommand.Transaction = transaction;
        selectCommand.CommandText = """
            SELECT tag FROM group_member WHERE group_name = $group_name;
            """;
        selectCommand.Parameters.AddWithValue("$group_name", groupName);

        var knownTags = new List<string>();
        using (var reader = selectCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                knownTags.Add(reader.GetString(0));
            }
        }

        using var upsertCommand = connection.CreateCommand();
        upsertCommand.Transaction = transaction;
        upsertCommand.CommandText = """
            INSERT INTO monitor_selection(group_name, tag, enabled)
            VALUES ($group_name, $tag, $enabled)
            ON CONFLICT(group_name, tag) DO UPDATE SET enabled = excluded.enabled;
            """;
        var groupParameter = upsertCommand.Parameters.Add("$group_name", SqliteType.Text);
        var tagParameter = upsertCommand.Parameters.Add("$tag", SqliteType.Text);
        var enabledParameter = upsertCommand.Parameters.Add("$enabled", SqliteType.Integer);

        foreach (var tag in knownTags)
        {
            groupParameter.Value = groupName;
            tagParameter.Value = tag;
            enabledParameter.Value = selected.Contains(tag) ? 1 : 0;
            upsertCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SaveDefaultNodeOrder(string groupName, IReadOnlyList<string> orderedPresentTags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentNullException.ThrowIfNull(orderedPresentTags);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var members = LoadAllMembersInDefaultOrder(connection, transaction, groupName);
        var presentTags = members
            .Where(member => member.IsPresent)
            .Select(member => member.Tag)
            .ToHashSet(StringComparer.Ordinal);
        var requestedTags = orderedPresentTags.ToHashSet(StringComparer.Ordinal);
        if (orderedPresentTags.Count != requestedTags.Count
            || !presentTags.SetEquals(requestedTags))
        {
            throw new InvalidOperationException(
                "节点顺序已过期，请刷新节点组后重试。");
        }

        var reorderedPresent = new Queue<string>(orderedPresentTags);
        var completeOrder = members
            .Select(member => member.IsPresent ? reorderedPresent.Dequeue() : member.Tag)
            .ToArray();

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText =
                "DELETE FROM group_node_order WHERE group_name = $group_name;";
            deleteCommand.Parameters.AddWithValue("$group_name", groupName);
            deleteCommand.ExecuteNonQuery();
        }

        using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO group_node_order(group_name, tag, position)
            VALUES ($group_name, $tag, $position);
            """;
        insertCommand.Parameters.AddWithValue("$group_name", groupName);
        var tagParameter = insertCommand.Parameters.Add("$tag", SqliteType.Text);
        var positionParameter = insertCommand.Parameters.Add("$position", SqliteType.Integer);
        for (var position = 0; position < completeOrder.Length; position++)
        {
            tagParameter.Value = completeOrder[position];
            positionParameter.Value = position;
            insertCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public DashboardSortPreference GetDashboardSortPreference(string groupName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sort_key, descending
            FROM group_sort_preference
            WHERE group_name = $group_name;
            """;
        command.Parameters.AddWithValue("$group_name", groupName);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new DashboardSortPreference(
                ParseDashboardSortKey(reader.GetString(0)),
                reader.GetInt32(1) != 0)
            : DashboardSortPreference.Default;
    }

    public void SaveDashboardSortPreference(
        string groupName,
        DashboardSortPreference preference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentNullException.ThrowIfNull(preference);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO group_sort_preference(group_name, sort_key, descending)
            VALUES ($group_name, $sort_key, $descending)
            ON CONFLICT(group_name) DO UPDATE SET
                sort_key = excluded.sort_key,
                descending = excluded.descending;
            """;
        command.Parameters.AddWithValue("$group_name", groupName);
        command.Parameters.AddWithValue("$sort_key", ToDatabaseValue(preference.Key));
        command.Parameters.AddWithValue("$descending", preference.Descending ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public long CreateProbeRun(string groupName, long startedAtMs, int expectedCount)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO probe_run(
                group_name, started_at_ms, status, expected_count,
                success_count, failure_count)
            VALUES ($group_name, $started_at_ms, 'running', $expected_count, 0, 0);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$group_name", groupName);
        command.Parameters.AddWithValue("$started_at_ms", startedAtMs);
        command.Parameters.AddWithValue("$expected_count", expectedCount);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public void CompleteProbeRun(
        long runId,
        ProbeRunStatus status,
        IReadOnlyCollection<ProbeOutcome> outcomes,
        int expectedCount,
        string? error)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO latency_sample(
                run_id, tag, measured_at_ms, delay_ms, ok, error, request_cost_ms)
            VALUES (
                $run_id, $tag, $measured_at_ms, $delay_ms, $ok, $error, $request_cost_ms)
            ON CONFLICT(run_id, tag) DO UPDATE SET
                measured_at_ms = excluded.measured_at_ms,
                delay_ms = excluded.delay_ms,
                ok = excluded.ok,
                error = excluded.error,
                request_cost_ms = excluded.request_cost_ms;
            """;

        var runParameter = insertCommand.Parameters.Add("$run_id", SqliteType.Integer);
        var tagParameter = insertCommand.Parameters.Add("$tag", SqliteType.Text);
        var timeParameter = insertCommand.Parameters.Add("$measured_at_ms", SqliteType.Integer);
        var delayParameter = insertCommand.Parameters.Add("$delay_ms", SqliteType.Integer);
        var okParameter = insertCommand.Parameters.Add("$ok", SqliteType.Integer);
        var errorParameter = insertCommand.Parameters.Add("$error", SqliteType.Text);
        var costParameter = insertCommand.Parameters.Add("$request_cost_ms", SqliteType.Integer);

        foreach (var outcome in outcomes.Where(item => item.ShouldPersist))
        {
            runParameter.Value = runId;
            tagParameter.Value = outcome.Tag;
            timeParameter.Value = outcome.MeasuredAtMs;
            delayParameter.Value = outcome.DelayMs is null ? DBNull.Value : outcome.DelayMs.Value;
            okParameter.Value = outcome.IsSuccess ? 1 : 0;
            errorParameter.Value = outcome.Error is null ? DBNull.Value : outcome.Error;
            costParameter.Value = outcome.RequestCostMs;
            insertCommand.ExecuteNonQuery();
        }

        var successCount = outcomes.Count(item => item.IsSuccess);
        var failureCount = outcomes.Count(item => item.Kind == ProbeOutcomeKind.NodeFailure);
        using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = """
            UPDATE probe_run
            SET finished_at_ms = $finished_at_ms,
                status = $status,
                expected_count = $expected_count,
                success_count = $success_count,
                failure_count = $failure_count,
                error = $error
            WHERE id = $run_id;
            """;
        updateCommand.Parameters.AddWithValue("$finished_at_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        updateCommand.Parameters.AddWithValue("$status", ToDatabaseValue(status));
        updateCommand.Parameters.AddWithValue("$expected_count", expectedCount);
        updateCommand.Parameters.AddWithValue("$success_count", successCount);
        updateCommand.Parameters.AddWithValue("$failure_count", failureCount);
        updateCommand.Parameters.AddWithValue("$error", error is null ? DBNull.Value : error);
        updateCommand.Parameters.AddWithValue("$run_id", runId);
        updateCommand.ExecuteNonQuery();

        transaction.Commit();
    }

    public DashboardSnapshot LoadDashboard(string groupName, long anchorAtMs)
    {
        var from24 = anchorAtMs - DayMs;
        var from7 = anchorAtMs - 7 * DayMs;
        var from30 = anchorAtMs - 30 * DayMs;
        using var connection = OpenConnection();

        var selectedNodes = LoadSelectedNodes(connection, groupName);
        if (selectedNodes.Count == 0)
        {
            return DashboardSnapshot.Empty(groupName, anchorAtMs);
        }

        var offlineBuckets = LoadOfflineBuckets(connection, groupName, from24, anchorAtMs);
        var cellsByTag = selectedNodes.ToDictionary(
            item => item.Tag,
            _ => CreateEmptyCells(from24, offlineBuckets),
            StringComparer.Ordinal);
        FillHeatmap(connection, groupName, from24, anchorAtMs, cellsByTag);

        var statistics = LoadPeriodStatistics(
            connection,
            groupName,
            from24,
            from7,
            from30,
            anchorAtMs);

        var rows = new List<NodeStatisticsRow>(selectedNodes.Count);
        foreach (var node in selectedNodes)
        {
            statistics.TryGetValue(node.Tag, out var periods);
            periods ??= StatisticsTriple.Empty;
            rows.Add(new NodeStatisticsRow(
                node.Tag,
                node.Ordinal,
                node.IsPresent,
                cellsByTag[node.Tag],
                periods.Hours24,
                periods.Days7,
                periods.Days30));
        }

        return new DashboardSnapshot(groupName, anchorAtMs, rows);
    }

    public int DeleteSamplesOlderThan(long cutoffMs)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var deleteSamples = connection.CreateCommand();
        deleteSamples.Transaction = transaction;
        deleteSamples.CommandText = "DELETE FROM latency_sample WHERE measured_at_ms < $cutoff_ms;";
        deleteSamples.Parameters.AddWithValue("$cutoff_ms", cutoffMs);
        var deleted = deleteSamples.ExecuteNonQuery();

        using var deleteRuns = connection.CreateCommand();
        deleteRuns.Transaction = transaction;
        deleteRuns.CommandText = """
            DELETE FROM probe_run
            WHERE started_at_ms < $cutoff_ms
              AND NOT EXISTS (
                  SELECT 1 FROM latency_sample WHERE latency_sample.run_id = probe_run.id
              );
            """;
        deleteRuns.Parameters.AddWithValue("$cutoff_ms", cutoffMs);
        deleteRuns.ExecuteNonQuery();
        transaction.Commit();
        return deleted;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void UpsertGroup(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NodeGroupDescriptor group,
        long observedAtMs)
    {
        using (var groupCommand = connection.CreateCommand())
        {
            groupCommand.Transaction = transaction;
            groupCommand.CommandText = """
                INSERT INTO node_group(name, type, current_tag, last_seen_at_ms)
                VALUES ($name, $type, $current_tag, $last_seen_at_ms)
                ON CONFLICT(name) DO UPDATE SET
                    type = excluded.type,
                    current_tag = excluded.current_tag,
                    last_seen_at_ms = excluded.last_seen_at_ms;
                """;
            groupCommand.Parameters.AddWithValue("$name", group.Name);
            groupCommand.Parameters.AddWithValue("$type", group.Type);
            groupCommand.Parameters.AddWithValue(
                "$current_tag",
                group.CurrentTag is null ? DBNull.Value : group.CurrentTag);
            groupCommand.Parameters.AddWithValue("$last_seen_at_ms", observedAtMs);
            groupCommand.ExecuteNonQuery();
        }

        using (var markMissing = connection.CreateCommand())
        {
            markMissing.Transaction = transaction;
            markMissing.CommandText = """
                UPDATE group_member SET is_present = 0 WHERE group_name = $group_name;
                """;
            markMissing.Parameters.AddWithValue("$group_name", group.Name);
            markMissing.ExecuteNonQuery();
        }

        for (var index = 0; index < group.Nodes.Count; index++)
        {
            var tag = group.Nodes[index];
            using (var nodeCommand = connection.CreateCommand())
            {
                nodeCommand.Transaction = transaction;
                nodeCommand.CommandText = """
                    INSERT INTO node(tag, type, first_seen_at_ms, last_seen_at_ms)
                    VALUES ($tag, '', $observed_at_ms, $observed_at_ms)
                    ON CONFLICT(tag) DO UPDATE SET last_seen_at_ms = excluded.last_seen_at_ms;
                    """;
                nodeCommand.Parameters.AddWithValue("$tag", tag);
                nodeCommand.Parameters.AddWithValue("$observed_at_ms", observedAtMs);
                nodeCommand.ExecuteNonQuery();
            }

            using (var memberCommand = connection.CreateCommand())
            {
                memberCommand.Transaction = transaction;
                memberCommand.CommandText = """
                    INSERT INTO group_member(
                        group_name, tag, ordinal, is_present, last_seen_at_ms)
                    VALUES ($group_name, $tag, $ordinal, 1, $last_seen_at_ms)
                    ON CONFLICT(group_name, tag) DO UPDATE SET
                        ordinal = excluded.ordinal,
                        is_present = 1,
                        last_seen_at_ms = excluded.last_seen_at_ms;
                    """;
                memberCommand.Parameters.AddWithValue("$group_name", group.Name);
                memberCommand.Parameters.AddWithValue("$tag", tag);
                memberCommand.Parameters.AddWithValue("$ordinal", index);
                memberCommand.Parameters.AddWithValue("$last_seen_at_ms", observedAtMs);
                memberCommand.ExecuteNonQuery();
            }

            using var selectionCommand = connection.CreateCommand();
            selectionCommand.Transaction = transaction;
            selectionCommand.CommandText = """
                INSERT INTO monitor_selection(group_name, tag, enabled)
                VALUES ($group_name, $tag, $enabled)
                ON CONFLICT(group_name, tag) DO NOTHING;
                """;
            selectionCommand.Parameters.AddWithValue("$group_name", group.Name);
            selectionCommand.Parameters.AddWithValue("$tag", tag);
            selectionCommand.Parameters.AddWithValue(
                "$enabled",
                group.SelectNewNodesByDefault ? 1 : 0);
            selectionCommand.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<string> GetGroupNodeTags(SqliteConnection connection, string groupName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tag FROM group_member
            WHERE group_name = $group_name AND is_present = 1
            ORDER BY ordinal, tag COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$group_name", groupName);
        var tags = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    private static List<SelectableNode> LoadSelectedNodes(
        SqliteConnection connection,
        string groupName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT gm.tag, gm.ordinal, gm.is_present
            FROM group_member gm
            JOIN monitor_selection ms
              ON ms.group_name = gm.group_name AND ms.tag = gm.tag
            LEFT JOIN group_node_order custom_order
              ON custom_order.group_name = gm.group_name AND custom_order.tag = gm.tag
            WHERE gm.group_name = $group_name
              AND gm.is_present = 1
              AND ms.enabled = 1
            ORDER BY
                CASE WHEN custom_order.position IS NULL THEN 1 ELSE 0 END,
                custom_order.position,
                gm.ordinal,
                gm.tag COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$group_name", groupName);

        var nodes = new List<SelectableNode>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            nodes.Add(new SelectableNode(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2) != 0,
                true));
        }

        return nodes;
    }

    private static List<GroupMemberOrder> LoadAllMembersInDefaultOrder(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string groupName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT gm.tag, gm.is_present
            FROM group_member gm
            LEFT JOIN group_node_order custom_order
              ON custom_order.group_name = gm.group_name AND custom_order.tag = gm.tag
            WHERE gm.group_name = $group_name
            ORDER BY
                CASE WHEN custom_order.position IS NULL THEN 1 ELSE 0 END,
                custom_order.position,
                gm.ordinal,
                gm.tag COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$group_name", groupName);

        var members = new List<GroupMemberOrder>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            members.Add(new GroupMemberOrder(
                reader.GetString(0),
                reader.GetInt32(1) != 0));
        }

        return members;
    }

    private static HashSet<int> LoadOfflineBuckets(
        SqliteConnection connection,
        string groupName,
        long fromMs,
        long toMs)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST((started_at_ms - $from_ms) / $hour_ms AS INTEGER)
            FROM probe_run
            WHERE group_name = $group_name
              AND status = 'controller_offline'
              AND started_at_ms >= $from_ms
              AND started_at_ms < $to_ms
            GROUP BY CAST((started_at_ms - $from_ms) / $hour_ms AS INTEGER);
            """;
        command.Parameters.AddWithValue("$group_name", groupName);
        command.Parameters.AddWithValue("$from_ms", fromMs);
        command.Parameters.AddWithValue("$to_ms", toMs);
        command.Parameters.AddWithValue("$hour_ms", HourMs);

        var result = new HashSet<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bucket = reader.GetInt32(0);
            if (bucket is >= 0 and < 24)
            {
                result.Add(bucket);
            }
        }

        return result;
    }

    private static HeatmapCell[] CreateEmptyCells(long fromMs, IReadOnlySet<int> offlineBuckets)
    {
        var cells = new HeatmapCell[24];
        for (var index = 0; index < cells.Length; index++)
        {
            var isOffline = offlineBuckets.Contains(index);
            cells[index] = new HeatmapCell(
                index,
                fromMs + index * HourMs,
                fromMs + (index + 1) * HourMs,
                isOffline ? HeatmapCellState.ControllerOffline : HeatmapCellState.NoData,
                LatencyBand.None,
                null,
                null,
                0,
                0,
                isOffline);
        }

        return cells;
    }

    private static void FillHeatmap(
        SqliteConnection connection,
        string groupName,
        long fromMs,
        long toMs,
        IReadOnlyDictionary<string, HeatmapCell[]> cellsByTag)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                s.tag,
                CAST((s.measured_at_ms - $from_ms) / $hour_ms AS INTEGER) AS bucket_index,
                COUNT(*) AS attempts,
                SUM(s.ok) AS successes,
                AVG(CASE WHEN s.ok = 1 THEN s.delay_ms END) AS average_delay_ms,
                MAX(CASE WHEN s.ok = 1 THEN s.delay_ms END) AS maximum_delay_ms
            FROM latency_sample s
            JOIN monitor_selection ms
              ON ms.group_name = $group_name
             AND ms.tag = s.tag
             AND ms.enabled = 1
            WHERE s.measured_at_ms >= $from_ms
              AND s.measured_at_ms < $to_ms
            GROUP BY s.tag, bucket_index
            ORDER BY s.tag, bucket_index;
            """;
        command.Parameters.AddWithValue("$group_name", groupName);
        command.Parameters.AddWithValue("$from_ms", fromMs);
        command.Parameters.AddWithValue("$to_ms", toMs);
        command.Parameters.AddWithValue("$hour_ms", HourMs);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tag = reader.GetString(0);
            var index = reader.GetInt32(1);
            if (index is < 0 or >= 24 || !cellsByTag.TryGetValue(tag, out var cells))
            {
                continue;
            }

            var attempts = reader.GetInt32(2);
            var successes = reader.GetInt32(3);
            double? average = reader.IsDBNull(4) ? null : reader.GetDouble(4);
            int? maximum = reader.IsDBNull(5) ? null : reader.GetInt32(5);
            var offline = cells[index].ControllerWasOffline;

            cells[index] = successes == 0 || average is null
                ? new HeatmapCell(
                    index,
                    fromMs + index * HourMs,
                    fromMs + (index + 1) * HourMs,
                    HeatmapCellState.Failed,
                    LatencyBand.Failed,
                    null,
                    null,
                    attempts,
                    successes,
                    offline)
                : new HeatmapCell(
                    index,
                    fromMs + index * HourMs,
                    fromMs + (index + 1) * HourMs,
                    HeatmapCellState.Success,
                    LatencyBands.FromDelay(average.Value),
                    average,
                    maximum,
                    attempts,
                    successes,
                    offline);
        }
    }

    private static Dictionary<string, StatisticsTriple> LoadPeriodStatistics(
        SqliteConnection connection,
        string groupName,
        long from24,
        long from7,
        long from30,
        long toMs)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                s.tag,
                AVG(CASE WHEN s.measured_at_ms >= $from_24 AND s.ok = 1 THEN s.delay_ms END),
                SUM(CASE WHEN s.measured_at_ms >= $from_24 THEN 1 ELSE 0 END),
                SUM(CASE WHEN s.measured_at_ms >= $from_24 AND s.ok = 1 THEN 1 ELSE 0 END),
                AVG(CASE WHEN s.measured_at_ms >= $from_7 AND s.ok = 1 THEN s.delay_ms END),
                SUM(CASE WHEN s.measured_at_ms >= $from_7 THEN 1 ELSE 0 END),
                SUM(CASE WHEN s.measured_at_ms >= $from_7 AND s.ok = 1 THEN 1 ELSE 0 END),
                AVG(CASE WHEN s.measured_at_ms >= $from_30 AND s.ok = 1 THEN s.delay_ms END),
                SUM(CASE WHEN s.measured_at_ms >= $from_30 THEN 1 ELSE 0 END),
                SUM(CASE WHEN s.measured_at_ms >= $from_30 AND s.ok = 1 THEN 1 ELSE 0 END)
            FROM latency_sample s
            JOIN monitor_selection ms
              ON ms.group_name = $group_name
             AND ms.tag = s.tag
             AND ms.enabled = 1
            WHERE s.measured_at_ms >= $from_30
              AND s.measured_at_ms < $to_ms
            GROUP BY s.tag;
            """;
        command.Parameters.AddWithValue("$group_name", groupName);
        command.Parameters.AddWithValue("$from_24", from24);
        command.Parameters.AddWithValue("$from_7", from7);
        command.Parameters.AddWithValue("$from_30", from30);
        command.Parameters.AddWithValue("$to_ms", toMs);

        var result = new Dictionary<string, StatisticsTriple>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = new StatisticsTriple(
                ReadPeriod(reader, 1, 2, 3),
                ReadPeriod(reader, 4, 5, 6),
                ReadPeriod(reader, 7, 8, 9));
        }

        return result;
    }

    private static PeriodStatistics ReadPeriod(
        SqliteDataReader reader,
        int averageIndex,
        int attemptsIndex,
        int successesIndex) =>
        new(
            reader.IsDBNull(averageIndex) ? null : reader.GetDouble(averageIndex),
            reader.IsDBNull(attemptsIndex) ? 0 : reader.GetInt32(attemptsIndex),
            reader.IsDBNull(successesIndex) ? 0 : reader.GetInt32(successesIndex));

    private static string ToDatabaseValue(ProbeRunStatus status) => status switch
    {
        ProbeRunStatus.Running => "running",
        ProbeRunStatus.Complete => "complete",
        ProbeRunStatus.Partial => "partial",
        ProbeRunStatus.ControllerOffline => "controller_offline",
        ProbeRunStatus.Cancelled => "cancelled",
        ProbeRunStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static string ToDatabaseValue(DashboardSortKey key) => key switch
    {
        DashboardSortKey.Default => "default",
        DashboardSortKey.Hours24Delay => "delay_24h",
        DashboardSortKey.Hours24Availability => "availability_24h",
        DashboardSortKey.Days7Delay => "delay_7d",
        DashboardSortKey.Days7Availability => "availability_7d",
        DashboardSortKey.Days30Delay => "delay_30d",
        DashboardSortKey.Days30Availability => "availability_30d",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    private static DashboardSortKey ParseDashboardSortKey(string value) => value switch
    {
        "delay_24h" => DashboardSortKey.Hours24Delay,
        "availability_24h" => DashboardSortKey.Hours24Availability,
        "delay_7d" => DashboardSortKey.Days7Delay,
        "availability_7d" => DashboardSortKey.Days7Availability,
        "delay_30d" => DashboardSortKey.Days30Delay,
        "availability_30d" => DashboardSortKey.Days30Availability,
        _ => DashboardSortKey.Default
    };

    private sealed record GroupMemberOrder(string Tag, bool IsPresent);

    private sealed record StatisticsTriple(
        PeriodStatistics Hours24,
        PeriodStatistics Days7,
        PeriodStatistics Days30)
    {
        public static StatisticsTriple Empty { get; } = new(
            new PeriodStatistics(null, 0, 0),
            new PeriodStatistics(null, 0, 0),
            new PeriodStatistics(null, 0, 0));
    }
}
