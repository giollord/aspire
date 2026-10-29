// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Traefik;

internal static class TraefikContainerImageTags
{
    /// <remarks>docker.io</remarks>
    public const string Registry = "docker.io";

    /// <remarks>library/traefik</remarks>
    public const string Image = "library/traefik";

    /// <remarks>v3.7</remarks>
    public const string Tag = "v3.7";

    /// <remarks>quay.io</remarks>
    public const string OAuth2ProxyRegistry = "quay.io";

    /// <remarks>oauth2-proxy/oauth2-proxy</remarks>
    public const string OAuth2ProxyImage = "oauth2-proxy/oauth2-proxy";

    /// <remarks>v7.15.5</remarks>
    public const string OAuth2ProxyTag = "v7.15.5";
}
