using Crm.Web.Data;
using Microsoft.Extensions.Configuration;

namespace Crm.Web.Config;

/// <summary>migrate | seed-test | run-sql (dotnet.md 4 and 7). Loads only the database configuration.</summary>
public static class CliRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        DbConfig db;
        TestAdmin? admin = null;
        try
        {
            db = CrmConfigLoader.LoadDb(configuration);
            if (args[0] == "seed-test")
            {
                admin = CrmConfigLoader.LoadTestAdmin(configuration["TEST_ADMIN_NAME"], configuration["TEST_ADMIN_EMAIL"]);
            }
        }
        catch (ConfigException e)
        {
            await Console.Error.WriteLineAsync(e.Message);
            return 1;
        }
        if (args[0] == "run-sql" && args.Length != 2)
        {
            await Console.Error.WriteLineAsync("usage: run-sql <file.sql>");
            return 1;
        }
        var migrator = new Migrator(new SqlConnectionFactory(db), Console.Out, Console.Error);
        return args[0] switch
        {
            "migrate" => await migrator.MigrateAsync(CancellationToken.None),
            "seed-test" => await migrator.SeedTestAsync(admin, CancellationToken.None),
            _ => await migrator.RunSqlFileAsync(args[1], CancellationToken.None),
        };
    }
}
