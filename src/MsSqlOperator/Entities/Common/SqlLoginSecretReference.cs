namespace MsSqlOperator.Entities.Common;

public class SqlLoginSecretReference
{
    /// <summary>Secret name containing login and password keys.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Key in the Secret holding the SQL login name.</summary>
    public string LoginKey { get; set; } = "login";

    /// <summary>Key in the Secret holding the SQL password.</summary>
    public string PasswordKey { get; set; } = "password";
}
