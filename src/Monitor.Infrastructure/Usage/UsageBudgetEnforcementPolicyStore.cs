using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Monitor.Domain;

namespace Monitor.Infrastructure.Usage;

public sealed class UsageBudgetEnforcementPolicyStore(MonitorDbContext db)
{
    public async Task<UsageBudgetEnforcementAction> GetCriticalActionAsync(
        Guid budgetId,
        CancellationToken cancellationToken = default)
    {
        var actions = await GetCriticalActionsAsync([budgetId], cancellationToken);
        return actions.TryGetValue(budgetId, out var action)
            ? action
            : UsageBudgetEnforcementAction.None;
    }

    public async Task<IReadOnlyDictionary<Guid, UsageBudgetEnforcementAction>> GetCriticalActionsAsync(
        IEnumerable<Guid> budgetIds,
        CancellationToken cancellationToken = default)
    {
        var requested = budgetIds.Distinct().ToHashSet();
        if (requested.Count == 0)
        {
            return new Dictionary<Guid, UsageBudgetEnforcementAction>();
        }

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            AttachCurrentTransaction(command);
            command.CommandText = "SELECT UsageBudgetId, CriticalAction FROM UsageBudgetEnforcementPolicies;";

            var result = new Dictionary<Guid, UsageBudgetEnforcementAction>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var budgetId = reader.GetGuid(0);
                if (!requested.Contains(budgetId))
                {
                    continue;
                }

                var raw = reader.GetInt32(1);
                if (raw is (int)UsageBudgetEnforcementAction.Pause or (int)UsageBudgetEnforcementAction.Disable)
                {
                    result[budgetId] = (UsageBudgetEnforcementAction)raw;
                }
            }

            return result;
        }
        finally
        {
            if (openedHere)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    public async Task SetCriticalActionAsync(
        Guid budgetId,
        UsageBudgetEnforcementAction action,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (action == UsageBudgetEnforcementAction.None)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM UsageBudgetEnforcementPolicies WHERE UsageBudgetId = {budgetId};",
                cancellationToken);
            return;
        }

        if (action is not UsageBudgetEnforcementAction.Pause and not UsageBudgetEnforcementAction.Disable)
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            MERGE UsageBudgetEnforcementPolicies WITH (HOLDLOCK) AS target
            USING (SELECT {budgetId} AS UsageBudgetId) AS source
                ON target.UsageBudgetId = source.UsageBudgetId
            WHEN MATCHED THEN
                UPDATE SET CriticalAction = {(int)action}, UpdatedAt = {now}
            WHEN NOT MATCHED THEN
                INSERT (UsageBudgetId, CriticalAction, UpdatedAt)
                VALUES ({budgetId}, {(int)action}, {now});
            """, cancellationToken);
    }

    private void AttachCurrentTransaction(DbCommand command)
    {
        var transaction = db.Database.CurrentTransaction;
        if (transaction is not null)
        {
            command.Transaction = transaction.GetDbTransaction();
        }
    }
}
