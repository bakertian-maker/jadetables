using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GameWeb.Data;

/// <summary>
/// Lets "dotnet ef" build the context for migrations. The connection string comes from
/// configuration (user secrets in development), so it is never written into the repo.
/// </summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<GamesDbContext>
{
    public GamesDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<DesignTimeFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString("Games");
        if (string.IsNullOrWhiteSpace(cs)) cs = "Server=.;Database=Games;Integrated Security=true;TrustServerCertificate=True";
        return new GamesDbContext(new DbContextOptionsBuilder<GamesDbContext>().UseSqlServer(cs).Options);
    }
}
