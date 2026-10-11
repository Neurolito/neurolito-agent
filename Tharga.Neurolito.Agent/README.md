# Neurolito Agent

> **Installing an agent on a machine?** Use the [install guide](https://neurolito.com/install-agent) on the site. It asks
> what the machine is - operating system, graphics card, container runtime - and shows the one route
> that fits, with a check that the agent connected. The rest of this file is for working *on* the
> agent rather than installing it.

*Neurolito Agent* can be installed in different ways. One is as a Linux container that also contains
*Ollama*, which runs on Windows (Docker Desktop), Linux and macOS. Another is to install using
Chocolatey and run as a Windows service.

## Install with Chocolatey
You need [Ollama](https://ollama.com/download) and [Chocolatey](https://chocolatey.org/install) on the
machine. Nothing else - the package is self-contained, so the .NET runtime does not have to be installed.

The agent package is served by Neurolito itself, and the command carries **your team's install key**, so a
download can be attributed to your team and a key revoked without disturbing anyone else. Sign in and open
the [install guide](https://neurolito.com/install-agent); it shows the two commands filled in and ready to
copy:

```powershell
choco source add -n=neurolito -s="https://neurolito.com/nuget/v3/index.json" -u="<your team>" -p="<your key>" --priority=1
choco install neurolito-agent -y -s neurolito
```

Registering the source stores the key on the machine, so later upgrades need no credential at all:

```powershell
choco upgrade neurolito-agent -y
```

The source needs no credential once the feed allows anonymous read. Until then it does, which is why
the install guide is the place that stays current.

## Install as a container
The image is built from `Dockerfile`: `ollama/ollama` with the ASP.NET Core runtime and the agent on
top, started by `start.sh` (Ollama first, then the agent once Ollama answers). It is served to teams
through the site's registry at `/v2/`, behind the team's install key, and the
[install guide](https://neurolito.com/install-agent) shows the `docker login` and `docker run` commands
filled in. On Windows, `install.agent.ps1` at the root of this repository does the same from PowerShell.

The container reads its settings from the environment:

| Variable | Why |
|---|---|
| `Tharga__Communication__ApiKey` | The team install key. Without it the agent connects unassigned and runs nothing. |
| `Tharga__Communication__ServerAddress` | The server to join. Defaults to `https://neurolito.com/` in Production. |
| `ASPNETCORE_URLS=http://+:5000` | Required - Kestrel otherwise binds to `localhost` inside the container. |

Mount a volume at `/root/.ollama` so models survive an upgrade, and pass `--gpus all` to use an NVIDIA
card.

---

## Running the agent
The agent can be run in several ways.

### Local Agent in docker
From the root of the repository run `.\install.agent.ps1 -NoPull`. It builds the image from source and
starts it; the agent answers on [http://localhost:5101/swagger/](http://localhost:5101/swagger/).

### Local Agent native
From the root of the repository run
`$env:ASPNETCORE_ENVIRONMENT = "Production"; dotnet run --project .\Tharga.Neurolito.Agent\Tharga.Neurolito.Agent.csproj --launch-profile https`

### Context for Anthropic-compatible requests
Ollama's Anthropic endpoint (`/v1/messages`) takes no context option, and its default keeps only the end
of a long prompt, so a Claude Code request loses its system prompt and tools. The agent therefore runs
these requests on a copy of the model with a larger context, named like
`qwen3-coder-neurolito-ctx32768:30b`. The copy shares the model's files, is created on first use, is never
listed as a model of its own, and is removed when the model is uninstalled.

`Ollama__AnthropicContextLength` sets the context in tokens (default `32768`, capped per model at what it
was trained for). `0` turns it off and forwards to the model as installed. The agent reports the setting to
the server, which shows it on the agent page.

## Building Chocolatey package
In the `Tharga.Neurolito.Agent` folder run...
- `.\Resources\pack-choco.ps1` to build the package
- `choco install neurolito-agent --version="1.0.1" --source=".\choco-temp" -y` to install the package
- Go to [http://localhost:5001/swagger/](http://localhost:5001/swagger/)

---

## Running from Visual Studio
- Have *ollama* running separatley on port 11434.
- Start the agent using https (or http) and access it here
- [https://localhost:7200/swagger/](https://localhost:7200/swagger/)
- [http://localhost:5015/swagger/](http://localhost:5015/swagger/)

### Install Ollama in docker
- `docker pull ollama/ollama:latest`
- `docker rm -f ollama 2>$null`

#### Start Ollama without GPU
`docker run -d --name ollama -p 11434:11434 -v ollama:/root/.ollama ollama/ollama:latest`

#### Start Ollama with GPU
`docker run -d --name ollama --gpus all -p 11434:11434 -v ollama:/root/.ollama ollama/ollama:latest`

[Ollama Models](https://ollama.com/search)
- [tinyllama](https://localhost:7200/Ollama/models/install/tinyllama)
- [gemma:2b](https://localhost:7200/Ollama/models/install/gemma%3A2b)
- [gemma3:1b](https://localhost:7200/Ollama/models/install/gemma3%3A1b)
- [llama-guard3](https://localhost:7200/Ollama/models/install/llama-guard3%3A1b)

# How to start an agent from .NET
- `dotnet run --project Tharga.Neurolito.Agent.csproj`
