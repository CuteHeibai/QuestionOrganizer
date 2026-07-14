namespace EaxmBuilder.Core;

public enum AiProviderKind
{
    OpenAi = 0,
    OpenAiCompatible = 1,
    Doubao = 3
}

public sealed class AppSettings
{
    public bool OnboardingCompleted { get; set; }
    public AiProviderKind Provider { get; set; } = AiProviderKind.OpenAi;
    public string ProtectedApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-5.5";
    public string OutputDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "题目整理");
    public string Theme { get; set; } = "浅色";
    public string WordTemplatePath { get; set; } = string.Empty;
}
