namespace LiteDb.Distributed.Studio.Models;

public enum CredentialType
{
    ApiKey,
    Password
}

public class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Local Node";
    public string BaseUrl { get; set; } = "http://localhost:1446";
    public string Database { get; set; } = "testapp";
    public CredentialType CredentialType { get; set; } = CredentialType.ApiKey;
    public string Credential { get; set; } = "sample-local-key";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public string CredentialHeaderName => CredentialType == CredentialType.Password ? "Password" : "ApiKey";

    public ConnectionProfile Clone()
    {
        return new ConnectionProfile
        {
            Id = Id,
            Name = Name,
            BaseUrl = BaseUrl,
            Database = Database,
            CredentialType = CredentialType,
            Credential = Credential,
            UpdatedUtc = UpdatedUtc
        };
    }

    public static ConnectionProfile CreateDefault() => new();
}

