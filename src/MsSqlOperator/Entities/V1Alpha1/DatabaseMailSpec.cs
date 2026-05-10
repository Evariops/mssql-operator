using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class DatabaseMailSpec
{
    public bool Enabled { get; set; }
    public List<MailProfileSpec>? Profiles { get; set; }
    public List<MailAccountSpec>? Accounts { get; set; }
}

public class MailProfileSpec
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Default { get; set; }
}

public class MailAccountSpec
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? ReplyTo { get; set; }
    public SmtpSpec Smtp { get; set; } = new();
    public List<string>? Profiles { get; set; }
}

public class SmtpSpec
{
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public SecretReference? CredentialsSecretRef { get; set; }
    public string? Username { get; set; }
}

public class AgentOperatorSpec
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string? EmailAddress { get; set; }
    public string? PagerAddress { get; set; }
    public string? WeekdayPagerStartTime { get; set; }
    public string? WeekdayPagerEndTime { get; set; }
    public string? SaturdayPagerStartTime { get; set; }
    public string? SaturdayPagerEndTime { get; set; }
}
