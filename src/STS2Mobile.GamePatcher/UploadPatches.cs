using Mono.Cecil.Cil;

namespace STS2Mobile.GamePatcher;

// Nothing is uploaded to anyone: no run, achievement, epoch or settings metrics, and no feedback
// package (zipped logs plus a screenshot). Both the decision and the transmit step are removed.
internal static class UploadPatches
{
    private const string Metrics = "MegaCrit.Sts2.Core.Runs.Metrics.MetricUtilities";

    public static IEnumerable<PatchStep> Steps =>
    [
        new("No gameplay metrics sent", true, patch =>
        {
            var decide = patch.Method(Metrics, "ShouldUploadMetrics");
            var send = patch.Method(Metrics, "PutRequest");
            patch.Noop(decide, "Never upload gameplay metrics.");
            patch.Complete(send, "Remove the metrics transmit step.");
        }),
        new("No logs or screenshots sent as feedback", true, patch =>
            patch.Replace(patch.Method("MegaCrit.Sts2.Core.Nodes.Screens.FeedbackScreen.NSendFeedbackScreen", "SendFeedback"), "Never upload logs or screenshots as feedback.", il =>
            {
                il.Emit(OpCodes.Call, patch.Adapter("RuntimeServices", "RejectFeedbackUploadAsync"));
                il.Emit(OpCodes.Ret);
            })),
    ];
}
