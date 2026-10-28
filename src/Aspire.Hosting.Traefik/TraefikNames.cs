// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Validation and conversion of the names used in Traefik configuration.
/// </summary>
internal static partial class TraefikNames
{
    /// <summary>
    /// Checks a router, entry point, or resolver name. Names become segments of label keys such as
    /// <c>traefik.http.routers.&lt;name&gt;.rule</c>, so dots would split the key, and <c>@</c> is reserved for
    /// provider namespaces (<c>api@internal</c>).
    /// </summary>
    public static bool IsValidName(string name) => ValidNameRegex().IsMatch(name);

    /// <summary>
    /// Converts a name to an environment variable segment, for example <c>app-api</c> to <c>APP_API</c>.
    /// </summary>
    public static string ToEnvironmentName(string name) => NonAlphanumericRegex().Replace(name, "_").ToUpperInvariant();

    /// <summary>
    /// Returns the text of an expression without value providers, such as <c>ReferenceExpression.Create($"web")</c>,
    /// or <see langword="null"/> when it references parameters or other values.
    /// </summary>
    public static string? GetLiteral(ReferenceExpression expression) =>
        // Names can't contain braces, so the composite format string of a literal expression is the text itself.
        expression.ValueProviders.Count == 0 && !expression.IsConditional ? expression.Format : null;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex ValidNameRegex();

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumericRegex();
}
