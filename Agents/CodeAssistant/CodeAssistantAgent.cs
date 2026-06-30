using AgentSharp;
using Click.Agents.Common.Tools;
using Click.Infrastructure;

namespace Click.Agents.CodeAssistant;

public class CodeAssistantAgent : AgentBase
{
    public CodeAssistantAgent(
        FileToolHandler fileTool,
        TerminalToolHandler terminal,
        WebReadToolHandler webRead,
        SearchToolHandler search,
        SubAgentToolHandler subAgent,
        SemanticSearchToolHandler semanticSearch,
        SerperOptions serperOptions)
    {
        if (!string.IsNullOrEmpty(serperOptions.ApiKey))
        {
            AddTool<SearchArgs>("search",
                "Поиск в Google для получения актуальной информации. Используй при вопросах о технологиях, библиотеках, best practices. После поиска ОБЯЗАТЕЛЬНО читай страницы через web_read.",
                search);
        }

        AddTool<WebReadArgs>("web_read",
            "Чтение веб-страниц по URL. Используй для: изучения документации API, чтения руководств, анализа примеров кода, получения технических деталей.",
            webRead);

        AddTool<TerminalArgs>("terminal",
            "Выполнение команд в терминале проекта (dotnet, npm, git, build, test и т.д.). Укажи timeout_seconds для долгих операций.",
            terminal);

        AddTool<FileArgs>("file",
            "Работа с файлами проекта (read/write/append/edit/list/create_dir/move/copy/delete). " +
            "Сначала read, потом edit через SEARCH/REPLACE или write для больших изменений.",
            fileTool);

        AddTool<AskAgentArgs>("ask_agent",
            "Задать вопрос агенту-консультанту (QuestionAgent) для помощи в понимании кода. " +
            "Используй, когда нужно разобраться в устройстве проекта, найти где что лежит, понять архитектуру или зависимости. " +
            "Укажи agent_id='question' и сформулируй точный вопрос.",
            subAgent);

        AddTool<SemanticSearchArgs>("semantic_search",
            "Семантический поиск по коду проекта. Используй, когда пользователь спрашивает 'где реализовано X', " +
            "'найди код для Y', 'покажи функцию которая делает Z', 'как работает ...'. " +
            "Работает по смыслу для любого языка. Возвращает фрагменты кода с путями и строками.",
            semanticSearch);
    }

    public override string Id => "code";
    public override string Name => "Click";

    public override string GetSystemPrompt(AgentContext context) =>
        PromptLoader.Load("Agents/CodeAssistant/Prompts/System.md", context);
}
