// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Generates the file-provider (dynamic) configuration used by the managed Traefik in run mode.
/// </summary>
/// <remarks>
/// The output is YAML because the file provider reads YAML or TOML, not JSON:
/// <code>
/// http:
///   routers:
///     api:
///       rule: Host(`api.dev.localhost`)
///       entryPoints:
///       - web
///       service: api
///   services:
///     api:
///       loadBalancer:
///         servers:
///         - url: http://host.docker.internal:5123
/// </code>
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/routing/router/"/>.
/// </remarks>
internal static class TraefikDynamicConfiguration
{
    internal const string FileName = "aspire.yml";

    public static async Task<string> GenerateAsync(TraefikResource traefik, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        // Backend URLs and hosts are resolved from Traefik's point of view: containers by their name on the
        // Aspire container network, host processes through the container host (host.docker.internal, or the
        // Aspire container tunnel), so nothing about Docker Desktop or the tunnel is hardcoded here.
        var context = new ValueProviderContext
        {
            ExecutionContext = executionContext,
            Caller = traefik,
            Network = KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
        };

        var defaults = ((ITraefikRouteTarget)traefik).GetRouteDefaults(isPublishMode: false);
        var routers = new Dictionary<string, object>();
        var services = new Dictionary<string, object>();

        foreach (var route in traefik.Routes)
        {
            var router = new Dictionary<string, object>
            {
                ["rule"] = await TraefikRouteRule.Build(route.Options).GetValueAsync(context, cancellationToken).ConfigureAwait(false) ?? string.Empty,
                ["entryPoints"] = new[] { route.Options.EntryPoint ?? defaults.EntryPointName },
                ["service"] = route.RouterName,
            };

            if (route.Options.Priority is int priority)
            {
                router["priority"] = priority;
            }

            if (route.Middlewares.Count > 0)
            {
                // Middlewares defined in this same file need no `@file` provider suffix.
                router["middlewares"] = route.Middlewares.Select(m => m.Name).ToArray();
            }

            if (defaults.Tls && route.Options.EntryPoint is null)
            {
                // An empty `tls` section enables TLS with the default certificate. No resolver in run mode: see
                // TraefikResource.GetRouteDefaults.
                router["tls"] = new Dictionary<string, object>();
            }

            routers[route.RouterName] = router;

            var url = await route.Endpoint.GetValueAsync(context, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The endpoint '{route.Endpoint.EndpointName}' of resource '{route.Resource.Name}' has no URL.");

            services[route.RouterName] = new Dictionary<string, object>
            {
                ["loadBalancer"] = new Dictionary<string, object>
                {
                    ["servers"] = new[] { new Dictionary<string, object> { ["url"] = url } },
                },
            };
        }

        // Every middleware of this Traefik is written, including unused ones, which Traefik accepts. Routes can also
        // carry middlewares that aren't resources, such as the one behind TraefikRouteOptions.StripPathPrefix.
        var middlewares = new Dictionary<string, object>();
        foreach (var middleware in traefik.Middlewares.Concat(traefik.Routes.SelectMany(r => r.Middlewares)).Distinct())
        {
            middlewares[middleware.Name] = new Dictionary<string, object>
            {
                [middleware.Type] = await middleware.Options.ToFileConfigurationAsync(context, cancellationToken).ConfigureAwait(false),
            };
        }

        var http = new Dictionary<string, object>();
        if (routers.Count > 0)
        {
            http["routers"] = routers;
            http["services"] = services;
        }

        if (middlewares.Count > 0)
        {
            http["middlewares"] = middlewares;
        }

        var document = new Dictionary<string, object>();
        if (http.Count > 0)
        {
            document["http"] = http;
        }

        var serializer = new SerializerBuilder()
            .DisableAliases()
            // Quote strings that YAML would read as another type, such as the string `port: "8443"` of redirectScheme
            // or a header value of `true`; Traefik's decoder doesn't convert an int into a string field.
            .WithQuotingNecessaryStrings()
            .Build();

        return serializer.Serialize(document);
    }
}
