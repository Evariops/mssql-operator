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
        catch
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

    public async Task UpdateJobAsync(SqlConnection connection, string jobName, string? description, bool enabled, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            EXEC msdb.dbo.sp_update_job
                @job_name = @name,
                @description = @desc,
                @enabled = @enabled
            """;
        cmd.Parameters.AddWithValue("@name", jobName);
        cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
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

    public async Task CreateJobStepAsync(SqlConnection connection, string jobName, JobStepDefinition step, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // GoToStep needs its target step id, which used to be validated and then dropped.
        cmd.CommandText = """
            EXEC msdb.dbo.sp_add_jobstep
                @job_name = @jobName,
                @step_name = @stepName,
                @step_id = @stepId,
                @subsystem = @subsystem,
                @database_name = @database,
                @command = @command,
                @on_success_action = @onSuccess,
                @on_success_step_id = @onSuccessStep,
                @on_fail_action = @onFailure,
                @on_fail_step_id = @onFailStep,
                @retry_attempts = @retryAttempts,
                @retry_interval = @retryInterval
            """;
        cmd.Parameters.AddWithValue("@jobName", jobName);
        cmd.Parameters.AddWithValue("@stepName", step.Name);
        cmd.Parameters.AddWithValue("@stepId", step.StepId);
        cmd.Parameters.AddWithValue("@subsystem", step.Subsystem);
        cmd.Parameters.AddWithValue("@database", (object?)step.Database ?? "master");
        cmd.Parameters.AddWithValue("@command", step.Command);
        cmd.Parameters.AddWithValue("@onSuccess", StepActionMap.GetValueOrDefault(step.OnSuccess, 3));
        cmd.Parameters.AddWithValue("@onSuccessStep", step.OnSuccessStepId ?? 0);
        cmd.Parameters.AddWithValue("@onFailure", StepActionMap.GetValueOrDefault(step.OnFailure, 2));
        cmd.Parameters.AddWithValue("@onFailStep", step.OnFailStepId ?? 0);
        cmd.Parameters.AddWithValue("@retryAttempts", step.RetryAttempts);
        cmd.Parameters.AddWithValue("@retryInterval", step.RetryIntervalMinutes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAllJobStepsAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DECLARE @job_id UNIQUEIDENTIFIER = (SELECT job_id FROM msdb.dbo.sysjobs WHERE name = @name);
            IF @job_id IS NOT NULL
            BEGIN
                DECLARE @step_id INT;
                DECLARE c CURSOR FOR SELECT step_id FROM msdb.dbo.sysjobsteps WHERE job_id = @job_id ORDER BY step_id DESC;
                OPEN c;
                FETCH NEXT FROM c INTO @step_id;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    EXEC msdb.dbo.sp_delete_jobstep @job_id = @job_id, @step_id = @step_id;
                    FETCH NEXT FROM c INTO @step_id;
                END;
                CLOSE c;
                DEALLOCATE c;
            END
            """;
        cmd.Parameters.AddWithValue("@name", jobName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateScheduleAsync(SqlConnection connection, string jobName, JobScheduleDefinition schedule, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // freq_recurrence_factor is required (>= 1) for weekly and monthly schedules, the
        // sub-day fields make "every N minutes" expressible (log backups), and the start date was
        // validated and never passed.
        cmd.CommandText = """
            EXEC msdb.dbo.sp_add_jobschedule
                @job_name = @jobName,
                @name = @scheduleName,
                @enabled = @enabled,
                @freq_type = @freqType,
                @freq_interval = @freqInterval,
                @freq_recurrence_factor = @recurrence,
                @freq_subday_type = @subdayType,
                @freq_subday_interval = @subdayInterval,
                @active_start_time = @startTime,
                @active_start_date = @startDate
            """;
        cmd.Parameters.AddWithValue("@jobName", jobName);
        cmd.Parameters.AddWithValue("@scheduleName", schedule.Name);
        cmd.Parameters.AddWithValue("@enabled", schedule.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@freqType", schedule.FreqType);
        cmd.Parameters.AddWithValue("@freqInterval", schedule.FreqInterval);
        cmd.Parameters.AddWithValue("@recurrence", schedule.RecurrenceFactor);
        cmd.Parameters.AddWithValue("@subdayType", schedule.SubdayType);
        cmd.Parameters.AddWithValue("@subdayInterval", schedule.SubdayInterval);
        cmd.Parameters.AddWithValue("@startTime", schedule.ActiveStartTime);
        cmd.Parameters.AddWithValue("@startDate", (object?)schedule.ActiveStartDate ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAllSchedulesAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DECLARE @job_id UNIQUEIDENTIFIER = (SELECT job_id FROM msdb.dbo.sysjobs WHERE name = @name);
            IF @job_id IS NOT NULL
            BEGIN
                DECLARE @schedule_id INT;
                DECLARE c CURSOR FOR
                    SELECT s.schedule_id FROM msdb.dbo.sysjobschedules js
                    JOIN msdb.dbo.sysschedules s ON js.schedule_id = s.schedule_id
                    WHERE js.job_id = @job_id;
                OPEN c;
                FETCH NEXT FROM c INTO @schedule_id;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    EXEC msdb.dbo.sp_delete_schedule @schedule_id = @schedule_id, @force_delete = 1;
                    FETCH NEXT FROM c INTO @schedule_id;
                END;
                CLOSE c;
                DEALLOCATE c;
            END
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

    public async Task SetJobNotificationAsync(SqlConnection connection, string jobName, int notifyLevelEmail, string? emailOperatorName, int notifyLevelEventLog, int notifyLevelPage, string? pageOperatorName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            EXEC msdb.dbo.sp_update_job
                @job_name = @jobName,
                @notify_level_email = @notifyLevelEmail,
                @notify_email_operator_name = @emailOperator,
                @notify_level_eventlog = @notifyLevelEventLog,
                @notify_level_page = @notifyLevelPage,
                @notify_page_operator_name = @pageOperator
            """;
        cmd.Parameters.AddWithValue("@jobName", jobName);
        cmd.Parameters.AddWithValue("@notifyLevelEmail", notifyLevelEmail);
        cmd.Parameters.AddWithValue("@emailOperator", (object?)emailOperatorName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@notifyLevelEventLog", notifyLevelEventLog);
        cmd.Parameters.AddWithValue("@notifyLevelPage", notifyLevelPage);
        cmd.Parameters.AddWithValue("@pageOperator", (object?)pageOperatorName ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
