using Promo.Shared;
using Promo.Shared.Contracts;
using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Promo.Campaigns;

public static class CampaignsModule
{
    public static IServiceCollection AddCampaignsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Promo") ?? throw new InvalidOperationException("Connection string 'Promo' is not configured.");
        services.AddDbContext<CampaignsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", CampaignsDbContext.SchemaName)));
        services.AddScoped<CampaignsHandler>();
        services.AddScoped<ICampaignCatalog, CampaignCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<CampaignsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<CampaignsDbContext>>("uses-db", tags: ["ready"]);

        messaging.AddOutbox<CampaignsDbContext>()
            .AddConsumer<CampaignsHandler>(Topology.CampaignsQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
