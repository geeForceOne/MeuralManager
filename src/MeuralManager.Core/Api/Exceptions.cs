namespace MeuralManager.Core.Api;

public class MeuralAuthException(string message, Exception? inner = null) : Exception(message, inner);

// Invalid email/password, or a session/refresh token that's no longer valid.
public sealed class MeuralInvalidAuthException(string message, Exception? inner = null) : MeuralAuthException(message, inner);

// Network failure, timeout, or a 429/5xx from Netgear's auth services - transient, worth retrying.
public sealed class MeuralCannotConnectException(string message, Exception? inner = null) : MeuralAuthException(message, inner);

// Netgear's CloudFront/WAF rejected the request outright, distinct from bad credentials.
public sealed class MeuralAuthBlockedException(string message, Exception? inner = null) : MeuralAuthException(message, inner);

// The verification code/answer submitted for a pending challenge was rejected or expired.
public sealed class MeuralInvalidChallengeException(string message, Exception? inner = null) : MeuralAuthException(message, inner);

// Cognito needs an interactive answer (email/SMS/authenticator code) beyond the password.
public sealed class MeuralChallengeRequiredException(PendingChallenge challenge)
    : MeuralAuthException($"Cognito challenge {challenge.Name} requires a response")
{
    public PendingChallenge Challenge { get; } = challenge;
}

public sealed class MeuralApiException(string message) : Exception(message);
