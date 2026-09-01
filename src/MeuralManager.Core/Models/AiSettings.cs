namespace MeuralManager.Core.Models;

public enum AiProvider
{
    Claude,
    ChatGpt,
}

public enum RenameStyle
{
    Professional,
    Creative,
}

public sealed record AiSettings
{
    // Cheap-by-default: naming a picture is a simple classification task, not something that
    // needs a flagship model - the user picks these explicitly if they want something else.
    public const string DefaultClaudeModel = "claude-haiku-4-5";
    public const string DefaultOpenAiModel = "gpt-4o-mini";

    public AiProvider Provider { get; init; } = AiProvider.Claude;
    public RenameStyle RenameStyle { get; init; } = RenameStyle.Professional;
    public string? ClaudeApiKey { get; init; }
    // Only needed for an "identity-linked" Claude API key (one tied to a user identity that
    // spans multiple workspaces in an org) - the API rejects such a key with a 400 unless every
    // request names which workspace it acts in. A plain workspace-scoped key doesn't need this.
    public string? ClaudeWorkspaceId { get; init; }
    public string? ClaudeModel { get; init; }
    public string? OpenAiApiKey { get; init; }
    public string? OpenAiModel { get; init; }

    public string EffectiveClaudeModel => string.IsNullOrWhiteSpace(ClaudeModel) ? DefaultClaudeModel : ClaudeModel;
    public string EffectiveOpenAiModel => string.IsNullOrWhiteSpace(OpenAiModel) ? DefaultOpenAiModel : OpenAiModel;

    public bool HasKeyFor(AiProvider provider) => provider switch
    {
        AiProvider.Claude => !string.IsNullOrWhiteSpace(ClaudeApiKey),
        AiProvider.ChatGpt => !string.IsNullOrWhiteSpace(OpenAiApiKey),
        _ => false,
    };
}
