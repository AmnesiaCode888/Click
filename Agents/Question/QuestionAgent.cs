using AgentSharp;
using Click.Agents.Common.Tools;
using Click.Infrastructure;

namespace Click.Agents.Question;

/// <summary>
/// Read-only agent for answering code-related questions.
/// Cannot write, edit, delete files or run terminal commands.
/// </summary>
public class QuestionAgent : AgentBase
{
    public QuestionAgent(
        ReadOnlyFileToolHandler file,
        WebReadToolHandler webRead,
        SearchToolHandler search,
        SerperOptions serperOptions)
    {
        if (!string.IsNullOrEmpty(serperOptions.ApiKey))
        {
            AddTool<SearchArgs>("search",
                "Поиск в Google для получения информации о библиотеках, фреймворках, технологиях.",
                search);
        }

        AddTool<WebReadArgs>("web_read",
            "Чтение веб-страниц по URL. Используй для изучения документации.",
            webRead);

        AddTool<FileReadArgs>("file",
            "Только чтение файлов проекта (read/list/glob/read_tree). Никакой записи или изменений.",
            file);
    }

    public override string Id => "question";
    public override string Name => "QuestionAgent";

    public override string GetSystemPrompt(AgentContext context) =>
        PromptLoader.Load("Agents/Question/Prompts/System.md", context);
}
