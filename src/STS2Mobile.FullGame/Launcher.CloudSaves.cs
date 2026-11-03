using System.Threading;
using System.Threading.Tasks;

namespace STS2Mobile.FullGame;

/// <summary>Send on the cloud saves screen. Get is wired with the other screens; both end on the first screen with a notice.</summary>
public partial class Launcher
{
    private CloudSaveSendFlow _send = null!;

    private void WireCloudSaves()
    {
        _send = new CloudSaveSendFlow(_view, _login);
        _view.CloudSendRequested += () => RunAsync(null, "Connecting to Steam...", _login.ConnectAndVerifyAsync, SendCloudSavesAsync).FireAndForget("Cloud send");
    }

    private async Task SendCloudSavesAsync(CancellationToken attempt)
    {
        var (result, status) = await _send.SendAsync(attempt);
        _view.SetNotice(result);
        ShowEntry(status);
    }
}
