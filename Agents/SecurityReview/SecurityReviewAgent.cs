using AgentSharp;
using Click.Agents.Common.Tools;
using Click.Infrastructure;

namespace Click.Agents.SecurityReview;

public class SecurityReviewAgent : AgentBase
{
    public SecurityReviewAgent(
        ReadOnlyFileToolHandler file,
        WebReadToolHandler webRead,
        SearchToolHandler search,
        SerperOptions serperOptions)
    {
        if (!string.IsNullOrEmpty(serperOptions.ApiKey))
        {
            AddTool<SearchArgs>("search",
                "Поиск в Google для получения актуальной информации об уязвимостях, CVE, best practices. Используй при необходимости.",
                search);
        }

        AddTool<WebReadArgs>("web_read",
            "Чтение веб-страниц по URL. Используй для изучения документации по безопасности, CVE, рекомендаций.",
            webRead);

        AddTool<FileReadArgs>("file",
            "Только чтение файлов проекта (read/list/glob/read_tree). Никакой записи, удаления или изменения файлов.",
            file);
    }

    public override string Id => "security";
    public override string Name => "SecurityReviewer";

    public override string GetSystemPrompt(AgentContext context) =>
        PromptLoader.Load("Agents/SecurityReview/Prompts/System.md", context);
}
