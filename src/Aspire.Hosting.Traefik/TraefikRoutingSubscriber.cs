// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
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

        AddSignInRoutes(model, isPublishMode);

        // Traefik matches router names case-sensitively in the file provider, but Docker label keys are compared
        // case-insensitively by some tooling, so treat names that differ only by case as duplicates.
        var routerNames = new Dictionary<string, IResource>(StringComparer.OrdinalIgnoreCase);
        var publishesToDockerCompose = isPublishMode && model.Resources.OfType<DockerComposeEnvironmentResource>().Any();

        foreach (var traefik in model.Resources.OfType<TraefikResource>())
        {
            if (traefik.DashboardHost is not null)
            {
                routerNames[traefik.DashboardRouterName] = traefik;
            }

            if (publishesToDockerCompose)
            {
                ApplyPublishFileConfiguration(traefik);
            }
        }

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
            var resourceMiddlewares = resource.Annotations.OfType<TraefikMiddlewareAnnotation>().Select(a => a.Middleware).ToList();

            foreach (var middleware in resourceMiddlewares)
            {
                if (!annotations.Any(a => a.Traefik == middleware.Parent))
                {
                    throw new InvalidOperationException($"Resource '{resource.Name}' uses the Traefik middleware '{middleware.Name}' of '{middleware.Parent.Name}', but has no route through '{middleware.Parent.Name}'.");
                }
            }

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

                // An external Traefik's resolvers aren't known, but the managed one's are, in run mode too, so a typo
                // shows up before the first deployment.
                if (annotation.Options.CertResolver is { } certResolver &&
                    annotation.Traefik.PublishExternal is null &&
                    !annotation.Traefik.AcmeResolvers.Any(r => string.Equals(r.Name, certResolver, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"The Traefik route '{routerName}' of resource '{resource.Name}' uses the certificate resolver '{certResolver}', which '{annotation.Traefik.Name}' doesn't have. Add it with WithLetsEncrypt.");
                }

                var endpoint = ResolveEndpoint(resourceWithEndpoints, annotation.Options.EndpointName);
                var middlewares = GetMiddlewares(resource, routerName, annotation, resourceMiddlewares);
                var route = new TraefikRoute(annotation.Traefik, resourceWithEndpoints, routerName, endpoint, annotation.Options, middlewares);

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

            if (publishesToDockerCompose)
            {
                var middlewares = routes.SelectMany(r => r.Middlewares).SelectMany(m => m.WithReferences()).Distinct().ToList();

                if (resource is IResourceWithEnvironment resourceWithEnvironment)
                {
                    TraefikDockerLabels.AddMiddlewareValueProbes(resourceWithEnvironment, middlewares);
                }

                // After the route customizations above, which read the probes.
                resource.Annotations.Add(new DockerComposeServiceCustomizationAnnotation((_, service) => TraefikDockerLabels.RemoveProbes(service)));

                JoinExternalNetwork(model, middlewares);
            }

            if (!isPublishMode)
            {
                AddPublicUrls(resource, routes);

                // Show the routes as relationships in the dashboard, from the proxy to its backends.
                foreach (var traefik in routes.Select(r => r.Traefik).Distinct())
                {
                    traefik.Annotations.Add(new ResourceRelationshipAnnotation(resource, "Route"));
                }

                // Only middleware resources: the StripPathPrefix middleware isn't in the model.
                foreach (var middleware in routes.SelectMany(r => r.Middlewares).Distinct().Where(m => m.Parent.Middlewares.Contains(m)))
                {
                    resource.Annotations.Add(new ResourceRelationshipAnnotation(middleware, "Middleware"));
                }
            }
        }

        SetErrorPageServices(model);
    }

    private static void ApplyPublishFileConfiguration(TraefikResource traefik)
    {
        if (traefik.PublishExternal is not null && traefik.EntryPointMiddlewares.Count > 0)
        {
            throw new InvalidOperationException($"Traefik resource '{traefik.Name}' is published as external, so its entry-point middlewares can't be deployed. Configure them on the external Traefik instead.");
        }

        if (!traefik.HasPublishFileConfiguration)
        {
            return;
        }

        // Endpoint values in the middlewares, such as a forward authentication address, are resolved through
        // environment variables of Traefik's own service, as they are for routed services (see TraefikDockerLabels).
        TraefikDockerLabels.AddMiddlewareValueProbes(traefik, TraefikDynamicConfiguration.GetPublishMiddlewares(traefik));

        traefik.Annotations.Add(new DockerComposeServiceCustomizationAnnotation((composeService, service) =>
        {
            var content = TraefikDynamicConfiguration.GeneratePublish(traefik, composeService, service);
            TraefikDockerLabels.RemoveProbes(service);

            // The config itself is added to the compose file by AddTraefik, which can reach the environment's
            // ConfigureComposeFile; that runs after every service is generated.
            var name = $"{service.Name}-dynamic";
            traefik.PublishFileConfiguration = new(composeService.Parent, name, content);
            service.AddConfig(new ConfigReference
            {
                Source = name,
                Target = $"{TraefikResource.DynamicConfigurationDirectory}/{TraefikDynamicConfiguration.FileName}",
            });
        }));
    }

    private static void SetErrorPageServices(DistributedApplicationModel model)
    {
        foreach (var middleware in model.Resources.OfType<TraefikMiddlewareResource>())
        {
            if (middleware.ErrorPageResource is not { } errorPages)
            {
                continue;
            }

            // The service of a route has the router's name. The first route of the resource is used.
            var route = middleware.Parent.Routes.FirstOrDefault(r => r.Resource == errorPages)
                ?? throw new InvalidOperationException($"The Traefik middleware '{middleware.Name}' serves error pages from '{errorPages.Name}', which has no route through '{middleware.Parent.Name}'. Call WithTraefikRoute on it.");

            middleware.Options.Set("service", route.RouterName);
        }
    }

    private static List<TraefikMiddlewareResource> GetMiddlewares(IResource resource, string routerName, TraefikRouteAnnotation annotation, List<TraefikMiddlewareResource> resourceMiddlewares)
    {
        foreach (var middleware in annotation.Options.Middlewares)
        {
            if (middleware.Parent != annotation.Traefik)
            {
                throw new InvalidOperationException($"The Traefik route '{routerName}' of resource '{resource.Name}' goes through '{annotation.Traefik.Name}', but its middleware '{middleware.Name}' belongs to '{middleware.Parent.Name}'.");
            }
        }

        // Resource-wide middlewares (usually authentication or security headers) run before the route's own ones
        // (usually path rewrites), so they see the request as the client sent it. A middleware listed in both
        // places runs once, at its first position.
        var middlewares = resourceMiddlewares
            .Where(m => m.Parent == annotation.Traefik)
            .Concat(annotation.Options.Middlewares)
            .Distinct()
            .ToList();

        if (annotation.Options.StripPathPrefix)
        {
            // Last, so the route's own middlewares still see the path the route matched on. The middleware only
            // exists in Traefik's configuration, not as a resource.
            var name = $"{routerName}-strip";
            if (annotation.Traefik.Middlewares.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"The Traefik route '{routerName}' of resource '{resource.Name}' strips its path prefix with a middleware named '{name}', but '{annotation.Traefik.Name}' already has a middleware with that name.");
            }

            var strip = new TraefikMiddlewareResource(name, "stripPrefix", annotation.Traefik);
            strip.Options.Set("prefixes", new object[] { annotation.Options.PathPrefix! });
            middlewares.Add(strip);
        }

        return middlewares;
    }

    /// <summary>
    /// Routes <c>/oauth2/*</c> on every host protected by an oauth2-proxy middleware to the oauth2-proxy container.
    /// </summary>
    /// <remarks>
    /// The provider redirects the browser back to <c>/oauth2/callback</c> on the host the user first requested, so that
    /// path must reach oauth2-proxy rather than the protected backend, and without passing through the middleware.
    /// </remarks>
    private static void AddSignInRoutes(DistributedApplicationModel model, bool isPublishMode)
    {
        foreach (var resource in model.Resources)
        {
            foreach (var generated in resource.Annotations.OfType<TraefikRouteAnnotation>().Where(a => a.IsSignInRoute).ToList())
            {
                resource.Annotations.Remove(generated);
            }

            if (resource is TraefikMiddlewareResource middleware)
            {
                middleware.ProtectedHosts.Clear();
            }
        }

        foreach (var resource in model.Resources.ToList())
        {
            var resourceMiddlewares = resource.Annotations.OfType<TraefikMiddlewareAnnotation>().Select(a => a.Middleware).ToList();

            foreach (var annotation in resource.Annotations.OfType<TraefikRouteAnnotation>().ToList())
            {
                var middlewares = resourceMiddlewares
                    .Where(m => m.Parent == annotation.Traefik)
                    .Concat(annotation.Options.Middlewares)
                    .SelectMany(m => m.WithReferences())
                    .Where(m => m.OAuth2Proxy is not null)
                    .Distinct();

                foreach (var middleware in middlewares)
                {
                    if (annotation.Options.Host is not { } host)
                    {
                        throw new InvalidOperationException($"A Traefik route of resource '{resource.Name}' uses the oauth2-proxy middleware '{middleware.Name}', but has no host. The sign-in callback needs one.");
                    }

                    AddSignInRoute(middleware, annotation.Traefik, host, annotation.Options.EntryPoint);
                }
            }
        }

        foreach (var traefik in model.Resources.OfType<TraefikResource>())
        {
            // An external Traefik's dashboard isn't published (see WithDashboard), so neither is its sign-in route.
            if (traefik.DashboardHost is not { } host || (isPublishMode && traefik.PublishExternal is not null))
            {
                continue;
            }

            foreach (var middleware in traefik.DashboardMiddlewares.SelectMany(m => m.WithReferences()).Where(m => m.OAuth2Proxy is not null).Distinct())
            {
                AddSignInRoute(middleware, traefik, host, entryPoint: null);
            }
        }

        static void AddSignInRoute(TraefikMiddlewareResource middleware, TraefikResource traefik, ReferenceExpression host, string? entryPoint)
        {
            // Hosts are compared by their manifest expression, so `api.{domain}` declared twice counts once.
            if (middleware.ProtectedHosts.Any(h => h.ValueExpression == host.ValueExpression))
            {
                return;
            }

            middleware.ProtectedHosts.Add(host);
            middleware.OAuth2Proxy!.Annotations.Add(new TraefikRouteAnnotation(traefik, new TraefikRouteOptions
            {
                Host = host,
                PathPrefix = "/oauth2",
                Priority = TraefikForwardAuthExtensions.SignInRoutePriority,
                EntryPoint = entryPoint,
                RouterName = $"{middleware.OAuth2Proxy.Name}-{middleware.ProtectedHosts.Count}",
            }, IsSignInRoute: true));
        }
    }

    private static void JoinExternalNetwork(DistributedApplicationModel model, List<TraefikMiddlewareResource> middlewares)
    {
        // An external Traefik reaches the services a middleware calls, such as a forward authentication server, over
        // its shared network, so those services join it too. Routed services join it in TraefikDockerLabels.
        foreach (var middleware in middlewares)
        {
            if (((ITraefikRouteTarget)middleware.Parent).GetRouteDefaults(isPublishMode: true).Network is not { } network)
            {
                continue;
            }

            foreach (var referenced in middleware.Options.GetReferencedResources())
            {
                if (!model.Resources.Contains(referenced) || referenced.Annotations.OfType<TraefikNetworkAnnotation>().Any(a => a.Network == network))
                {
                    continue;
                }

                referenced.Annotations.Add(new TraefikNetworkAnnotation(network));
                referenced.Annotations.Add(new DockerComposeServiceCustomizationAnnotation((_, service) =>
                {
                    if (!service.Networks.Contains(network))
                    {
                        service.Networks.Add(network);
                    }
                }));
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
