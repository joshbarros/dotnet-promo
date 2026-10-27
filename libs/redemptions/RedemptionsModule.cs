using Promo.Shared;
using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Promo.Redemptions;

public static class RedemptionsModule
{
    public static IServiceCollection AddRedemptionsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Promo") ?? throw new InvalidOperationException("Connection string 'Promo' is not configured.");
        services.AddDbContext<RedemptionsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", RedemptionsDbContext.SchemaName)));
        services.Configure<RedemptionOptions>(config.GetSection("Redemptions"));
        services.AddScoped<RedemptionService>();
        services.AddScoped<RedemptionsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<RedemptionsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<RedemptionsDbContext>>("redemptions-db", tags: ["ready"]);

        messaging.AddOutbox<RedemptionsDbContext>()
            .AddConsumer<RedemptionsHandler>(Topology.RedemptionsQueue,
                [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
