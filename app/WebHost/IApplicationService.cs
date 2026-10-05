using System.Text.Json;

namespace OverlayDisk.WebHost;

public interface IApplicationService
{
    event EventHandler? StateChanged;
    Task<object?> GetStateAsync(CancellationToken cancellationToken);
    Task<object?> InvokeAsync(string method, JsonElement args, CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);
}
