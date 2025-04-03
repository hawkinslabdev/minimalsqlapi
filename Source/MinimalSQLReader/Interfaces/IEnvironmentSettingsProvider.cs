namespace MinimalSqlReader.Interfaces;

public interface IEnvironmentSettingsProvider
{
    Task<(string ConnectionString, string ServerName)> LoadEnvironmentOrThrowAsync(string env);
}
