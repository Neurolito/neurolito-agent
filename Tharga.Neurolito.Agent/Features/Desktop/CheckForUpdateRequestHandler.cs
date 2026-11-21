using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>
/// Looks for a new version when the server asks, and updates if told to. An agent with no updater (updates off, or not
/// installed by the Windows installer) answers that it cannot, instead of staying silent.
/// </summary>
internal sealed class CheckForUpdateRequestHandler : SendMessageHandlerBase<CheckForUpdateRequest, CheckForUpdateResponse>
{
    private readonly IAgentUpdateService _updates;

    public CheckForUpdateRequestHandler(IAgentUpdateService updates = null)
    {
        _updates = updates;
    }

    public override Task<CheckForUpdateResponse> Handle(CheckForUpdateRequest message)
    {
        if (_updates == null)
        {
            return Task.FromResult(new CheckForUpdateResponse
            {
                Status = UpdateStatus.Disabled,
                Message = "Updates are off for this agent: it was not installed by the Windows installer, or Updates:Enabled is off."
            });
        }

        return _updates.CheckAsync(message?.Apply == true, CancellationToken.None);
    }
}
