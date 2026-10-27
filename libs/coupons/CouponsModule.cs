using Promo.Shared;
using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Promo.Coupons;

public static class CouponsModule
{
    public static IServiceCollection AddCouponsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Promo") ?? throw new InvalidOperationException("Connection string 'Promo' is not configured.");
        services.AddDbContext<CouponsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", CouponsDbContext.SchemaName)));
        services.AddScoped<CouponsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<CouponsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<CouponsDbContext>>("coupons-db", tags: ["ready"]);

        messaging.AddOutbox<CouponsDbContext>()
            .AddConsumer<CouponsHandler>(Topology.CouponsQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
