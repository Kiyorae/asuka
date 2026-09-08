using System.Globalization;
using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

public sealed partial class OneBotProtocol
{
    // https://github.com/botuniverse/onebot-11/blob/master/api/public.md#get_credentials-获取-qq-相关接口凭证
    private async Task<ProtocolReply> CredentialApiAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        string? domain = null;
        if (request.Name is "get_cookies" or "get_credentials")
        {
            domain = string.Empty;
            if (request.Parameters.TryGetPropertyValue("domain", out var node))
            {
                if (node is not System.Text.Json.Nodes.JsonValue scalar
                    || !scalar.TryGetValue<string>(out var requestedDomain) || requestedDomain is null)
                    return Invalid("The cookie domain must be a string.");
                domain = requestedDomain;
            }
            domain = AccountCredentials.NormalizeDomain(domain);
        }

        var credentials = await _platform.GetAccountCredentialsAsync(SelfId, cancellationToken).ConfigureAwait(false);
        string? cookies = null;
        if (domain is not null && !credentials.Cookies.TryGetValue(domain, out cookies))
            return CredentialsUnavailable();

        var csrfToken = 0;
        if (request.Name != "get_cookies"
            && !int.TryParse(credentials.CsrfToken, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out csrfToken))
            return CredentialsUnavailable();

        var payload = new JsonObject();
        if (domain is not null) payload["cookies"] = cookies;
        if (request.Name != "get_cookies") payload[request.Name == "get_csrf_token" ? "token" : "csrf_token"] = csrfToken;
        return ProtocolReply.Success(payload);
    }

    private static ProtocolReply CredentialsUnavailable() =>
        new(1000, Message: "Compatible QQ credentials have not been configured for this account and scope.");
}
