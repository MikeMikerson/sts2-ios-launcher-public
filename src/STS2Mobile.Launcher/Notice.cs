namespace STS2Mobile.Launcher;

/// <summary>
/// How something the player started ended: a download, getting cloud saves, deleting the game. Shown on the screens after it until the
/// player does something else, so the result is never just a change of sentence.
/// </summary>
public sealed record Notice(bool Succeeded, string Title, string Text)
{
    public static Notice Success(string title, string text) => new(true, title, text);
    public static Notice Failure(string title, string text) => new(false, title, text);
}
