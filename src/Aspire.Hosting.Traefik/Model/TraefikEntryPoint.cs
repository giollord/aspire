// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Traefik;

/// <summary>
/// A custom entry point added with <c>WithEntryPoint</c>.
/// </summary>
/// <param name="Name">The name of the entry point and of its endpoint.</param>
/// <param name="TargetPort">The port Traefik listens on inside the container.</param>
/// <param name="IsUdp">Whether the entry point listens for UDP datagrams instead of TCP connections.</param>
internal sealed record TraefikEntryPoint(string Name, int TargetPort, bool IsUdp);
