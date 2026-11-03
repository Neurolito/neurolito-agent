# Tharga.Neurolito.Client

Sends prompts to [Neurolito](https://neurolito.com), which queues them and runs them on your team's agents.

## Register

```csharp
builder.Services.AddNeurolito(o =>
{
    o.ApiKey = builder.Configuration["Neurolito:ApiKey"];
});
```

`ServerAddress` defaults to `https://neurolito.com/api/`. The API key is created in Neurolito for your team.

## Queue a prompt

```csharp
var input = new InputBuilder()
    .Model("llama3.1:8b-instruct-q4_K_M")
    .System("Answer in one sentence.")
    .User("What is a large language model?")
    .Build();

var response = await neurolito.EnqueueAsync(input);
if (!response.Success) throw new InvalidOperationException(response.Message);

var status = await neurolito.GetStatusAsync(response.RequestId!.Value);
```

`INeurolitoService` also cancels a queued prompt (`CancelAsync`), votes on an answer (`VoteAsync`) and
checks the connection (`CanConnectAsync`). Pass a `ResponseInstruction` to have the answer delivered to
you when the job completes.

## Fall back to other models

Name the models to try, in order, when no machine can take the first one in time. A hosted model is used
only when your team has turned on hosted models.

```csharp
var input = new InputBuilder()
    .Model("llama3.1:8b-instruct-q4_K_M")
    .Fallback("qwen2.5:3b-instruct-q4_K_M", "claude-haiku-5-5")
    .FallbackAfter(TimeSpan.FromSeconds(20))
    .User("What is a large language model?")
    .Build();
```

## Get a model ready

`GetReadyAsync(model)` says a request for that model is coming soon, so the machine it would most likely
land on loads it now. It returns at once, generates nothing and is limited to once a minute per model.

## Source and issues

[github.com/Neurolito/neurolito-agent](https://github.com/Neurolito/neurolito-agent)
