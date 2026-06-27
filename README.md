# Click

**Click** — консольный AI-ассистент для разработки, построенный по принципу ReAct-агента. Он получает запрос пользователя, вызывает языковую модель (LLM), анализирует ответ, исполняет доступные инструменты и возвращает итоговый результат внутри выбранной рабочей директории.

**Click** is a console AI coding assistant built as a ReAct agent. It takes a user request, calls a language model (LLM), analyzes the response, executes available tools, and returns the final result within the selected workspace.

---

## Содержание / Table of Contents

- [Возможности / Features](#возможности--features)
- [Архитектура / Architecture](#архитектура--architecture)
- [Установка и запуск / Installation & Run](#установка-и-запуск--installation--run)
- [Конфигурация / Configuration](#конфигурация--configuration)
- [Использование / Usage](#использование--usage)
- [Расширение / Extending](#расширение--extending)
- [Безопасность / Security](#безопасность--security)
- [Лицензия / License](#лицензия--license)

---

## Возможности / Features

### Русский

- **Интерактивная консоль** — выбор workspace при старте и диалог с агентом в реальном времени.
- **ReAct-цикл** — LLM планирует, вызывает инструменты, получает результаты и формирует ответ.
- **Работа с файлами** — чтение, запись, дополнение, удаление, копирование, перемещение, создание директорий и редактирование через `SEARCH/REPLACE`.
- **Терминал** — выполнение shell-команд (PowerShell 7 на Windows, `/bin/sh` на Linux/macOS).
- **Поиск в интернете** — интеграция с [Serper](https://serper.dev/) (Google Search API).
- **Чтение веб-страниц** — загрузка и извлечение текста через `HtmlAgilityPack`.
- **Гибкая настройка LLM** — поддержка OpenAI-совместимых API, Ollama, LM Studio, DeepSeek и других провайдеров.
- **Легкое расширение** — добавление новых агентов и инструментов через DI и атрибуты.

### English

- **Interactive console** — workspace selection at startup and real-time agent dialog.
- **ReAct loop** — the LLM plans, calls tools, receives results, and produces the final answer.
- **File operations** — read, write, append, delete, copy, move, directory creation/removal, and `SEARCH/REPLACE` editing.
- **Terminal** — execute shell commands (PowerShell 7 on Windows, `/bin/sh` on Linux/macOS).
- **Web search** — integration with [Serper](https://serper.dev/) (Google Search API).
- **Web page reading** — fetch and extract text via `HtmlAgilityPack`.
- **Flexible LLM setup** — supports OpenAI-compatible APIs, Ollama, LM Studio, DeepSeek, and other providers.
- **Easy extensibility** — add new agents and tools via DI and attributes.

---

## Архитектура / Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                         Program.cs                          │
│  (DI-контейнер, интерактивная консоль, история сообщений)   │
└───────────────────────┬─────────────────────────────────────┘
                        │
┌───────────────────────▼─────────────────────────────────────┐
│                    AgentRunner                              │
│  (цикл: LLM → tool calls → выполнение → повторный запрос)   │
└───────┬───────────────────────┬─────────────────────────────┘
        │                       │
┌───────▼───────┐       ┌───────▼───────┐
│  IChatService │       │  IToolHandler │
│ OpenAiChatSvc │       │ File / Search │
└───────────────┘       │ Terminal / Web│
                        └───────────────┘
```

### Структура проекта / Project Structure

| Папка / Folder | Назначение / Purpose |
|---|---|---|
| `AgentSharp/` | Базовый фреймворк: `IAgent`, `AgentBase`, `IAgentRunner`, `IChatService`, модели сообщений и инструментов, `ToolFactory`. / Base framework: agent abstractions, message/tool models, `ToolFactory`. |
| `Agents/` | Конкретные агенты и их инструменты. Сейчас: `CodeAssistant`. / Concrete agents and their tools. Currently: `CodeAssistant`. |
| `Agents/Common/Tools/` | Общие обработчики инструментов (`file`, `terminal`, `search`, `web_read`). / Common tool handlers. |
| `Infrastructure/` | DI-регистрация, реализация `AgentRunner`, реестр агентов, загрузчик промптов. / DI registration, `AgentRunner`, agent registry, prompt loader. |
| `Services/` | Конфигурационные модели и реализация `OpenAiChatService`. / Configuration models and `OpenAiChatService`. |

### Агент `CodeAssistant`

- Идентификатор: `code`
- Имя: `Click`
- Системный промпт загружается из `Agents/CodeAssistant/Prompts/System.md` и параметризуется путём workspace, датой/временем и ОС.

### Доступные инструменты / Available Tools

| Инструмент / Tool | Обработчик / Handler | Описание / Description |
|---|---|---|
| `file` | `FileToolHandler` | Операции с файлами и директориями внутри workspace. / File and directory operations within the workspace. |
| `terminal` | `TerminalToolHandler` | Выполнение shell-команд. / Shell command execution. |
| `web_read` | `WebReadToolHandler` | Загрузка и чтение веб-страниц. / Fetch and read web pages. |
| `search` | `SearchToolHandler` | Поиск в Google через Serper. / Google search via Serper. |

---

## Установка и запуск / Installation & Run

### Требования / Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git

### Клонирование / Clone

```bash
git clone https://github.com/AmnesiaCode888/Click.git
cd Click
```

### Сборка / Build

```bash
dotnet build Click.csproj
```

### Запуск / Run

```bash
dotnet run --project Click.csproj
```

### Публикация / Publish

```bash
dotnet publish Click.csproj -c Release -o ./out
./out/Click.exe   # Windows
./out/Click       # Linux / macOS
```

### Интерактивные команды / Interactive Commands

- `/exit` — завершить сессию / exit the session.
- `/clear` — очистить историю сообщений / clear message history.

---

## Конфигурация / Configuration

Конфигурация задаётся в `appsettings.json`.

```json
{
  "Click": {
    "BasePath": "D:/ClickProjects"
  },
  "OpenAi": {
    "BaseUrl": "",
    "ApiKey": "",
    "Model": "deepseek/deepseek-v4-flash",
    "RequestTimeoutSeconds": 60
  },
  "Serper": {
    "ApiKey": ""
  }
}
```

### Настройка LLM / LLM Setup

#### OpenAI / OpenAI-совместимый провайдер

```json
{
  "OpenAi": {
    "BaseUrl": "https://api.openai.com/v1",
    "ApiKey": "sk-...",
    "Model": "gpt-4o-mini"
  }
}
```

#### DeepSeek

```json
{
  "OpenAi": {
    "BaseUrl": "https://api.deepseek.com/v1",
    "ApiKey": "sk-...",
    "Model": "deepseek-chat"
  }
}
```

#### Ollama (локально)

```json
{
  "OpenAi": {
    "Model": "ollama/llama3.1"
  }
}
```

> Ollama автоматически использует `http://localhost:11434/v1`.

#### LM Studio (локально)

```json
{
  "OpenAi": {
    "Model": "lmstudio/llama-3.2-1b-instruct"
  }
}
```

> LM Studio автоматически использует `http://localhost:1234/v1`.

#### Serper (поиск)

```json
{
  "Serper": {
    "ApiKey": "YOUR_SERPER_API_KEY"
  }
}
```

> Инструмент `search` добавляется только при наличии `Serper:ApiKey`.

### Настройка логирования / Logging

Уровень логирования задаётся в секции `Logging` того же `appsettings.json`. По умолчанию подробные HTTP-логи от `System.Net.Http.HttpClient` отключены, а логи инструментов (`file`, `terminal`, `search`, `web_read`) остаются видимыми.

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "System.Net.Http.HttpClient": "Error"
    }
  }
}
```

Чтобы временно включить детальное логирование HTTP-запросов, измените уровень `System.Net.Http.HttpClient` на `Information` или `Debug`.

### Настройка агента и инструментов / Agent and Tool Configuration

Поведение агента и инструментов полностью конфигурируется через `appsettings.json` (или `appsettings.Development.json`).

```json
{
  "Agent": {
    "MaxIterations": 15,
    "LoopDetectionWindow": 2,
    "MaxToolResultCharsKeep": 2500,
    "MaxToolResultCharsSuccess": 400,
    "PreserveRecentToolRounds": 2
  },
  "Chat": {
    "MaxHistoryMessages": 20,
    "MaxHistoryChars": 25000
  },
  "File": {
    "MaxReadChars": 12000,
    "DefaultReadLimit": 250
  },
  "Search": {
    "DefaultMaxResults": 5,
    "MinMaxResults": 1,
    "MaxMaxResults": 20
  },
  "WebRead": {
    "DefaultMaxLength": 8000
  },
  "Terminal": {
    "DefaultTimeoutSeconds": 60,
    "MinTimeoutSeconds": 1,
    "MaxTimeoutSeconds": 300,
    "MaxOutputChars": 6000
  }
}
```

| Секция / Section | Описание / Description |
|---|---|
| `Agent` | `MaxIterations` — максимальное число итераций ReAct-цикла; `LoopDetectionWindow` — окно детекции повторяющихся вызовов инструментов; `MaxToolResultCharsKeep` / `MaxToolResultCharsSuccess` — лимиты длины результатов инструментов в истории; `PreserveRecentToolRounds` — сколько последних tool-раундов сохранять полностью. / `MaxIterations` limits the ReAct loop; `LoopDetectionWindow` detects repeated tool calls; result size limits control history compaction. |
| `Chat` | `MaxHistoryMessages` — максимальное число сообщений в истории; `MaxHistoryChars` — максимальное число символов. / `MaxHistoryMessages` and `MaxHistoryChars` cap the chat history sent to the LLM. |
| `File` | `MaxReadChars` — максимальный размер читаемого файла; `DefaultReadLimit` — лимит строк по умолчанию. / `MaxReadChars` caps file size; `DefaultReadLimit` is the default line limit. |
| `Search` | `DefaultMaxResults` — число результатов по умолчанию; `MinMaxResults` / `MaxMaxResults` — допустимые границы, запрашиваемые у агента. / `DefaultMaxResults` and min/max bounds for search result count. |
| `WebRead` | `DefaultMaxLength` — максимальная длина извлекаемого текста веб-страницы. / `DefaultMaxLength` caps extracted web page text. |
| `Terminal` | `DefaultTimeoutSeconds` — таймаут команды по умолчанию; `MinTimeoutSeconds` / `MaxTimeoutSeconds` — допустимые границы; `MaxOutputChars` — максимальная длина вывода, сохраняемого в историю. / `DefaultTimeoutSeconds`, min/max timeout bounds and `MaxOutputChars` cap. |

### Где хранить секреты / Where to Store Secrets

**Никогда не храните API-ключи в `appsettings.json`, который попадает в Git.**

Используйте:

- `appsettings.Development.json`
- `appsettings.Local.json`
- Переменные окружения
- Секреты пользователя .NET (`dotnet user-secrets`)

Эти файлы уже исключены из репозитория через `.gitignore`.

---

## Использование / Usage

1. Запустите приложение:

```bash
dotnet run --project Click.csproj
```

2. Выберите рабочую директорию (workspace).

3. Задайте запрос агенту, например:

```text
Создай консольное приложение на C#, которое выводит текущее время.
```

4. Агент выполнит шаги:
   - Проанализирует задачу.
   - Создаст файлы через инструмент `file`.
   - При необходимости выполнит команды через `terminal`.
   - Вернёт результат и объяснение.

### Пример / Example

```text
> Создай файл README.md с приветствием
[Click] Создаю файл README.md...
[Click] Готово. Файл README.md создан с приветствием.
```

---

## Расширение / Extending

### Как добавить нового агента / How to Add a New Agent

1. Создайте класс, наследующий `AgentBase` или реализующий `IAgent`.
2. Определите `Id`, `Name` и метод `GetSystemPrompt(AgentContext)`.
3. Добавьте нужные инструменты через `AddTool<TArgs>(...)`.
4. Поместите системный промпт в `Agents/{YourAgent}/Prompts/System.md`.
5. Зарегистрируйте хендлеры инструментов как singleton в `Infrastructure/ServiceCollectionExtensions.cs`.

Реестр `AgentRegistry` автоматически найдёт все реализации `IAgent`.

### Как добавить новый инструмент / How to Add a New Tool

1. Создайте DTO аргументов с атрибутами `[ToolParameter]` и `[JsonPropertyName]`.
2. Реализуйте `IToolHandler`.
3. Зарегистрируйте хендлер как singleton в DI.
4. Добавьте инструмент в агента:

```csharp
AddTool<MyToolArgs>(
    name: "my_tool",
    description: "Описание инструмента",
    handler: myToolHandler);
```

### Как добавить нового LLM-провайдера / How to Add a New LLM Provider

1. Реализуйте `IChatService`.
2. Зарегистрируйте реализацию в DI вместо `OpenAiChatService`.

---

## Безопасность / Security

- Все API-ключи и локальные конфигурации исключены из Git через `.gitignore`.
- Инструмент `file` работает только внутри выбранного workspace.
- Инструмент `terminal` выполняет команды с правами текущего пользователя — используйте его осторожно.
- Перед коммитом убедитесь, что в `appsettings.json` и других отслеживаемых файлах нет реальных секретов.

---

## Лицензия / License

Этот проект распространяется под лицензией **GNU Affero General Public License v3.0 (AGPLv3)**.

This project is licensed under the **GNU Affero General Public License v3.0 (AGPLv3)**.

Подробнее см. файл [LICENSE](./LICENSE) или [https://www.gnu.org/licenses/agpl-3.0.html](https://www.gnu.org/licenses/agpl-3.0.html).
