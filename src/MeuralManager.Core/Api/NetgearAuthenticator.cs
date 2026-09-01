using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MeuralManager.Core.Api;

// Authenticates against Netgear's Cognito-backed login and exchanges the result for Meural
// OAuth tokens. Ported from the official Home Assistant Meural integration
// (github.com/GuySie/ha-meural, custom_components/meural/netgear_auth.py) after Meural/Netgear
// changed their auth backend - the old direct USER_PASSWORD_AUTH-only flow this app used to use
// stopped working. Constants, endpoints, and the CUSTOM_AUTH -> USER_PASSWORD_AUTH fallback /
// challenge-answering logic are copied from that source rather than guessed.
public sealed class NetgearAuthenticator
{
    private const string CognitoRegion = "eu-west-1";
    private const string CognitoClientId = "487bd4kvb1fnop6mbgk8gu5ibf";
    private const string CognitoUrl = $"https://cognito-idp.{CognitoRegion}.amazonaws.com/";
    private const string MeuralOAuthClientId = "3ui6nklcaqoij8inrkm06gfk4s";
    private const string NetgearAccountsUrl = "https://accounts2.netgear.com";

    internal const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static readonly Dictionary<string, string> ResponseKeyMap = new()
    {
        ["EMAIL_MFA"] = "EMAIL_MFA_CODE",
        ["EMAIL_OTP"] = "EMAIL_OTP_CODE",
        ["SMS_MFA"] = "SMS_MFA_CODE",
        ["SMS_OTP"] = "SMS_OTP_CODE",
        ["SOFTWARE_TOKEN_MFA"] = "SOFTWARE_TOKEN_MFA_CODE",
        ["NEW_PASSWORD_REQUIRED"] = "NEW_PASSWORD",
    };

    private static readonly string[] PasswordChallengeExclusionKeywords =
        ["otp", "verification", "email", "phone", "code"];

    // Netgear Accounts sits behind CloudFront/WAF, which answers a blocked request with an HTML
    // 403 page instead of a Cognito-style JSON error.
    private static readonly string[] WafErrorKeywords = ["forbiddenexception", "waf"];
    private static readonly string[] WafBlockPageKeywords = ["request blocked", "cloudfront", "<html"];

    // Netgear's auth Lambda signals an unmigrated account with a user-not-found error; Cognito's
    // own serialization of that condition is UserNotFoundException, so match both.
    private static readonly string[] UserMigrationKeywords = ["user_not_found", "usernotfoundexception"];

    private readonly HttpClient _http;

    public NetgearAuthenticator(HttpClient http, string? trustId = null)
    {
        _http = http;
        TrustId = trustId ?? Guid.NewGuid().ToString();
    }

    public string TrustId { get; set; }

    public async Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        JsonElement response;
        try
        {
            response = await InitiateAuthAsync("CUSTOM_AUTH", new() { ["USERNAME"] = username }, ct);
        }
        catch (HttpJsonException err)
        {
            if (!IsUserMigrationError(err))
                throw ClassifyAuthError(err, "Netgear rejected the account credentials");

            try
            {
                response = await InitiateAuthAsync(
                    "USER_PASSWORD_AUTH",
                    new() { ["USERNAME"] = username, ["PASSWORD"] = password },
                    ct);
            }
            catch (HttpJsonException fallbackErr)
            {
                throw ClassifyAuthError(fallbackErr, "Netgear rejected the account credentials");
            }
        }

        return await FinishCognitoAuthAsync(response, username, attempt: 0, password, ct);
    }

    public async Task<AuthResult> CompleteChallengeAsync(PendingChallenge challenge, string answer, CancellationToken ct = default)
    {
        if (challenge.TrustId != TrustId)
            throw new MeuralInvalidChallengeException("Authentication session identity changed");

        JsonElement response;
        try
        {
            response = await RespondToChallengeAsync(challenge, answer, ct);
        }
        catch (HttpJsonException err)
        {
            if (IsWafError(err))
                throw new MeuralAuthBlockedException("Netgear blocked the authentication request", err);
            if (err.Status == 429 || err.Status >= 500)
                throw new MeuralCannotConnectException("Netgear authentication is temporarily unavailable", err);
            throw new MeuralInvalidChallengeException("The verification code is invalid or expired", err);
        }

        return await FinishCognitoAuthAsync(response, challenge.Username, challenge.Attempt, password: null, ct);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        JsonElement response;
        try
        {
            response = await RequestJsonAsync(
                HttpMethod.Get,
                $"{NetgearAccountsUrl}/api/getAccessToken",
                new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {refreshToken}",
                    ["appkey"] = MeuralOAuthClientId,
                    ["Accept"] = "application/json",
                },
                null,
                ct);
        }
        catch (HttpJsonException err)
        {
            if (IsWafError(err))
                throw new MeuralAuthBlockedException("Netgear blocked the token refresh", err);
            if (err.Status == 429 || err.Status >= 500)
                throw new MeuralCannotConnectException("Netgear temporarily rejected the token refresh", err);
            throw new MeuralInvalidAuthException("The Meural session has expired", err);
        }

        return ParseMeuralTokens(response, refreshToken);
    }

    private async Task<AuthResult> FinishCognitoAuthAsync(JsonElement response, string username, int attempt, string? password, CancellationToken ct)
    {
        JsonElement authResultEl;
        while (!TryGetProp(response, "AuthenticationResult", out authResultEl))
        {
            attempt++;
            var challengeName = GetString(response, "ChallengeName");
            var challengeSession = GetString(response, "Session");
            var parameters = GetStringMap(response, "ChallengeParameters");

            if (challengeName is null || challengeSession is null)
                throw new MeuralInvalidAuthException("Cognito returned neither tokens nor a supported challenge");

            var pending = new PendingChallenge(username, challengeSession, challengeName, parameters, attempt, TrustId);

            if (password is not null && PasswordAnswersChallenge(pending))
            {
                try
                {
                    response = await RespondToChallengeAsync(pending, password, ct);
                }
                catch (HttpJsonException err)
                {
                    throw ClassifyAuthError(err, "Netgear rejected the account credentials");
                }
                continue;
            }

            throw new MeuralChallengeRequiredException(pending);
        }

        var cognitoAccessToken = GetString(authResultEl, "AccessToken");
        if (cognitoAccessToken is null)
            throw new MeuralInvalidAuthException("Cognito did not return an access token");

        return await ExchangeCognitoTokenAsync(cognitoAccessToken, ct);
    }

    private async Task<AuthResult> ExchangeCognitoTokenAsync(string cognitoAccessToken, CancellationToken ct)
    {
        JsonElement tokenResponse;
        try
        {
            var authorizeResponse = await RequestJsonAsync(
                HttpMethod.Get,
                $"{NetgearAccountsUrl}/api/oauth/authorize?client_id={Uri.EscapeDataString(MeuralOAuthClientId)}",
                new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {cognitoAccessToken}",
                    ["Accept"] = "application/json",
                },
                null,
                ct);

            var authorizeBody = Unwrap(authorizeResponse);
            var code = GetStringLike(authorizeBody, "code") ?? GetStringLike(authorizeBody, "authorizationCode");
            if (code is null)
                throw new MeuralInvalidAuthException("Netgear authorization did not return an authorization code");

            tokenResponse = await RequestJsonAsync(
                HttpMethod.Get,
                $"{NetgearAccountsUrl}/api/oauth/token?code={Uri.EscapeDataString(code)}",
                new Dictionary<string, string>
                {
                    ["Accept"] = "application/json",
                },
                null,
                ct);
        }
        catch (HttpJsonException err)
        {
            if (IsWafError(err))
                throw new MeuralAuthBlockedException("Netgear blocked the Meural token exchange", err);
            if (err.Status == 429 || err.Status >= 500)
                throw new MeuralCannotConnectException("Netgear Accounts is temporarily unavailable", err);
            throw new MeuralInvalidAuthException("Netgear rejected the Meural token exchange", err);
        }

        return ParseMeuralTokens(tokenResponse, existingRefreshToken: null);
    }

    private async Task<JsonElement> InitiateAuthAsync(string authFlow, Dictionary<string, string> authParameters, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["AuthFlow"] = authFlow,
            ["ClientId"] = CognitoClientId,
            ["AuthParameters"] = authParameters,
            ["ClientMetadata"] = ClientMetadata(),
        };
        return await RequestJsonAsync(
            HttpMethod.Post,
            CognitoUrl,
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-amz-json-1.1",
                ["X-Amz-Target"] = "AWSCognitoIdentityProviderService.InitiateAuth",
            },
            payload,
            ct);
    }

    private async Task<JsonElement> RespondToChallengeAsync(PendingChallenge challenge, string answer, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["ChallengeName"] = challenge.Name,
            ["ClientId"] = CognitoClientId,
            ["Session"] = challenge.Session,
            ["ChallengeResponses"] = new Dictionary<string, string>
            {
                ["USERNAME"] = challenge.Username,
                [ResponseKey(challenge.Name)] = answer,
            },
            ["ClientMetadata"] = ClientMetadata(),
        };
        return await RequestJsonAsync(
            HttpMethod.Post,
            CognitoUrl,
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-amz-json-1.1",
                ["X-Amz-Target"] = "AWSCognitoIdentityProviderService.RespondToAuthChallenge",
            },
            payload,
            ct);
    }

    private Dictionary<string, string> ClientMetadata() => new()
    {
        ["trustID"] = TrustId,
        ["sourceEvent"] = "login",
        ["language"] = "en-US",
        ["appType"] = "meural",
    };

    private AuthResult ParseMeuralTokens(JsonElement response, string? existingRefreshToken)
    {
        var body = Unwrap(response);
        var accessToken = Pick(body, "access_token", "accessToken", "token");
        var refreshToken = Pick(body, "refresh_token", "refreshToken") ?? existingRefreshToken;

        if (accessToken is null || refreshToken is null)
            throw new MeuralInvalidAuthException("Netgear returned an incomplete Meural session");

        var expiresInRaw = Pick(body, "expires_in", "expiresIn");
        var expiresIn = double.TryParse(expiresInRaw, out var parsedExpiresIn) ? parsedExpiresIn : 3600;
        var fallbackExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn;

        var expiresAt = JwtExpiration(accessToken) ?? fallbackExpiry;
        var idToken = Pick(body, "id_token", "idToken");

        return new AuthResult(accessToken, refreshToken, expiresAt, TrustId, idToken);
    }

    // Netgear's CUSTOM_AUTH flow verifies the password via the first CUSTOM_CHALLENGE;
    // interactive challenges (OTP/MFA) only follow it. Excluding challenges whose parameters
    // mention a code/OTP/contact method avoids guessing at the undocumented ChallengeParameters:
    // if the first challenge is actually a code prompt, it's routed to the user instead of being
    // wrongly answered with the password.
    private static bool PasswordAnswersChallenge(PendingChallenge challenge)
    {
        if (challenge.Name != "CUSTOM_CHALLENGE" || challenge.Attempt != 1)
            return false;

        var description = string.Join(' ', challenge.Parameters.Select(p => $"{p.Key} {p.Value}")).ToLowerInvariant();
        return !PasswordChallengeExclusionKeywords.Any(description.Contains);
    }

    private static string ResponseKey(string challengeName) =>
        ResponseKeyMap.GetValueOrDefault(challengeName, "ANSWER");

    private static bool IsUserMigrationError(HttpJsonException err)
    {
        var lower = err.RawBody.ToLowerInvariant();
        return UserMigrationKeywords.Any(lower.Contains);
    }

    private static bool IsWafError(HttpJsonException err)
    {
        var lower = err.RawBody.ToLowerInvariant();
        if (WafErrorKeywords.Any(lower.Contains))
            return true;
        return err.Status == 403 && WafBlockPageKeywords.Any(lower.Contains);
    }

    private static MeuralAuthException ClassifyAuthError(HttpJsonException err, string genericMessage)
    {
        if (IsWafError(err))
            return new MeuralAuthBlockedException("Netgear blocked the authentication request", err);
        if (err.Status == 429 || err.Status >= 500)
            return new MeuralCannotConnectException("Netgear authentication is temporarily unavailable", err);
        return new MeuralInvalidAuthException(genericMessage, err);
    }

    private static JsonElement Unwrap(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object)
            return data;
        return response.ValueKind == JsonValueKind.Object ? response : default;
    }

    private static string? Pick(JsonElement body, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = GetStringLike(body, key);
            if (value is not null)
                return value;
        }
        return null;
    }

    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value);
    }

    private static string? GetString(JsonElement obj, string name) =>
        TryGetProp(obj, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? GetStringLike(JsonElement obj, string name)
    {
        if (!TryGetProp(obj, name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static Dictionary<string, string> GetStringMap(JsonElement obj, string name)
    {
        var result = new Dictionary<string, string>();
        if (TryGetProp(obj, name, out var mapEl) && mapEl.ValueKind == JsonValueKind.Object)
            foreach (var prop in mapEl.EnumerateObject())
                result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()! : prop.Value.GetRawText();
        return result;
    }

    private static double? JwtExpiration(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;

        try
        {
            var payloadPart = parts[1].Replace('-', '+').Replace('_', '/');
            payloadPart += (payloadPart.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            var payloadBytes = Convert.FromBase64String(payloadPart);
            using var doc = JsonDocument.Parse(payloadBytes);
            return doc.RootElement.TryGetProperty("exp", out var expEl) && expEl.ValueKind == JsonValueKind.Number
                ? expEl.GetDouble()
                : null;
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
    }

    private async Task<JsonElement> RequestJsonAsync(
        HttpMethod method,
        string url,
        IDictionary<string, string> headers,
        object? jsonBody,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        // AWS WAF's managed rule set blocks requests with no User-Agent header outright, and
        // HttpClient sends none by default - accounts2.netgear.com sits behind exactly that WAF
        // (see the "Netgear blocked the ..." exceptions above), so every request needs one.
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        string? contentType = null;
        foreach (var (key, value) in headers)
        {
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
                continue;
            }
            request.Headers.TryAddWithoutValidation(key, value);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(jsonBody), Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json");
        }

        HttpResponseMessage response;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            response = await _http.SendAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MeuralCannotConnectException("Could not connect to Netgear Accounts");
        }
        catch (HttpRequestException ex)
        {
            throw new MeuralCannotConnectException("Could not connect to Netgear Accounts", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { message = body }));
                root = doc.RootElement.Clone();
            }

            if ((int)response.StatusCode is >= 200 and < 300)
                return root;

            throw new HttpJsonException((int)response.StatusCode, body);
        }
    }

    private sealed class HttpJsonException(int status, string rawBody) : Exception($"HTTP request failed with status {status}")
    {
        public int Status { get; } = status;
        public string RawBody { get; } = rawBody;
    }
}
