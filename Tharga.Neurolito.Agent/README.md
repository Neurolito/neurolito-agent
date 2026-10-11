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

### llama-server instead of Ollama
`Engine__Kind=LlamaServer` makes the agent drive [llama.cpp](https://github.com/ggml-org/llama.cpp)'s
`llama-server` itself instead of talking to Ollama. It starts it as a child process, one model at a time, and
unloads an idle model to give the memory back; on Windows the server ends with the agent. Models keep their Ollama
names: the server says which Hugging Face file a name stands for, pinned to a commit and its checksum, and the agent
downloads it. A model can also be named directly as `hf.co/{owner}/{repo}:{quantisation}`.

| Setting | |
|---|---|
| `LlamaServer__ExecutablePath` | The `llama-server` program. Default: `llama/llama-server(.exe)` next to the agent. |
| `LlamaServer__ModelsPath` | Where models are kept. Default: `Neurolito/models` in the machine's application data folder. |
| `LlamaServer__ContextLength` | The context a prompt gets when it asks for none (default `8192`), capped at what the model was trained for. |
| `LlamaServer__IdleUnloadMinutes` | Unload a model after this long without a request (default `10`); `0` keeps it loaded. |
| `LlamaServer__GpuLayers` | Layers on the GPU (default `999`, as many as fit). |
| `LlamaServer__ExtraArguments` | More arguments for `llama-server`, for example `--flash-attn on`. |
| `LlamaServer__HuggingFaceToken` | For gated models. Optional. |

### Desktop agent
`Desktop__Enabled=true` (Windows) makes the agent share a machine with the person using it. The
[Windows installer](https://github.com/Neurolito/neurolito-agent-windows) turns it on. The machine counts as free
while a screensaver runs, the console is locked or nobody is logged on; the agent then takes work, and when someone
returns it finishes the running job and takes no new one. It tells the server, which shows the machine as *In use*,
*Ready*, *Working* or *Asleep*.

| Setting | |
|---|---|
| `Desktop__EnergyMode` | `Never` (default), `WhileWorking` or `WhileReady`: when the agent keeps the machine from sleeping. |
| `Desktop__ResumeDelaySeconds` | How long the machine must be free before work starts (default `15`). |
| `Desktop__AlwaysWork` | Take work even while the machine is in use. |
| `Desktop__PipeName` | The local pipe the screensaver reads (default `neurolito-agent`). It carries the shape of the work, never its text. |

On Windows the agent also reads its settings from `HKLM\SOFTWARE\Neurolito\Agent` (one value per setting, named as
in configuration, for example `Desktop:EnergyMode`), where the installer writes them.

### Updates
`Updates__Enabled=true` with `Updates__Source` (a GitHub repository address, a web folder or a local folder) lets an
agent installed with [Velopack](https://velopack.io) update itself. Every few hours it looks for a new version,
downloads it, stops taking work, lets the running job finish, applies the update and exits, so that the service
manager starts it again on the new version. An agent not installed that way never updates itself.

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
