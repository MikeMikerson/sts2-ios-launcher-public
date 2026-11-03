using Mono.Cecil;
using Mono.Cecil.Cil;

namespace STS2Mobile.GamePatcher;

internal static class NetworkPatches
{
    // The game opens one ENet connection to host a LAN game and one to join it.
    private const int ExpectedEnetOpeners = 2;

    public static IEnumerable<PatchStep> Steps => [new("No LAN play", true, RefuseLan)];

    private static void RefuseLan(GamePatch patch)
    {
        // Refuse raw LAN sockets at their containing methods, including generated async bodies.
        foreach (var method in patch.MethodsWithBodies.ToArray())
            if (method.Body.Instructions.Any(i => i.Operand is MethodReference target &&
                target.DeclaringType.Namespace == "System.Net.Sockets"))
                patch.Reject(method, "LAN networking is disabled by the Steam/Mega Crit destination rule.");

        // LAN play also runs over the engine's own ENet transport, which never touches System.Net.Sockets.
        int enetOpeners = 0;
        foreach (var method in patch.MethodsWithBodies.ToArray())
            if (method.Body.Instructions.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference target &&
                target.DeclaringType.FullName == "Godot.ENetConnection"))
            {
                patch.Reject(method, "LAN networking over ENet is disabled by the Steam/Mega Crit destination rule.");
                enetOpeners++;
            }
        patch.Expect(enetOpeners, ExpectedEnetOpeners, "ENet connection openers (host and client)");
    }
}
