using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public class DatabaseMailManager : IDatabaseMailManager
{
    public async Task EnableDatabaseMailAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            EXEC sp_configure 'show advanced options', 1; RECONFIGURE;
            EXEC sp_configure 'Database Mail XPs', 1; RECONFIGURE;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

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
}
