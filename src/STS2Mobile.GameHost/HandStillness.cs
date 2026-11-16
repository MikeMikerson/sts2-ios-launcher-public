using System.Collections.Generic;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// Keeps hand cards on their portrait paths when the game re-lays out the hand for a desktop (without it the hand bounced on
/// every touch, as the cards came in at a fight's start and after each card played).
/// The game's layout (<c>NPlayerHand.RefreshLayout</c>) runs on every card drawn, played, focused or put back and gives every
/// card a desktop target, about 490 units below its portrait slot; setting a target moves the card one animation step at once
/// (about an eighth of the way), before the per-frame pass can set its portrait target again. So each card's place is recorded
/// at every pass, and a card whose target the game changed since the last pass is put back where that pass left it: it then
/// moves only toward its portrait slot, as smoothly as the game's own animation.
/// </summary>
internal sealed class HandStillness
{
    private readonly Dictionary<ulong, Place> _records = new();
    private int _passes;

    /// <summary>
    /// For the proof runs only (<c>phone-notes</c>): the earlier rule, which put a card back only when its own slot
    /// had not changed, so the motion of both can be measured in one run. Never set otherwise.
    /// </summary>
    internal static bool ProofOldRule;

    private struct Place
    {
        public Vector2 Target;
        public Vector2 Position;
        public Vector2 Scale;
        public float Rotation;
        public ulong Frame;
    }

    /// <summary>
    /// Before setting the holder's target to its slot: when the last pass (this frame or the one before) left the holder with
    /// a target and the game has given it another since, put the holder back where that pass left it. Returns whether it did.
    /// A record from an older frame (the card was held, or out of the hand) is not used: the card starts from where it is.
    /// </summary>
    public bool RestoreIfRetargeted(Control holder, Vector2 slot, Vector2 currentTarget)
    {
        if (!_records.TryGetValue(holder.GetInstanceId(), out Place record)) return false;
        if (ProofOldRule)
        {
            if ((record.Target - slot).LengthSquared() > 0.25f || (currentTarget - slot).LengthSquared() <= 0.25f) return false;
        }
        else
        {
            if (Engine.GetProcessFrames() - record.Frame > 1) return false;
            if ((currentTarget - record.Target).LengthSquared() <= 0.25f) return false;
        }
        holder.Position = record.Position;
        holder.Scale = record.Scale;
        holder.RotationDegrees = record.Rotation;
        return true;
    }

    /// <summary>After setting the holder's target to its slot: the target and where the holder is now.</summary>
    public void Record(Control holder, Vector2 target)
    {
        _records[holder.GetInstanceId()] = new Place
        {
            Target = target,
            Position = holder.Position,
            Scale = holder.Scale,
            Rotation = holder.RotationDegrees,
            Frame = Engine.GetProcessFrames(),
        };
        if (++_passes % 600 == 0)
        {
            var dead = new List<ulong>();
            foreach (ulong id in _records.Keys)
                if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
            foreach (ulong id in dead) _records.Remove(id);
        }
    }

    public void Forget(Control holder) => _records.Remove(holder.GetInstanceId());
}
