using EaxmBuilder.Core;
using EaxmBuilder.Infrastructure;

namespace EaxmBuilder.AI;

public sealed class AiProviderFactory(SettingsStore settingsStore)
{
    public IAiProvider Create(AppSettings settings, string? apiKey = null)
    {
        var resolvedKey = apiKey ?? settingsStore.ReadApiKey(settings);
        if (string.IsNullOrWhiteSpace(resolvedKey))
            throw new InvalidOperationException("请先配置 API Key。");
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("请填写模型名称。");
        if (settings.Provider is not (AiProviderKind.OpenAi or AiProviderKind.OpenAiCompatible or AiProviderKind.Doubao))
            throw new InvalidOperationException("当前 AI 提供商已不再支持，请重新选择 OpenAI、OpenAI Compatible 或火山豆包。");

        return new OpenAiProvider(
            settings.Provider,
            settings.BaseUrl,
            settings.Model,
            resolvedKey);
    }
}
