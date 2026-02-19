using BTCPayServer.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk.Core.Wallet;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public class ArkPluginMigrationRunner(
    ILogger<ArkPluginMigrationRunner> logger,
    ArkPluginDbContextFactory dbContextFactory,
    ISettingsRepository settingsRepository) : IStartupTask
{
    private class ArkPluginDataMigrationHistory
    {
        public bool InitialSetup { get; set; }
        public bool NNArkMigration { get; set; }
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var settings =
            await settingsRepository.GetSettingAsync<ArkPluginDataMigrationHistory>() ??
            new ArkPluginDataMigrationHistory();

        await using var ctx = dbContextFactory.CreateContext();
        var pendingMigrations = (await ctx.Database.GetPendingMigrationsAsync(cancellationToken: cancellationToken)).ToList();
        if (pendingMigrations.Count != 0)
        {
            logger.LogInformation("Applying {Count} migrations", pendingMigrations.Count);
            await ctx.Database.MigrateAsync(cancellationToken: cancellationToken);
        }
        else
        {
            logger.LogInformation("No migrations to apply");
        }
        if (!settings.InitialSetup)
        {
            settings.InitialSetup = true;
            await settingsRepository.UpdateSetting(settings);
        }
        if (!settings.NNArkMigration)
        {
            var wallets = await ctx.Wallets.Where(wallet => wallet.AccountDescriptor == "TODO_MIGRATION")
                .ToListAsync(cancellationToken: cancellationToken);
            foreach (var wallet in wallets)
            {
                wallet.AccountDescriptor =
                    WalletFactory.GetOutputDescriptorFromNsec(wallet.Wallet);
            }
            var result = await ctx.SaveChangesAsync(cancellationToken: cancellationToken);
            
            settings.NNArkMigration = true;
            await settingsRepository.UpdateSetting(settings);
            logger.LogInformation("Migrated {Count} wallets", result);
        }
    }
}