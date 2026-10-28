// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Turns the <see cref="TraefikRouteAnnotation"/>s on routed resources into Traefik configuration.
/// </summary>
/// <remarks>
/// Routes are declared on the routed resource, so they are only complete once the whole model is built. This
/// subscriber runs on <see cref="BeforeStartEvent"/>, which <c>aspire publish</c> also raises before the
/// publishing pipeline, and:
/// <list type="bullet">
///   <item>assigns router names and resolves the backend endpoint of every route;</item>
///   <item>in run mode, records the routes on the managed Traefik (written to its dynamic configuration file)
///     and shows the public URLs on the routed resources in the dashboard;</item>
///   <item>at publish time, adds the <c>traefik.*</c> labels to the routed Docker Compose services.</item>
/// </list>
/// </remarks>
internal sealed class TraefikRoutingSubscriber : IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            ApplyRoutes(@event.Model, executionContext.IsPublishMode);
            return Task.CompletedTask;
        });

        return Task.CompletedTask;
    }

    internal static void ApplyRoutes(DistributedApplicationModel model, bool isPublishMode)
    {
        foreach (var traefik in model.Resources.OfType<TraefikResource>())
        {
            traefik.Routes.Clear();
        }

        // Traefik matches router names case-sensitively in the file provider, but Docker label keys are compared
        // case-insensitively by some tooling, so treat names that differ only by case as duplicates.
        var routerNames = new Dictionary<string, IResource>(StringComparer.OrdinalIgnoreCase);
        var publishesToDockerCompose = isPublishMode && model.Resources.OfType<DockerComposeEnvironmentResource>().Any();

        foreach (var resource in model.Resources)
        {
            var annotations = resource.Annotations.OfType<TraefikRouteAnnotation>().ToList();
            if (annotations.Count == 0)
            {
                continue;
            }

            if (resource is not IResourceWithEndpoints resourceWithEndpoints)
            {
                throw new InvalidOperationException($"Resource '{resource.Name}' is routed through Traefik but has no endpoints.");
            }

            var routes = new List<TraefikRoute>(annotations.Count);

            for (var i = 0; i < annotations.Count; i++)
            {
                var annotation = annotations[i];
                var routerName = annotation.Options.RouterName ?? (i == 0 ? resource.Name : $"{resource.Name}-{i + 1}");

                if (!TraefikNames.IsValidName(routerName))
                {
                    throw new InvalidOperationException($"The Traefik router name '{routerName}' of resource '{resource.Name}' is invalid. Use letters, digits, '-' and '_' only.");
                }

                if (routerNames.TryGetValue(routerName, out var owner))
                {
                    throw new InvalidOperationException($"The Traefik router name '{routerName}' is used by both '{owner.Name}' and '{resource.Name}'. Set {nameof(TraefikRouteOptions)}.{nameof(TraefikRouteOptions.RouterName)} to make it unique.");
                }

                routerNames.Add(routerName, resource);

                var endpoint = ResolveEndpoint(resourceWithEndpoints, annotation.Options.EndpointName);
                var route = new TraefikRoute(annotation.Traefik, resourceWithEndpoints, routerName, endpoint, annotation.Options);

                annotation.Traefik.Routes.Add(route);
                routes.Add(route);

                if (publishesToDockerCompose)
                {
                    if (resource is IResourceWithEnvironment resourceWithEnvironment)
                    {
                        TraefikDockerLabels.AddTargetPortProbe(resourceWithEnvironment, route);
                    }

                    var defaults = ((ITraefikRouteTarget)annotation.Traefik).GetRouteDefaults(isPublishMode: true);
                    resource.Annotations.Add(new DockerComposeServiceCustomizationAnnotation(
                        (composeService, service) => TraefikDockerLabels.Apply(composeService, service, route, defaults)));
                }
            }

            if (!isPublishMode)
            {
                AddPublicUrls(resource, routes);

                // Show the routes as relationships in the dashboard, from the proxy to its backends.
                foreach (var traefik in routes.Select(r => r.Traefik).Distinct())
                {
                    traefik.Annotations.Add(new ResourceRelationshipAnnotation(resource, "Route"));
                }
            }
        }
    }

    private static void AddPublicUrls(IResource resource, List<TraefikRoute> routes)
    {
        resource.Annotations.Add(new ResourceUrlsCallbackAnnotation(async context =>
        {
            // Show the resource as the address it answers on through Traefik. The direct localhost URLs bypass
            // the proxy (and any middleware on the route), so keep them in the details pane only.
            foreach (var url in context.Urls)
            {
                url.DisplayLocation = UrlDisplayLocation.DetailsOnly;
            }

            foreach (var route in routes)
            {
                if (route.Options.Host is null)
                {
                    // A path-only route matches any host, so there is no single public URL to show.
                    continue;
                }

                var expression = TraefikUrls.Build(route.Traefik, route.Options, isPublishMode: false);
                var value = await expression.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                context.Urls.Add(new ResourceUrlAnnotation
                {
                    Url = value,
                    DisplayText = $"{value} (Traefik)",
                });
            }
        }));
    }

    private static EndpointReference ResolveEndpoint(IResourceWithEndpoints resource, string? endpointName)
    {
        if (endpointName is not null)
        {
            var endpoint = resource.GetEndpoint(endpointName);
            if (!endpoint.Exists)
            {
                throw new InvalidOperationException($"Resource '{resource.Name}' has no endpoint named '{endpointName}' to route Traefik traffic to.");
            }

            return endpoint;
        }

        var http = resource.GetEndpoint("http");
        if (http.Exists)
        {
            return http;
        }

        var httpEndpoints = resource.Annotations.OfType<EndpointAnnotation>()
            .Where(e => e.UriScheme is "http" or "https")
            .ToList();

        return httpEndpoints.Count switch
        {
            1 => resource.GetEndpoint(httpEndpoints[0].Name),
            0 => throw new InvalidOperationException($"Resource '{resource.Name}' has no HTTP endpoint to route Traefik traffic to."),
            _ => throw new InvalidOperationException($"Resource '{resource.Name}' has several HTTP endpoints and none is named 'http'. Set {nameof(TraefikRouteOptions)}.{nameof(TraefikRouteOptions.EndpointName)} to choose one."),
        };
    }
}
