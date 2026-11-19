using Crm.Web.Data;
using Microsoft.Extensions.Configuration;

namespace Crm.Web.Config;

/// <summary>migrate | seed-test | seed-demo | run-sql (dotnet.md 4 and 7). Loads only the database configuration.</summary>
public static class CliRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        if (args[0] == "seed-demo" && DemoArgsError(args, configuration) is { } demoError)
        {
            await Console.Error.WriteLineAsync(demoError);
            return 1;
        }
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
            "seed-demo" => await migrator.SeedDemoAsync(args.Contains("--reset-demo"), TimeProvider.System, CancellationToken.None, args.Contains("--edge-fx"), args.Contains("--demo-settings"), args.Contains("--purge-legacy-demo")),
            _ => await migrator.RunSqlFileAsync(args[1], CancellationToken.None),
        };
    }

    private const string DemoUsage = "usage: seed-demo --yes-demo [--reset-demo] [--edge-fx] [--demo-settings] [--purge-legacy-demo]";

    /// <summary>
    /// Safety gate for seed-demo: refused on live/Azure hosts, needs the explicit flag, rejects unknown options and checks the target
    /// (environment variables here; the test marker in the database is checked by the seeder). Null = go ahead.
    /// </summary>
    internal static string? DemoArgsError(string[] args, IConfiguration configuration)
    {
        if (CrmConfigLoader.DemoRefusal(configuration) is { } refusal)
        {
            return refusal;
        }
        var options = args.Skip(1).ToList();
        if (options.Exists(o => o is not ("--yes-demo" or "--reset-demo" or "--edge-fx" or "--demo-settings" or "--purge-legacy-demo")))
        {
            return DemoUsage;
        }
        if (!options.Contains("--yes-demo"))
        {
            return "seed-demo writes a few thousand fictitious rows (accounts, deals, activities, audit and FX rows) into the database. Add --yes-demo to confirm this is a test database. " + DemoUsage;
        }
        return CrmConfigLoader.DemoTargetRefusal(configuration);
    }
}
