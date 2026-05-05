# Ollama to LM Studio Adapter

This service exposes a focused Ollama-compatible API and proxies requests to LM Studio's OpenAI-compatible local server.

This implementation is primarily tailored for use with Visual Studio Code's GitHub Copilot and related editor integrations.

## Supported endpoints

- `GET /` returns adapter metadata.
- `GET /health` returns a simple health payload.
- `GET /api/version` returns the adapter version.
- `GET /api/tags` maps to LM Studio `GET /v1/models`.
- `POST /api/show` retrieves information about a specific model.
- `GET /v1/models` exposes an OpenAI-compatible model list for clients such as VS Code.
- `POST /api/generate` maps to LM Studio `POST /v1/completions`.
- `POST /api/chat` maps to LM Studio `POST /v1/chat/completions`, including Ollama-style tool definitions, assistant `tool_calls`, and follow-up `tool` messages.
- `POST /v1/completions` proxies OpenAI-compatible completion requests to LM Studio.
- `POST /v1/chat/completions` proxies OpenAI-compatible chat completion requests to LM Studio.

## Configuration

Configure LM Studio in `src/Ollama2LmStudioAdapter.Api/appsettings.json` or environment overrides.

```json
{
  "LmStudio": {
    "BaseUrl": "http://localhost:1234/v1/",
    "TimeoutSeconds": 300,
    "ApiKey": null
  },
  "Adapter": {
    "Version": "0.21.0",
    "DefaultGenerateMaxTokens": 512,
    "DefaultChatMaxTokens": 512
  }
}
```

LM Studio must have its local server enabled.

## Streaming behavior

The adapter consumes LM Studio Server-Sent Events and emits Ollama-style newline-delimited JSON. Each streaming request ends with a final object where `done` is `true`.

The OpenAI-compatible `/v1/completions` and `/v1/chat/completions` endpoints forward LM Studio's JSON and SSE responses without remapping so OpenAI clients receive the expected schema.

## Known limitations

- Embeddings and model management endpoints are not implemented.
- Hardware-specific Ollama options such as `num_gpu`, `num_thread`, and `num_ctx` are rejected with `400`.
- Metadata fields in `/api/tags` that LM Studio does not expose are synthetic placeholders.

## Verification

Use `src/Ollama2LmStudioAdapter.Api/Ollama2LmStudioAdapter.Api.http` for manual checks after starting LM Studio and the adapter.