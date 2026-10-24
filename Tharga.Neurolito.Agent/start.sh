#!/bin/sh
set -e

#NOTE: A hosted engine runs nothing here, so Ollama is neither started nor waited for.
case "$(echo "${Engine__Kind:-}" | tr '[:upper:]' '[:lower:]')" in
  anthropic)
    echo "Engine is Anthropic: Ollama is not started."
    ;;
  *)
    echo "Starting Ollama..."
    ollama serve &

    echo "Waiting for Ollama to be ready..."
    until curl -s http://localhost:11434/api/tags > /dev/null; do
      sleep 1
    done
    ;;
esac

echo "Starting Tharga.Neurolito.Agent..."
exec dotnet Tharga.Neurolito.Agent.dll
