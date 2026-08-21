namespace RDM.Infrastructure.Database;

public interface IMigration
{
    string Version { get; }
    string Description { get; }
    string UpSql { get; }
}
