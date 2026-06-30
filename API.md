# Click API Documentation

Base URL: `http://localhost:5077`

Port is configurable in `appsettings.json` under `"Api": { "Port": 5077 }`.

CORS is enabled for all origins.

---

## Endpoints

### POST /api/chat/stream

SSE streaming chat. Returns `text/event-stream`.

**Request:**
```json
{
  "message": "string (required)",
  "mode": "code | question | security (optional, default: code)",
  "model": "string (optional, overrides default model)"
}
```

**Response headers:**
```
Content-Type: text/event-stream
Cache-Control: no-cache
X-Accel-Buffering: no
```

**SSE events (each line starts with `data: `):**

| type | Fields | Description |
|---|---|---|
| `progress` | `text`, `step` | Agent iteration status (e.g. "Analyzing request") |
| `reasoning` | `text` | LLM reasoning/thinking (truncated to 500 chars) |
| `tool` | `name`, `action`, `status` | Tool call — `status` is `"ok"`, `"running"`, or `"error"` |
| `content` | `text` | Final answer text from the agent |
| `done` | `usage` | Completion signal. `usage` contains `promptTokens`, `completionTokens`, `totalTokens` |
| `error` | `text` | Error message |

Terminal event: `data: [DONE]`

**Full example stream:**
```
data: {"type":"progress","text":"Analyzing request","step":0}

data: {"type":"progress","text":"Iteration 1","step":1}

data: {"type":"tool","name":"file","action":"file read src/Program.cs — ...","status":"ok"}

data: {"type":"content","text":"Here is what I found..."}

data: {"type":"done","usage":{"promptTokens":1523,"completionTokens":87,"totalTokens":1610}}

data: [DONE]
```

**Example (JavaScript / Electron):**
```javascript
const response = await fetch('http://localhost:5077/api/chat/stream', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ message: 'Explain this project', mode: 'code' })
});

const reader = response.body.getReader();
const decoder = new TextDecoder();
let buffer = '';

while (true) {
  const { done, value } = await reader.read();
  if (done) break;

  buffer += decoder.decode(value, { stream: true });
  const lines = buffer.split('\n');
  buffer = lines.pop(); // keep incomplete line

  for (const line of lines) {
    if (!line.startsWith('data: ')) continue;
    const data = line.slice(6);
    if (data === '[DONE]') return;

    const event = JSON.parse(data);
    switch (event.type) {
      case 'content':   appendToUI(event.text); break;
      case 'tool':      showToolStatus(event);  break;
      case 'reasoning':  showThinking(event);    break;
      case 'done':      showStats(event.usage); break;
      case 'error':     showError(event.text);  break;
    }
  }
}
```

---

### POST /api/chat

Regular (non-streaming) chat. Returns JSON when complete.

**Request:**
```json
{
  "message": "string (required)",
  "mode": "code | question | security (optional, default: code)",
  "model": "string (optional)"
}
```

**Success 200:**
```json
{
  "content": "Agent's response text",
  "usage": {
    "promptTokens": 1523,
    "completionTokens": 87,
    "totalTokens": 1610
  }
}
```

**Error 400:**
```json
{
  "error": "Missing message"
}
```

**Example:**
```javascript
const res = await fetch('http://localhost:5077/api/chat', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ message: 'What does this project do?' })
});
const data = await res.json();
console.log(data.content);
```

---

### GET /api/models

Returns available LLM models from the configured provider.

**Success 200:**
```json
{
  "models": ["openai/gpt-4o", "openai/gpt-4o-mini", "groq/llama-3.1-70b-versatile"]
}
```

**Example:**
```javascript
const res = await fetch('http://localhost:5077/api/models');
const { models } = await res.json();
```

---

### GET /api/status

Returns current server state.

**Success 200:**
```json
{
  "mode": "code",
  "model": "groq/openai/gpt-oss-120b",
  "workspace": "D:/MyProject",
  "historyMessages": 4,
  "port": 5077
}
```

---

### POST /api/mode

Switch the active agent mode.

**Request:**
```json
{
  "mode": "code | question | security"
}
```

Mode aliases: `q` = question, `s` = security, anything else = code.

**Success 200:**
```json
{
  "mode": "code",
  "agentName": "Code Assistant"
}
```

**Error 400:**
```json
{
  "error": "Missing mode"
}
```

---

### POST /api/clear

Clear all conversation histories (all modes).

**Success 200:**
```json
{
  "ok": true
}
```

---

### GET /api/health

Health check.

**Success 200:**
```json
{
  "status": "ok",
  "port": 5077
}
```

---

## Modes

| Mode | Agent | Description |
|---|---|---|
| `code` | Code Assistant | Full access: read, write, terminal execution |
| `question` | Question | Read-only: code consultations, explanations |
| `security` | Security Review | Read-only: vulnerability analysis |

Each mode has its own independent conversation history.

---

## Configuration

`appsettings.json`:
```json
{
  "Api": {
    "Port": 5077
  }
}
```

The API server starts automatically when Click launches, alongside the interactive console. Both run in parallel. When the console exits, the API server shuts down.

---

## Architecture

```
Electron App
    |
    | HTTP / SSE
    v
Click API Server (port 5077)
    |
    | IAgentRunner
    v
AgentRunner -> IChatService -> LLM Provider
    |
    | IProgress<AgentRunnerProgress>
    v
SSE Stream -> Electron
```

- The API server shares the same DI container as the console
- Each mode (code/question/security) has its own conversation history
- History is trimmed at 40 messages or 50,000 characters per mode
- Tool calls are executed with a 120-second global timeout
- Agent max iterations: 2000 (configurable in `appsettings.json` under `Agent:MaxIterations`)
