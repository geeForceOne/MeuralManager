using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using MeuralManager.Core.Models;

namespace MeuralManager.Core.Services;

public sealed class AiNamingException(string message) : Exception(message);

// Suggests a new name for an image by sending it to a vision-capable model (Claude or ChatGPT,
// per the caller's AiSettings) and asking for a short title. Never renames anything itself - the
// caller always shows the suggestion to the user for review before committing it via
// MeuralApiClient.RenameItemAsync, the same way a manual rename works.
public static class ImageNamingService
{
    public static async Task<string> SuggestNameAsync(
        AiSettings settings, string imageUrl, string? playlistName, IProgress<string>? progress, CancellationToken ct)
    {
        if (!settings.HasKeyFor(settings.Provider))
            throw new AiNamingException($"No API key configured for {settings.Provider} - add one in Settings.");

        progress?.Report("Downloading image...");
        var (bytes, mediaType) = await DownloadImageAsync(imageUrl, ct);
        var base64 = Convert.ToBase64String(bytes);
        var prompt = BuildPrompt(settings.RenameStyle, playlistName);

        progress?.Report($"Asking {settings.Provider} for a name suggestion...");
        var suggestion = settings.Provider switch
        {
            AiProvider.Claude => await SuggestViaClaudeAsync(
                settings.ClaudeApiKey!, settings.ClaudeWorkspaceId, settings.EffectiveClaudeModel, base64, mediaType, prompt, ct),
            AiProvider.ChatGpt => await SuggestViaChatGptAsync(
                settings.OpenAiApiKey!, settings.EffectiveOpenAiModel, base64, mediaType, prompt, ct),
            _ => throw new AiNamingException($"Unknown AI provider: {settings.Provider}"),
        };

        var cleaned = CleanSuggestion(suggestion);
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new AiNamingException($"{settings.Provider} didn't return a usable name.");

        return cleaned;
    }

    private static string BuildPrompt(RenameStyle style, string? playlistName)
    {
        var context = string.IsNullOrWhiteSpace(playlistName)
            ? ""
            : $" It's currently in a playlist called \"{playlistName}\" - let that inform the tone or theme of your suggestion where it fits, but the image itself comes first.";

        return style switch
        {
            RenameStyle.Creative =>
                "Look at this image, which is part of someone's personal digital art/photo collection." + context +
                " Suggest a short, playful, a little funny or creative title for it - the kind of title that " +
                "would make someone smile when they see it in a file list. Reply with ONLY the title itself: " +
                "1-4 words, no quotation marks, no trailing punctuation, no explanation.",
            _ =>
                "Look at this image, which is part of someone's personal digital art/photo collection." + context +
                " Suggest a short, clear, descriptive title for it, the way a museum or stock photo library " +
                "would caption it. Reply with ONLY the title itself: 1-4 words, no quotation marks, no " +
                "trailing punctuation, no explanation.",
        };
    }

    private static string CleanSuggestion(string raw)
    {
        var cleaned = raw.Trim().Trim('"', '\'', '.', ' ');
        // A model occasionally answers in a full sentence despite the prompt - take just the
        // first line so a stray explanation doesn't end up as the suggested filename.
        var firstLine = cleaned.Split('\n')[0].Trim();
        return firstLine.Length > 80 ? firstLine[..80].Trim() : firstLine;
    }

    private static async Task<(byte[] Bytes, string MediaType)> DownloadImageAsync(string imageUrl, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var resp = await http.GetAsync(imageUrl, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            mediaType = "image/jpeg";
        return (bytes, mediaType);
    }

    private static async Task<string> SuggestViaClaudeAsync(
        string apiKey, string? workspaceId, string model, string base64Image, string mediaType, string prompt, CancellationToken ct)
    {
        // ExtraHeaders is how the SDK surfaces "anthropic-workspace-id" - needed only for an
        // identity-linked API key (one that can act across multiple workspaces in an org); a
        // plain workspace-scoped key never needs it and workspaceId stays null for those.
        var clientOptions = new ClientOptions { ApiKey = apiKey };
        if (!string.IsNullOrWhiteSpace(workspaceId))
            clientOptions.ExtraHeaders = new Dictionary<string, string> { ["anthropic-workspace-id"] = workspaceId };

        var client = new AnthropicClient(clientOptions);
        Message response;
        try
        {
            response = await client.Messages.Create(new MessageCreateParams
            {
                Model = model,
                MaxTokens = 300,
                // No OutputConfig.Effort here - which models even accept it varies (Claude
                // Haiku 4.5, the default for this feature, rejects it outright with a 400), and
                // the model picker lets the user configure any Claude model string, so there's
                // no single value that's safe to send unconditionally.
                Messages =
                [
                    new()
                    {
                        Role = Role.User,
                        Content = new List<ContentBlockParam>
                        {
                            new ImageBlockParam { Source = new Base64ImageSource { Data = base64Image, MediaType = mediaType } },
                            new TextBlockParam { Text = prompt },
                        },
                    },
                ],
            }, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AiNamingException($"Claude request failed: {ex.Message}");
        }

        var text = response.Content.Select(b => b.Value).OfType<TextBlock>().FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new AiNamingException($"Claude didn't return a name (stop reason: {response.StopReason}).");

        return text;
    }

    private sealed record OpenAiChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("messages")] List<OpenAiChatMessage> Messages);

    private sealed record OpenAiChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] List<OpenAiContentPart> Content);

    private sealed record OpenAiContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text = null,
        [property: JsonPropertyName("image_url")] OpenAiImageUrl? ImageUrl = null);

    private sealed record OpenAiImageUrl([property: JsonPropertyName("url")] string Url);

    private sealed record OpenAiChatResponse([property: JsonPropertyName("choices")] List<OpenAiChoice>? Choices);
    private sealed record OpenAiChoice([property: JsonPropertyName("message")] OpenAiMessage? Message);
    private sealed record OpenAiMessage([property: JsonPropertyName("content")] string? Content);

    private static async Task<string> SuggestViaChatGptAsync(
        string apiKey, string model, string base64Image, string mediaType, string prompt, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var body = new OpenAiChatRequest(
            Model: model,
            MaxTokens: 300,
            Messages:
            [
                new OpenAiChatMessage("user",
                [
                    new OpenAiContentPart("text", Text: prompt),
                    new OpenAiContentPart("image_url", ImageUrl: new OpenAiImageUrl($"data:{mediaType};base64,{base64Image}")),
                ]),
            ]);

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync("https://api.openai.com/v1/chat/completions", content, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AiNamingException($"ChatGPT request failed: {ex.Message}");
        }

        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new AiNamingException($"ChatGPT request failed ({(int)resp.StatusCode}): {responseBody}");

        var parsed = JsonSerializer.Deserialize<OpenAiChatResponse>(responseBody);
        var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
            throw new AiNamingException("ChatGPT didn't return a name.");

        return text;
    }
}
