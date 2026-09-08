using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

public sealed partial class MilkyProtocol
{
    // https://milky.ntqqrev.org/api/system#get_cookies
    private async Task<ProtocolReply> CredentialApiAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        string? domain = null;
        if (request.Name == "get_cookies")
        {
            if (request.Parameters["domain"] is not System.Text.Json.Nodes.JsonValue scalar
                || !scalar.TryGetValue<string>(out domain) || string.IsNullOrEmpty(domain))
                return Invalid("A nonempty cookie domain string is required.");
            domain = AccountCredentials.NormalizeDomain(domain);
        }

        var credentials = await _platform.GetAccountCredentialsAsync(SelfId, cancellationToken).ConfigureAwait(false);
        if (domain is not null)
        {
            return credentials.Cookies.TryGetValue(domain, out var cookies)
                ? ProtocolReply.Success(new JsonObject { ["cookies"] = cookies }) : CredentialsUnavailable();
        }

        return credentials.CsrfToken is { } csrfToken
            ? ProtocolReply.Success(new JsonObject { ["csrf_token"] = csrfToken }) : CredentialsUnavailable();
    }

    private static ProtocolReply CredentialsUnavailable() =>
        new(-500, Message: "QQ credentials have not been configured for this account and scope.");
}
