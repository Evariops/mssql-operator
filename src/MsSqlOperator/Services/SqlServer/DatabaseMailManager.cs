using Microsoft.Data.SqlClient;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.SqlServer;

public class DatabaseMailManager : IDatabaseMailManager
{
    public Task EnableDatabaseMailAsync(SqlConnection connection, CancellationToken ct = default)
        // 'show advanced options' is put back as it was found. Nothing is committed
        // while another value is armed.
        => SpConfigure.ApplyAsync(connection, new Dictionary<string, int> { ["Database Mail XPs"] = 1 }, ct);

    public async Task CreateMailProfileAsync(SqlConnection connection, string profileName, string? description, bool isDefault, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysmail_profile WHERE name = @profileName)
                EXEC msdb.dbo.sysmail_add_profile_sp @profile_name = @profileName, @description = @description;
            """;
        cmd.Parameters.AddWithValue("@profileName", profileName);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        if (isDefault)
        {
            await using var defaultCmd = connection.CreateCommand();
            defaultCmd.CommandText = """
                IF NOT EXISTS (
                    SELECT 1 FROM msdb.dbo.sysmail_principalprofile pp
                    JOIN msdb.dbo.sysmail_profile p ON p.profile_id = pp.profile_id
                    WHERE p.name = @profileName AND pp.is_default = 1
                )
                    EXEC msdb.dbo.sysmail_add_principalprofile_sp @principal_name = 'public', @profile_name = @profileName, @is_default = 1;
                """;
            defaultCmd.Parameters.AddWithValue("@profileName", profileName);
            await defaultCmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task CreateOrUpdateMailAccountAsync(SqlConnection connection, string accountName, string? description,
        string emailAddress, string? displayName, string? replyTo,
        string smtpServer, int port, bool useSsl, string? username, string? password, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysmail_account WHERE name = @accountName)
                EXEC msdb.dbo.sysmail_add_account_sp
                    @account_name = @accountName, @description = @description,
                    @email_address = @emailAddress, @display_name = @displayName,
                    @replyto_address = @replyTo, @mailserver_name = @smtpServer,
                    @mailserver_type = 'SMTP', @port = @port,
                    @enable_ssl = @useSsl, @username = @username, @password = @password;
            ELSE
                EXEC msdb.dbo.sysmail_update_account_sp
                    @account_name = @accountName, @email_address = @emailAddress,
                    @display_name = @displayName, @replyto_address = @replyTo,
                    @mailserver_name = @smtpServer, @port = @port,
                    @enable_ssl = @useSsl, @username = @username, @password = @password;
            """;
        cmd.Parameters.AddWithValue("@accountName", accountName);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@emailAddress", emailAddress);
        cmd.Parameters.AddWithValue("@displayName", (object?)displayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@replyTo", (object?)replyTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@smtpServer", smtpServer);
        cmd.Parameters.AddWithValue("@port", port);
        cmd.Parameters.AddWithValue("@useSsl", useSsl ? 1 : 0);
        cmd.Parameters.AddWithValue("@username", (object?)username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@password", (object?)password ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AssociateAccountToProfileAsync(SqlConnection connection, string profileName, string accountName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (
                SELECT 1 FROM msdb.dbo.sysmail_profileaccount pa
                JOIN msdb.dbo.sysmail_profile p ON p.profile_id = pa.profile_id
                JOIN msdb.dbo.sysmail_account a ON a.account_id = pa.account_id
                WHERE p.name = @profileName AND a.name = @accountName
            )
                EXEC msdb.dbo.sysmail_add_profileaccount_sp @profile_name = @profileName, @account_name = @accountName, @sequence_number = 1;
            """;
        cmd.Parameters.AddWithValue("@profileName", profileName);
        cmd.Parameters.AddWithValue("@accountName", accountName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateOrUpdateOperatorAsync(SqlConnection connection, string operatorName, bool enabled,
        string? emailAddress, string? pagerAddress,
        string? weekdayPagerStart, string? weekdayPagerEnd,
        string? saturdayPagerStart, string? saturdayPagerEnd, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysoperators WHERE name = @name)
                EXEC msdb.dbo.sp_add_operator
                    @name = @name, @enabled = @enabled, @email_address = @emailAddress,
                    @pager_address = @pagerAddress;
            ELSE
                EXEC msdb.dbo.sp_update_operator
                    @name = @name, @enabled = @enabled, @email_address = @emailAddress;
            """;
        cmd.Parameters.AddWithValue("@name", operatorName);
        cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@emailAddress", (object?)emailAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pagerAddress", (object?)pagerAddress ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ConfigureAsync(SqlConnection connection, DatabaseMailSpec spec, Func<SecretReference, CancellationToken, Task<string?>>? resolveSecret = null, CancellationToken ct = default)
    {
        await EnableDatabaseMailAsync(connection, ct);

        if (spec.Profiles != null)
        {
            foreach (var profile in spec.Profiles)
            {
                await CreateMailProfileAsync(connection, profile.Name, profile.Description, profile.Default, ct);
            }
        }

        if (spec.Accounts != null)
        {
            foreach (var account in spec.Accounts)
            {
                // Resolve SMTP credentials from Kubernetes secret if referenced
                string? smtpPassword = null;
                if (account.Smtp.CredentialsSecretRef != null && resolveSecret != null)
                {
                    smtpPassword = await resolveSecret(account.Smtp.CredentialsSecretRef, ct);
                }

                await CreateOrUpdateMailAccountAsync(connection, account.Name, account.Description,
                    account.EmailAddress, account.DisplayName, account.ReplyTo,
                    account.Smtp.Server, account.Smtp.Port, account.Smtp.UseSsl,
                    account.Smtp.Username, smtpPassword, ct);

                if (account.Profiles != null)
                {
                    foreach (var profileName in account.Profiles)
                    {
                        await AssociateAccountToProfileAsync(connection, profileName, account.Name, ct);
                    }
                }
            }
        }
    }

    public async Task ConfigureOperatorsAsync(SqlConnection connection, List<AgentOperatorSpec> operators, CancellationToken ct = default)
    {
        foreach (var op in operators)
        {
            await CreateOrUpdateOperatorAsync(connection, op.Name, op.Enabled,
                op.EmailAddress, op.PagerAddress,
                op.WeekdayPagerStartTime, op.WeekdayPagerEndTime,
                op.SaturdayPagerStartTime, op.SaturdayPagerEndTime, ct);
        }
    }
}
