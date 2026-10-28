using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public class SqlAgentManager : ISqlAgentManager
{
    private static readonly Dictionary<string, int> StepActionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "QuitWithSuccess", 1 }, { "QuitWithFailure", 2 }, { "GoToNextStep", 3 }, { "GoToStep", 4 }
    };

    public async Task<bool> IsAgentRunningAsync(SqlConnection connection, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM msdb.dbo.sysjobs";
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    public async Task<Guid?> GetJobIdAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT job_id FROM msdb.dbo.sysjobs WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", jobName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid g ? g : null;
    }

    // The built-in default local job category. Its name literally includes the square
    // brackets; passing "Uncategorized (Local)" (without them) makes sp_add_job fail with
    // "The specified @category_name (...) does not exist."
    private const string DefaultJobCategory = "[Uncategorized (Local)]";

    public async Task<Guid> CreateJobAsync(SqlConnection connection, string jobName, string? description, bool enabled, string? category, string? owner, CancellationToken ct = default)
    {
        var effectiveCategory = string.IsNullOrWhiteSpace(category) ? DefaultJobCategory : category;
        // sp_add_job rejects an unknown @category_name, so make sure it exists first.
        await EnsureJobCategoryAsync(connection, effectiveCategory, ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DECLARE @job_id UNIQUEIDENTIFIER;
            EXEC msdb.dbo.sp_add_job
                @job_name = @name,
                @description = @desc,
                @enabled = @enabled,
                @category_name = @category,
                @owner_login_name = @owner,
                @job_id = @job_id OUTPUT;
            EXEC msdb.dbo.sp_add_jobserver @job_name = @name, @server_name = N'(LOCAL)';
            SELECT @job_id;
            """;
        cmd.Parameters.AddWithValue("@name", jobName);
        cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@category", effectiveCategory);
        cmd.Parameters.AddWithValue("@owner", (object?)owner ?? "sa");
        var result = await cmd.ExecuteScalarAsync(ct);
        return (Guid)result!;
    }

    // Creates a local job category when it is missing. The built-in "[Uncategorized (Local)]"
    // already exists, so this only adds genuinely custom categories. Idempotent.
    private static async Task EnsureJobCategoryAsync(SqlConnection connection, string category, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM msdb.dbo.syscategories
                           WHERE name = @category AND category_class = 1)
                EXEC msdb.dbo.sp_add_category @class = N'JOB', @type = N'LOCAL', @name = @category;
            """;
        cmd.Parameters.AddWithValue("@category", category);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(bool Enabled, int StepCount)?> GetJobShapeAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT j.enabled, (SELECT COUNT(*) FROM msdb.dbo.sysjobsteps s WHERE s.job_id = j.job_id)
            FROM msdb.dbo.sysjobs j WHERE j.name = @name
            """;
        cmd.Parameters.AddWithValue("@name", jobName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return (reader.GetByte(0) == 1, reader.GetInt32(1));
    }

    public async Task ReplaceJobDefinitionAsync(SqlConnection connection, JobDefinition definition, CancellationToken ct = default)
    {
        var category = string.IsNullOrWhiteSpace(definition.Category) ? DefaultJobCategory : definition.Category;
        await using var cmd = connection.CreateCommand();
        // The msdb procedures report their errors with RAISERROR and RETURN 1, which
        // XACT_ABORT does not abort on: the batch went on, set the new description (the marker of a
        // complete definition) and committed a broken one. In a TRY block, the RAISERROR of a nested
        // procedure goes to the CATCH, which rolls everything back.
        var sql = new System.Text.StringBuilder("""
            SET XACT_ABORT ON;
            BEGIN TRY
            BEGIN TRANSACTION;
            IF NOT EXISTS (SELECT 1 FROM msdb.dbo.syscategories WHERE name = @category AND category_class = 1)
                EXEC msdb.dbo.sp_add_category @class = N'JOB', @type = N'LOCAL', @name = @category;
            DECLARE @job_id UNIQUEIDENTIFIER = (SELECT job_id FROM msdb.dbo.sysjobs WHERE name = @name);
            IF @job_id IS NULL
            BEGIN
                EXEC msdb.dbo.sp_add_job @job_name = @name, @enabled = 0, @category_name = @category,
                    @owner_login_name = @owner, @job_id = @job_id OUTPUT;
                EXEC msdb.dbo.sp_add_jobserver @job_id = @job_id, @server_name = N'(LOCAL)';
            END
            ELSE
                EXEC msdb.dbo.sp_update_job @job_id = @job_id, @enabled = 0;
            DECLARE @step_id INT;
            WHILE EXISTS (SELECT 1 FROM msdb.dbo.sysjobsteps WHERE job_id = @job_id)
            BEGIN
                SELECT @step_id = MAX(step_id) FROM msdb.dbo.sysjobsteps WHERE job_id = @job_id;
                EXEC msdb.dbo.sp_delete_jobstep @job_id = @job_id, @step_id = @step_id;
            END
            DECLARE @schedule_id INT;
            WHILE EXISTS (SELECT 1 FROM msdb.dbo.sysjobschedules WHERE job_id = @job_id)
            BEGIN
                SELECT TOP (1) @schedule_id = schedule_id FROM msdb.dbo.sysjobschedules WHERE job_id = @job_id;
                EXEC msdb.dbo.sp_detach_schedule @job_id = @job_id, @schedule_id = @schedule_id, @delete_unused_schedule = 1;
            END

            """);
        cmd.Parameters.AddWithValue("@name", definition.Name);
        cmd.Parameters.AddWithValue("@category", category);
        cmd.Parameters.AddWithValue("@owner", (object?)definition.Owner ?? "sa");

        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            // GoToStep needs its target step id, which used to be validated and then dropped.
            sql.Append($"""
                EXEC msdb.dbo.sp_add_jobstep @job_id = @job_id, @step_name = @s{i}_name, @step_id = @s{i}_id,
                    @subsystem = @s{i}_subsystem, @database_name = @s{i}_database, @command = @s{i}_command,
                    @on_success_action = @s{i}_onSuccess, @on_success_step_id = @s{i}_onSuccessStep,
                    @on_fail_action = @s{i}_onFailure, @on_fail_step_id = @s{i}_onFailStep,
                    @retry_attempts = @s{i}_retryAttempts, @retry_interval = @s{i}_retryInterval;

                """);
            cmd.Parameters.AddWithValue($"@s{i}_name", step.Name);
            cmd.Parameters.AddWithValue($"@s{i}_id", step.StepId);
            cmd.Parameters.AddWithValue($"@s{i}_subsystem", step.Subsystem);
            cmd.Parameters.AddWithValue($"@s{i}_database", (object?)step.Database ?? "master");
            cmd.Parameters.AddWithValue($"@s{i}_command", step.Command);
            cmd.Parameters.AddWithValue($"@s{i}_onSuccess", StepActionMap.GetValueOrDefault(step.OnSuccess, 3));
            cmd.Parameters.AddWithValue($"@s{i}_onSuccessStep", step.OnSuccessStepId ?? 0);
            cmd.Parameters.AddWithValue($"@s{i}_onFailure", StepActionMap.GetValueOrDefault(step.OnFailure, 2));
            cmd.Parameters.AddWithValue($"@s{i}_onFailStep", step.OnFailStepId ?? 0);
            cmd.Parameters.AddWithValue($"@s{i}_retryAttempts", step.RetryAttempts);
            cmd.Parameters.AddWithValue($"@s{i}_retryInterval", step.RetryIntervalMinutes);
        }

        for (var i = 0; i < definition.Schedules.Count; i++)
        {
            var schedule = definition.Schedules[i];
            // freq_recurrence_factor is required (>= 1) for weekly and monthly schedules, the
            // sub-day fields make "every N minutes" expressible (log backups).
            sql.Append($"""
                EXEC msdb.dbo.sp_add_jobschedule @job_id = @job_id, @name = @c{i}_name, @enabled = @c{i}_enabled,
                    @freq_type = @c{i}_freqType, @freq_interval = @c{i}_freqInterval, @freq_recurrence_factor = @c{i}_recurrence,
                    @freq_subday_type = @c{i}_subdayType, @freq_subday_interval = @c{i}_subdayInterval,
                    @active_start_time = @c{i}_startTime, @active_start_date = @c{i}_startDate;

                """);
            cmd.Parameters.AddWithValue($"@c{i}_name", schedule.Name);
            cmd.Parameters.AddWithValue($"@c{i}_enabled", schedule.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue($"@c{i}_freqType", schedule.FreqType);
            cmd.Parameters.AddWithValue($"@c{i}_freqInterval", schedule.FreqInterval);
            cmd.Parameters.AddWithValue($"@c{i}_recurrence", schedule.RecurrenceFactor);
            cmd.Parameters.AddWithValue($"@c{i}_subdayType", schedule.SubdayType);
            cmd.Parameters.AddWithValue($"@c{i}_subdayInterval", schedule.SubdayInterval);
            cmd.Parameters.AddWithValue($"@c{i}_startTime", schedule.ActiveStartTime);
            cmd.Parameters.AddWithValue($"@c{i}_startDate", (object?)schedule.ActiveStartDate ?? DBNull.Value);
        }

        if (definition.Notifications is { } n)
        {
            sql.Append("""
                EXEC msdb.dbo.sp_update_job @job_id = @job_id, @notify_level_email = @notifyEmail,
                    @notify_email_operator_name = @emailOperator, @notify_level_eventlog = @notifyEventLog,
                    @notify_level_page = @notifyPage, @notify_page_operator_name = @pageOperator;

                """);
            cmd.Parameters.AddWithValue("@notifyEmail", n.Email);
            cmd.Parameters.AddWithValue("@emailOperator", (object?)n.EmailOperator ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@notifyEventLog", n.EventLog);
            cmd.Parameters.AddWithValue("@notifyPage", n.Page);
            cmd.Parameters.AddWithValue("@pageOperator", (object?)n.PageOperator ?? DBNull.Value);
        }

        // The marker goes last: a description that carries it promises a complete definition.
        sql.Append("""
            EXEC msdb.dbo.sp_update_job @job_id = @job_id, @enabled = @enabled;
            EXEC msdb.dbo.sp_update_job @job_id = @job_id, @description = @description;
            COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH
            """);
        cmd.Parameters.AddWithValue("@enabled", definition.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@description", definition.Description);
        cmd.CommandText = sql.ToString();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetJobDescriptionAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT description FROM msdb.dbo.sysjobs WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", jobName);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task DeleteJobAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // Idempotent — a CR deleted before its job was ever provisioned must finalize
        // on the first pass instead of erroring forever on sp_delete_job.
        cmd.CommandText = """
            IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @name)
                EXEC msdb.dbo.sp_delete_job @job_name = @name, @delete_unused_schedule = 1
            """;
        cmd.Parameters.AddWithValue("@name", jobName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(DateTime? lastRun, string? lastStatus, DateTime? nextRun)> GetJobStatusAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1
                CASE WHEN jh.run_date > 0 THEN
                    CAST(CAST(jh.run_date AS VARCHAR(8)) AS DATE)
                ELSE NULL END as last_run,
                CASE jh.run_status
                    WHEN 0 THEN 'Failed' WHEN 1 THEN 'Succeeded'
                    WHEN 2 THEN 'Retry' WHEN 3 THEN 'Canceled'
                    ELSE 'Unknown' END as last_status,
                ja.next_scheduled_run_date
            FROM msdb.dbo.sysjobs j
            LEFT JOIN msdb.dbo.sysjobhistory jh ON j.job_id = jh.job_id AND jh.step_id = 0
            LEFT JOIN msdb.dbo.sysjobactivity ja ON j.job_id = ja.job_id
                AND ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
            WHERE j.name = @name
            ORDER BY jh.instance_id DESC
            """;
        cmd.Parameters.AddWithValue("@name", jobName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var lastRun = reader.IsDBNull(0) ? null : (DateTime?)reader.GetDateTime(0);
            var lastStatus = reader.IsDBNull(1) ? null : reader.GetString(1);
            var nextRun = reader.IsDBNull(2) ? null : (DateTime?)reader.GetDateTime(2);
            return (lastRun, lastStatus, nextRun);
        }
        return (null, null, null);
    }

}
