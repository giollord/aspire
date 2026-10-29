// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Records a middleware applied to every route of a resource with <c>WithTraefikMiddleware</c>.
/// </summary>
internal sealed record TraefikMiddlewareAnnotation(TraefikMiddlewareResource Middleware) : IResourceAnnotation;
