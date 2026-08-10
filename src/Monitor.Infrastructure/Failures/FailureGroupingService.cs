using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Monitor.Domain;

namespace Monitor.Infrastructure.Failures;

public sealed class FailureGroupingService(
    MonitorDbContext db,
    FailureClassifier classifier,
    ILogger<FailureGroupingService> logger)
{
    private const string LockResource = "Monitor.FailureGrouping";

    public async Task<int> GroupPendingAsync(CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await TryAcquireLockAsync(db.Database.GetDbConnection(), cancellationToken))
            {
                return 0;
            }

            try
            {
                var runs = await db.Runs
                    .Include(x => x.Spans)
                    .Where(x =>
                        x.FailureGroupId == null &&
                        (x.Status == RunStatus.Failed || x.Status == RunStatus.Cancelled))
                    .OrderBy(x => x.CompletedAt)
                    .ThenBy(x => x.Sequence)
                    .Take(1000)
                    .ToListAsync(cancellationToken);

                if (runs.Count == 0)
                {
                    return 0;
                }

                var classified = runs
                    .Select(run => new ClassifiedRun(run, classifier.Classify(run)))
                    .ToList();
                var fingerprints = classified
                    .Select(x => x.Descriptor.Fingerprint)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var existingGroups = await db.FailureGroups
                    .Where(x => fingerprints.Contains(x.Fingerprint))
                    .ToDictionaryAsync(x => x.Fingerprint, StringComparer.Ordinal, cancellationToken);

                foreach (var fingerprintGroup in classified.GroupBy(x => x.Descriptor.Fingerprint, StringComparer.Ordinal))
                {
                    var first = fingerprintGroup.First();
                    if (!existingGroups.TryGetValue(fingerprintGroup.Key, out var group))
                    {
                        var seenAt = first.Run.CompletedAt ?? first.Run.StartedAt;
                        group = FailureGroup.Create(
                            first.Descriptor.Fingerprint,
                            first.Descriptor.Category,
                            first.Descriptor.FailureType,
                            first.Descriptor.Operation,
                            first.Descriptor.Dependency,
                            first.Descriptor.HttpStatusCode,
                            first.Descriptor.MessageTemplate,
                            seenAt);
                        db.FailureGroups.Add(group);
                        existingGroups.Add(group.Fingerprint, group);

                        foreach (var item in fingerprintGroup.Skip(1))
                        {
                            group.RecordOccurrence(item.Run.CompletedAt ?? item.Run.StartedAt);
                        }
                    }
                    else
                    {
                        foreach (var item in fingerprintGroup)
                        {
                            group.RecordOccurrence(item.Run.CompletedAt ?? item.Run.StartedAt);
                        }
                    }

                    foreach (var item in fingerprintGroup)
                    {
                        item.Run.AssignFailureGroup(group.Id);
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Grouped {FailureCount} failed/cancelled runs into {GroupCount} fingerprints.", runs.Count, classified.Select(x => x.Descriptor.Fingerprint).Distinct().Count());
                return runs.Count;
            }
            finally
            {
                await ReleaseLockAsync(db.Database.GetDbConnection(), cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> TryAcquireLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = 0;
            SELECT @result;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = LockResource;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) >= 0;
    }

    private static async Task ReleaseLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = LockResource;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record ClassifiedRun(AgentRun Run, FailureDescriptor Descriptor);
}
