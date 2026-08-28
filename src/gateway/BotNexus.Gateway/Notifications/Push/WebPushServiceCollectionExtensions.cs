using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// DI registration for web push delivery.
/// </summary>
public static class WebPushServiceCollectionExtensions
{
    /// <summary>
    /// Registers the subscription store, the VAPID identity, the sender and the bridge.
    /// </summary>
    /// <remarks>
    /// Registered unconditionally. With no subscriptions the sender does nothing on each
    /// notification and the bridge idles, which costs nothing and means a device can subscribe at
    /// any time without a restart - whereas a flag would have to be found and set first, by
    /// someone who does not yet know the feature exists.
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <param name="dbPath">Path to the subscriptions database.</param>
    /// <param name="vapidPath">Path to the VAPID key file.</param>
    /// <param name="subject">Operator contact for push services: a mailto: or https: URI.</param>
    /// <param name="fileSystem">Filesystem abstraction; resolved from DI when omitted.</param>
    public static IServiceCollection AddBotNexusWebPush(
        this IServiceCollection services,
        string dbPath,
        string vapidPath,
        string subject,
        IFileSystem? fileSystem = null)
    {
        services.TryAddSingleton<IPushSubscriptionStore>(sp =>
            new SqlitePushSubscriptionStore(
                dbPath,
                fileSystem ?? sp.GetService<IFileSystem>(),
                sp.GetService<TimeProvider>()));

        services.TryAddSingleton(new VapidKeyStore(vapidPath, subject));

        // A named client so the push-service timeout is short: a slow push service must not hold
        // the bridge up behind it while other subscribers wait.
        services.AddHttpClient<WebPushSender>(client => client.Timeout = TimeSpan.FromSeconds(15));

        services.AddHostedService<WebPushBridge>();

        return services;
    }
}
