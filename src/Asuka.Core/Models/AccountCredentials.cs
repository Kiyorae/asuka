using System.Collections.Frozen;
using System.Globalization;

namespace Asuka.Core;

/// <summary>Explicit simulator input. This snapshot is never persisted by the platform store.</summary>
public sealed class AccountCredentials
{
    public const int MaximumCookieDomains = 64;
    public const int MaximumCookieLength = 16_384;
    public const int MaximumCsrfTokenLength = 512;

    public static AccountCredentials Empty { get; } = new(new Dictionary<string, string>());

    public AccountCredentials(IReadOnlyDictionary<string, string> cookies, string? csrfToken = null)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        if (cookies.Count > MaximumCookieDomains)
            throw new ArgumentException("Too many configured cookie domains.", nameof(cookies));

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in cookies)
        {
            var domain = NormalizeDomain(entry.Key);
            if (string.IsNullOrWhiteSpace(entry.Value) || entry.Value.Length > MaximumCookieLength
                || entry.Value.Any(char.IsControl))
                throw new ArgumentException("Cookies must be nonempty bounded text without control characters.", nameof(cookies));
            if (!copy.TryAdd(domain, entry.Value))
                throw new ArgumentException("Cookie domains must be unique after normalization.", nameof(cookies));
        }

        if (csrfToken is not null && (string.IsNullOrWhiteSpace(csrfToken)
            || csrfToken.Length > MaximumCsrfTokenLength || csrfToken.Any(char.IsControl)))
            throw new ArgumentException("The CSRF token must be nonempty bounded text without control characters.", nameof(csrfToken));

        Cookies = copy.ToFrozenDictionary(StringComparer.Ordinal);
        CsrfToken = csrfToken;
    }

    public IReadOnlyDictionary<string, string> Cookies { get; }
    public string? CsrfToken { get; }

    /// <summary>Normalizes an exact DNS host. Empty denotes only OneBot V11's explicit default scope.</summary>
    public static string NormalizeDomain(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        if (domain.Length == 0) return string.Empty;
        if (domain.Length > 254 || domain.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw InvalidDomain();

        // Never parse a URL or strip a leading dot: neither is an exact domain scope.
        var normalized = domain.EndsWith('.') ? domain[..^1] : domain;
        try { normalized = new IdnMapping().GetAscii(normalized).ToLowerInvariant(); }
        catch (ArgumentException) { throw InvalidDomain(); }
        if (normalized.Length is 0 or > 253) throw InvalidDomain();
        foreach (var label in normalized.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'
                || label.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
                throw InvalidDomain();
        }
        return normalized;
    }

    public override string ToString() => "AccountCredentials { credentials redacted }";

    private static ArgumentException InvalidDomain() => new("The cookie domain must be an exact DNS host or the empty default scope.");
}
