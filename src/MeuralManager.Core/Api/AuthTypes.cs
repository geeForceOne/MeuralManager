namespace MeuralManager.Core.Api;

// A Cognito challenge that needs an interactive response (verification code, etc).
public sealed record PendingChallenge(
    string Username,
    string Session,
    string Name,
    IReadOnlyDictionary<string, string> Parameters,
    int Attempt,
    string TrustId)
{
    // Best-effort redacted destination Cognito reports the code was sent to, for display in the UI.
    public string Destination =>
        Parameters.TryGetValue("CODE_DELIVERY_DESTINATION", out var dest) ||
        Parameters.TryGetValue("deliveryDestination", out dest) ||
        Parameters.TryGetValue("email", out dest) ||
        Parameters.TryGetValue("phone_number", out dest)
            ? dest
            : "your Netgear account";
}
// Meural OAuth tokens obtained by exchanging a Cognito session for Netgear Accounts access.
public sealed record AuthResult(
    string AccessToken,
    string RefreshToken,
    double ExpiresAt,
    string TrustId,
    string? IdToken = null);
