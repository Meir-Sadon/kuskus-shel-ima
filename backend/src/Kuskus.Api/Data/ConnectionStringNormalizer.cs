using Npgsql;

namespace Kuskus.Api.Data;

/// <summary>Hosts such as Render hand out the database as a postgres:// URL; Npgsql wants key=value pairs.</summary>
public static class ConnectionStringNormalizer
{
    public static string? Normalize(string? value)
    {
        if (value is null || !(value.StartsWith("postgres://") || value.StartsWith("postgresql://")))
            return value;

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        }.ConnectionString;
    }
}
