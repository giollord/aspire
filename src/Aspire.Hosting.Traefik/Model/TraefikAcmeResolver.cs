// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// An ACME certificate resolver added with <c>WithLetsEncrypt</c>.
/// </summary>
/// <remarks>
/// Mutable because <c>WithLetsEncrypt</c> and <c>WithDnsChallenge</c> configure the same resolver, in either order of
/// later calls.
/// </remarks>
internal sealed class TraefikAcmeResolver(string name, ReferenceExpression email)
{
    public string Name { get; } = name;

    public ReferenceExpression Email { get; set; } = email;

    public TraefikAcmeChallenge Challenge { get; set; }

    public bool Staging { get; set; }

    /// <summary>
    /// Gets the DNS-01 challenge, which replaces <see cref="Challenge"/> when set.
    /// </summary>
    public TraefikDnsChallenge? DnsChallenge { get; set; }
}

internal sealed record TraefikDnsChallenge(string Provider, IReadOnlyDictionary<string, ParameterResource> Credentials, IReadOnlyList<string> Resolvers, string? DelayBeforeChecks);

internal sealed record TraefikTlsDomain(ReferenceExpression Main, IReadOnlyList<ReferenceExpression> Sans)
{
    /// <summary>
    /// Gets a value indicating whether a name is a wildcard, which Let's Encrypt only issues with the DNS-01 challenge.
    /// </summary>
    public bool HasWildcard => Sans.Prepend(Main).Any(name => name.Format.Contains('*'));
}
