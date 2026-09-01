using System.Net;

namespace MeuralManager.Core.Api;

public readonly record struct DeleteOutcome(bool Success, HttpStatusCode StatusCode);
