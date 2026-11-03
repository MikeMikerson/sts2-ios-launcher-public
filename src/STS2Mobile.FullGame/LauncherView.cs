using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using QRCoder;
using STS2Mobile.Launcher;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

/// <summary>
/// Presentation only: builds the launcher's controls, switches between its screens and reports what the player did. It knows nothing
/// about Steam services or the game, so sign-in methods and screens can be added here without touching startup logic.
/// </summary>
/// <remarks>
/// Portrait. Every screen is one column: the wordmark and Back at the top, a heading, the climb of setup steps when setting up,
/// what is happening, and the actions at the bottom within reach of a thumb. Forms keep their action right under their fields, above the
/// on-screen keyboard. The look is <see cref="LauncherTheme"/>'s.
/// </remarks>
public sealed partial class LauncherView
{
    private const int QrPixels = 560;
    private const float ContentWidth = 1000;
    private const string AppName = "STS2 Mobile";
    private const string CloseLabel = "Close the app";
    private readonly Control _root;
    private readonly MarginContainer _frame;
    private readonly Label _heading;
    private readonly Label _status;
    private readonly PanelContainer _notice;
    private readonly Label _noticeTitle;
    private readonly Label _noticeText;
    private readonly ClimbRail _climb = new();
    private readonly ProgressBar _bar;
    private readonly LineEdit _account;
    private readonly LineEdit _password;
    private readonly LineEdit _guardCode;
    private readonly PanelContainer _qrFrame;
    private readonly TextureRect _qr;
    private readonly VBoxContainer _rows;
    private readonly Control _spacer;
    private readonly Button _back;
    private readonly Button _useAccount;
    private readonly Button _useQr;
    private readonly Button _choiceFirst;
    private readonly Button _choiceSecond;
    private readonly Button _formSubmit;
    private readonly Button _play;
    private readonly Button _signInAgain;
    private readonly Button _signOut;
    private readonly Button _download;
    private readonly Button _chooseVersion;
    private readonly Button _deleteGame;
    private readonly Button _cloudGet;
    private readonly Dictionary<string, Button> _rowButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rowValues = new(StringComparer.Ordinal);
    private readonly Control[] _switchable;
    private string? _lastLogged;
    // Button labels to press in order as they appear, to test a flow on the simulator with nobody there. Empty for a player.
    private readonly Queue<string> _autoTap = new((OS.GetEnvironment("STS2_AUTO_TAP") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Account name and password as typed. The view keeps neither.</summary>
    public event Action<string, string>? PasswordSignInRequested;
    public event Action? QrSignInRequested;
    public event Action<string>? GuardCodeSubmitted;
    public event Action? BackRequested;
    /// <summary>The player answered <see cref="ShowChoice"/>; true is the first answer.</summary>
    public event Action<bool>? ChoiceMade;
    public event Action? PlayRequested;
    public event Action? ManageRequested;
    public event Action? ModsToggleRequested;
    public event Action? DeleteRequested;
    public event Action? SignInAgainRequested;
    /// <summary>The player asked to sign out, from the Steam account screen. The launcher asks before it does.</summary>
    public event Action? SignOutRequested;
    /// <summary>The player asked for the Steam Cloud's saves on this iPhone, from the cloud saves screen.</summary>
    public event Action? CloudGetRequested;
    /// <summary>The player wants the game with none on this iPhone, or carrying on with a download; false is choosing the version again.</summary>
    public event Action<bool>? DownloadRequested;

    /// <summary>Where the player is in setup, shown as the climb on the next screen; null for a launch that is past setup.</summary>
    public SetupStep? Step { get; set; }

    public LauncherView(Control root)
    {
        _root = root;
        root.Theme = LauncherTheme.Create();
        root.AddChild(new ColorRect { Color = LauncherTheme.Dusk, AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore });
        _frame = new MarginContainer { AnchorRight = 1, AnchorBottom = 1 };
        root.AddChild(_frame);
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 36);
        _frame.AddChild(page);

        _back = LauncherTheme.MakeButton("Back", LauncherTheme.QuietButton, () => BackRequested?.Invoke());
        _back.Alignment = HorizontalAlignment.Left;
        _back.CustomMinimumSize = new Vector2(220, 120);
        var header = new HBoxContainer { CustomMinimumSize = new Vector2(0, 120) };
        header.AddChild(_back);
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        var wordmark = LauncherTheme.Text(AppName, LauncherTheme.Display, LauncherTheme.WordmarkSize, LauncherTheme.Mist);
        wordmark.VerticalAlignment = VerticalAlignment.Center;
        header.AddChild(wordmark);

        _heading = LauncherTheme.Text("", LauncherTheme.Display, LauncherTheme.HeadingSize, LauncherTheme.Bone);
        _heading.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status = LauncherTheme.Text("", LauncherTheme.Body, LauncherTheme.BodySize, LauncherTheme.Bone);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.AddThemeConstantOverride("line_spacing", 10);
        _bar = new ProgressBar { MaxValue = 1, Step = 0.001, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 14) };
        _account = CreateField("Steam account name", LineEdit.VirtualKeyboardTypeEnum.Default, secret: false);
        _password = CreateField("Password", LineEdit.VirtualKeyboardTypeEnum.Password, secret: true);
        _guardCode = CreateField("Steam Guard code", LineEdit.VirtualKeyboardTypeEnum.Default, secret: false);
        _qr = new TextureRect
        {
            CustomMinimumSize = new Vector2(QrPixels, QrPixels),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest
        };
        // A scanner needs the light margin around the code, whatever the background.
        _qrFrame = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        _qrFrame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Colors.White, ContentMarginLeft = 40, ContentMarginRight = 40, ContentMarginTop = 40, ContentMarginBottom = 40,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10
        });
        _qrFrame.AddChild(_qr);
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 0);
        _spacer = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

        _play = LauncherTheme.MakeButton("Play", LauncherTheme.PrimaryButton, () => PlayRequested?.Invoke());
        _download = LauncherTheme.MakeButton("", LauncherTheme.PrimaryButton, () => DownloadRequested?.Invoke(true));
        _useAccount = LauncherTheme.MakeButton("Sign in with a Steam account", LauncherTheme.PrimaryButton, () => ShowCredentials());
        _useQr = LauncherTheme.MakeButton("Scan a QR code with Steam on another device", "Button", () => QrSignInRequested?.Invoke());
        _formSubmit = LauncherTheme.MakeButton("", LauncherTheme.PrimaryButton, () => { if (_guardCode.Visible) SubmitGuardCode(); else SubmitCredentials(); });
        _choiceFirst = LauncherTheme.MakeButton("", LauncherTheme.PrimaryButton, () => ChoiceMade?.Invoke(true));
        _choiceSecond = LauncherTheme.MakeButton("", "Button", () => ChoiceMade?.Invoke(false));
        _cloudGet = LauncherTheme.MakeButton("Get from Steam Cloud", LauncherTheme.PrimaryButton, () => CloudGetRequested?.Invoke());
        _chooseVersion = LauncherTheme.MakeButton("Choose a version", "Button", () => DownloadRequested?.Invoke(false));
        _signInAgain = LauncherTheme.MakeButton("Sign in again", "Button", () => SignInAgainRequested?.Invoke());
        _signOut = LauncherTheme.MakeButton(SignOut.ButtonLabel, "Button", () => SignOutRequested?.Invoke());
        _deleteGame = LauncherTheme.MakeButton("Delete the game", LauncherTheme.DangerButton, () => DeleteRequested?.Invoke());
        var actions = new VBoxContainer();
        actions.AddThemeConstantOverride("separation", 24);
        foreach (Button button in new[] { _play, _download, _useAccount, _useQr, _formSubmit, _choiceFirst, _choiceSecond, _cloudGet, _chooseVersion, _signInAgain, _signOut, _deleteGame })
        {
            button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            actions.AddChild(button);
        }

        _account.TextSubmitted += _ => _password.GrabFocus();
        _password.TextSubmitted += _ => SubmitCredentials();
        _guardCode.TextSubmitted += _ => SubmitGuardCode();
        // How the last thing the player started ended, edged in its colour, until they do something else.
        _notice = new PanelContainer { Visible = false };
        _noticeTitle = LauncherTheme.Text("", LauncherTheme.BodyStrong, LauncherTheme.BodySize, LauncherTheme.Moss);
        _noticeText = LauncherTheme.Text("", LauncherTheme.Body, LauncherTheme.SmallSize, LauncherTheme.Bone);
        _noticeText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var noticeLines = new VBoxContainer();
        noticeLines.AddThemeConstantOverride("separation", 8);
        noticeLines.AddChild(_noticeTitle);
        noticeLines.AddChild(_noticeText);
        _notice.AddChild(noticeLines);
        foreach (Control control in new Control[] { header, _heading, _notice, _climb.Control, _status, _bar, _account, _password, _guardCode, _qrFrame, _rows, _spacer, actions })
            page.AddChild(control);
        Control[] version = CreateVersionControls(page, actions);
        Control[] mods = CreateModControls(page, actions);
        _switchable = [_back, _heading, _bar, _account, _password, _guardCode, _qrFrame, _rows, _play, _download, _useAccount, _useQr, _formSubmit,
            _choiceFirst, _choiceSecond, _cloudGet, _chooseVersion, _signInAgain, _signOut, _deleteGame, .. version, .. mods];
        AddCloudSaves(page, actions);

        root.GetViewport().SizeChanged += FitToScreen;
        FitToScreen();
    }

    /// <summary>The first screen for where things stand (<see cref="EntryScreen"/>).</summary>
    public void ShowEntry(EntryScreen screen, bool canGoBack = false)
    {
        var visible = new List<Control>();
        switch (screen.Kind)
        {
            case EntryKind.SignIn:
                visible.AddRange([_useAccount, _useQr]);
                break;
            case EntryKind.GetGame:
                _download.Text = screen.PrimaryAction;
                visible.Add(_download);
                break;
            default:
                visible.Add(FillRows(screen.Rows));
                visible.Add(_play);
                break;
        }
        if (screen.Kind == EntryKind.GetGame && screen.OfferGameVersion) visible.Add(_chooseVersion);
        if (screen.OfferSignInAgain) visible.Add(_signInAgain);
        if (canGoBack) visible.Add(_back);
        Show(screen.Heading, screen.Status, form: false, visible.ToArray());
    }

    /// <summary>The ways to sign in. <paramref name="canGoBack"/> is for a player who already has a sign-in here and came to replace it.</summary>
    public void ShowStart(string status, bool canGoBack = false)
    {
        if (canGoBack) Show("Sign in to Steam", status, form: false, _useAccount, _useQr, _back);
        else Show("Sign in to Steam", status, form: false, _useAccount, _useQr);
    }

    /// <summary>The stored sign-in, the way to replace it, and the way to sign out.</summary>
    public void ShowAccount(string account) => Show(SignOut.Heading,
        $"Signed in as {account}. The sign-in is kept on this iPhone, encrypted with a key in its Keychain; your password is not stored.\nSigning in with another account keeps the game and the saves of each account.",
        form: false, _signInAgain, _signOut, _back);

    /// <summary>A step of setup that has a measure of how far it is. A negative <paramref name="fraction"/> means working, with no measure.</summary>
    public void ShowProgress(string status, double fraction, bool canGoBack = false)
    {
        var visible = new List<Control> { _bar };
        if (canGoBack) visible.Add(_back);
        _bar.Indeterminate = fraction < 0;
        if (fraction >= 0) _bar.Value = fraction;
        Show(StepHeading(), status, form: false, visible.ToArray());
    }

    /// <summary>
    /// Progress on the screen UIKit draws over the engine, which stays there until the app closes (<c>native/setup_overlay.mm</c>). The engine's own
    /// screen underneath says the same, for anywhere that one is not available.
    /// </summary>
    public void ShowOverEngine(string status, double fraction)
    {
        ShowProgress(status, fraction);
        SetupOverlay.Show(StepLine(), StepHeading() ?? AppName, status, (float)fraction, null);
    }

    /// <summary>The app has to be started again to go on. The button closes it, and iOS leaves opening it to the player.</summary>
    public void ShowReopen(string status)
    {
        const string heading = "Open the app again";
        SetNotice(null);
        Show(heading, status, form: false);
        SetupOverlay.Show(StepLine(), heading, status, -1, CloseLabel);
        if (_autoTap.Count == 0 || _autoTap.Peek() != CloseLabel) return;
        GD.Print("Auto tap: ", _autoTap.Dequeue());
        SetupOverlay.Press();
    }

    /// <summary>Account name and password entry, kept in the upper part of the screen clear of the keyboard.</summary>
    public void ShowCredentials(string status = "Your password is sent only to Steam, encrypted. It is not stored.")
    {
        _formSubmit.Text = "Sign in";
        Show("Sign in to Steam", status, form: true, _account, _password, _formSubmit, _back);
        _password.Text = "";
        (_account.Text.Length == 0 ? _account : _password).GrabFocus();
    }

    /// <summary>Something is in progress. <paramref name="canGoBack"/> offers a way out of a wait that depends on the player.</summary>
    public void ShowWorking(string status, bool canGoBack = false)
    {
        if (canGoBack) Show(StepHeading(), status, form: false, _back);
        else Show(StepHeading(), status, form: false);
    }

    public void ShowGuardCode(string status)
    {
        _formSubmit.Text = "Continue";
        Show("Steam Guard", status, form: true, _guardCode, _formSubmit, _back);
        _guardCode.Text = "";
        _guardCode.GrabFocus();
    }

    public void ShowQr(Image code, string status)
    {
        _qr.Texture = ImageTexture.CreateFromImage(code);
        Show("Scan with Steam", status, form: false, _qrFrame, _back);
    }

    /// <summary>
    /// A question with one or two answers, each a full-width button. The answer arrives through <see cref="ChoiceMade"/>. Without a second
    /// answer the screen is a notice to acknowledge. A first answer that deletes something is drawn as such.
    /// </summary>
    /// <param name="canGoBack">Also offers the way out of the attempt, through <see cref="BackRequested"/>.</param>
    /// <param name="heading">The heading, for a question asked outside setup; during setup the step's own.</param>
    public void ShowChoice(string question, string first, string? second = null, bool canGoBack = false, string? heading = null)
    {
        _choiceFirst.Text = first;
        _choiceFirst.ThemeTypeVariation = first.StartsWith("Delete", StringComparison.Ordinal) ? LauncherTheme.DangerButton : LauncherTheme.PrimaryButton;
        _choiceSecond.Text = second ?? "";
        var visible = new List<Control> { _choiceFirst };
        if (second != null) visible.Add(_choiceSecond);
        if (canGoBack) visible.Add(_back);
        Show(heading ?? StepHeading(), question, form: false, visible.ToArray());
    }

    /// <summary>A failure the player cannot retry inside this process.</summary>
    public void ShowFatal(string status) => Show(StepHeading(), status, form: false);

    public void Hide()
    {
        ReleaseKeyboard();
        _root.Visible = false;
    }

    public static Image RenderChallenge(string challenge)
    {
        using var data = QRCodeGenerator.GenerateQrCode(challenge, QRCodeGenerator.ECCLevel.M);
        int size = data.ModuleMatrix.Count;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                image.SetPixel(x, y, data.ModuleMatrix[y][x] ? Colors.Black : Colors.White);
        return image;
    }

    /// <param name="heading">The screen's heading, or null for none.</param>
    /// <param name="form">The actions follow the fields directly, so the keyboard does not cover them.</param>
    private void Show(string? heading, string status, bool form, params Control[] visible)
    {
        ReleaseKeyboard();
        _heading.Text = heading ?? "";
        _status.Text = status;
        foreach (Control control in _switchable) control.Visible = Array.IndexOf(visible, control) >= 0;
        SwitchCloudControls(visible);
        _heading.Visible = heading != null;
        _status.Visible = status.Length > 0;
        _spacer.Visible = !form;
        if (!_qrFrame.Visible) _qr.Texture = null;
        // Forms keep clear of the keyboard, and a QR code needs the height.
        DrawSteps(!form && !_qrFrame.Visible ? Step : null);
        var buttons = visible.OfType<Button>().ToList();
        if (_rows.Visible) buttons.InsertRange(0, _rows.GetChildren().OfType<Button>());
        if (_modScroll.Visible) buttons.AddRange(_modDeleteButtons);
        // What the player would read, for a run with nobody there. Not each tick of a measured step.
        if ((!_bar.Visible || _bar.Indeterminate) && status != _lastLogged) GD.Print($"Screen [{Step?.ToString() ?? "-"}]: {status.ReplaceLineEndings(" ")} | {string.Join(", ", buttons.Select(button => button.Text))}");
        _lastLogged = status;
        PressAutoTap(buttons);
    }

    private void DrawSteps(SetupStep? current)
    {
        _climb.Control.Visible = current != null;
        if (current != null) _climb.Show(SetupSteps.For(current.Value));
    }

    /// <summary>The heading for a screen of setup, from the step it is part of.</summary>
    private string? StepHeading() => Step switch
    {
        SetupStep.SignIn => "Sign in to Steam",
        SetupStep.Ownership => "Checking ownership",
        SetupStep.Download => "Get the game",
        SetupStep.Prepare => "Preparing the game",
        SetupStep.Effects => "Preparing effects",
        SetupStep.Reopen => "Open the app again",
        SetupStep.Play => "Starting the game",
        _ => null,
    };

    private string StepLine() => Step is { } current
        ? string.Join('\t', SetupSteps.For(current).Select(step => step.Label + "=" + (int)step.State))
        : "";

    /// <summary>The home rows, each a button named by what it is about, with how that stands drawn at its right.</summary>
    private Control FillRows(IReadOnlyList<HomeRow> rows)
    {
        foreach (Node child in _rows.GetChildren()) _rows.RemoveChild(child);
        foreach (var row in rows)
        {
            if (!_rowButtons.TryGetValue(row.Label, out var button))
            {
                string label = row.Label;
                button = LauncherTheme.MakeButton(label, LauncherTheme.RowButton, () => OpenRow(label));
                button.Alignment = HorizontalAlignment.Left;
                button.CustomMinimumSize = new Vector2(0, LauncherTheme.RowHeight);
                Button drawn = button;
                button.Draw += () => DrawRowEnd(drawn, label);
                _rowButtons[label] = button;
            }
            _rowValues[row.Label] = row.Value;
            _rows.AddChild(button);
            button.QueueRedraw();
        }
        return _rows;
    }

    /// <summary>
    /// Shows how something the player started ended, on this screen and the ones after it, or clears it with null. The launcher clears it
    /// when the player starts something else.
    /// </summary>
    public void SetNotice(Notice? notice)
    {
        _notice.Visible = notice != null;
        if (notice == null) return;
        Color edge = notice.Succeeded ? LauncherTheme.Moss : LauncherTheme.Blood;
        _notice.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = LauncherTheme.Stone, BorderColor = edge, BorderWidthLeft = 8,
            CornerRadiusTopRight = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 32, ContentMarginRight = 28, ContentMarginTop = 22, ContentMarginBottom = 24
        });
        _noticeTitle.Text = notice.Title;
        _noticeTitle.AddThemeColorOverride("font_color", edge);
        _noticeText.Text = notice.Text;
        // For a run with nobody there.
        GD.Print($"Notice ({(notice.Succeeded ? "done" : "failed")}): {notice.Title}: {notice.Text.ReplaceLineEndings(" ")}");
    }

    private void OpenRow(string label)
    {
        SetNotice(null);
        switch (label)
        {
            case EntryScreen.ModsRow: ModsRequested?.Invoke(); break;
            case EntryScreen.GameVersionRow: ManageRequested?.Invoke(); break;
            case EntryScreen.CloudSavesRow: ShowCloudSaves(); break;
            case EntryScreen.AccountRow: ShowAccount(_rowValues.GetValueOrDefault(label, "")); break;
        }
    }

    /// <summary>The row's value and a chevron, right-aligned, over the button's own drawing.</summary>
    private void DrawRowEnd(Button button, string label)
    {
        const float chevron = 16, chevronRight = 18;
        Vector2 size = button.Size;
        float middle = size.Y / 2;
        float tipX = size.X - chevronRight;
        button.DrawPolyline([new Vector2(tipX - chevron * 0.6f, middle - chevron), new Vector2(tipX, middle), new Vector2(tipX - chevron * 0.6f, middle + chevron)],
            LauncherTheme.Mist, 4, antialiased: true);
        string value = _rowValues.GetValueOrDefault(label, "");
        if (value.Length == 0) return;
        Font font = LauncherTheme.Figures;
        float right = tipX - chevron - 28, width = size.X * 0.55f;
        float baseline = middle + font.GetAscent(LauncherTheme.SmallSize) / 2 - font.GetDescent(LauncherTheme.SmallSize) / 2;
        button.DrawString(font, new Vector2(right - width, baseline), value, HorizontalAlignment.Right, width, LauncherTheme.SmallSize, LauncherTheme.Mist);
    }

    /// <summary>
    /// Keeps the column inside the screen's safe area (the notch, the island, the home indicator) and no wider than reads well. The safe
    /// area comes in the window's pixels, the layout in canvas units.
    /// </summary>
    private void FitToScreen()
    {
        Vector2 canvas = _root.GetViewportRect().Size;
        Vector2I window = DisplayServer.WindowGetSize();
        Rect2I safe = DisplayServer.GetDisplaySafeArea();
        float scale = window.X > 0 ? canvas.X / window.X : 1;
        bool known = safe.Size.X > 0 && safe.Size.Y > 0 && window.X > 0;
        float top = known ? safe.Position.Y * scale : 0, bottom = known ? (window.Y - safe.End.Y) * scale : 0;
        float left = known ? safe.Position.X * scale : 0, right = known ? (window.X - safe.End.X) * scale : 0;
        float side = Math.Max(LauncherTheme.SidePadding, (canvas.X - ContentWidth) / 2);
        _frame.AddThemeConstantOverride("margin_left", (int)(Math.Max(left, 0) + side));
        _frame.AddThemeConstantOverride("margin_right", (int)(Math.Max(right, 0) + side));
        _frame.AddThemeConstantOverride("margin_top", (int)(Math.Max(top, 0) + 24));
        _frame.AddThemeConstantOverride("margin_bottom", (int)(Math.Max(bottom, 0) + 48));
    }

    private void PressAutoTap(List<Button> visible)
    {
        if (_autoTap.Count == 0 || visible.FirstOrDefault(button => button.Text == _autoTap.Peek()) is not { } button) return;
        GD.Print("Auto tap: ", _autoTap.Dequeue());
        Callable.From(() => button.EmitSignal(BaseButton.SignalName.Pressed)).CallDeferred();
    }

    private void SubmitCredentials()
    {
        string account = _account.Text.Trim(), password = _password.Text;
        if (account.Length == 0 || password.Length == 0) return;
        _password.Text = "";
        PasswordSignInRequested?.Invoke(account, password);
    }

    private void SubmitGuardCode()
    {
        string code = _guardCode.Text.Trim().ToUpperInvariant();
        if (code.Length == 0) return;
        _guardCode.Text = "";
        GuardCodeSubmitted?.Invoke(code);
    }

    private void ReleaseKeyboard()
    {
        foreach (LineEdit field in new[] { _account, _password, _guardCode })
            if (field.HasFocus()) field.ReleaseFocus();
    }

    private static LineEdit CreateField(string placeholder, LineEdit.VirtualKeyboardTypeEnum keyboard, bool secret) => new()
    {
        PlaceholderText = placeholder,
        Secret = secret,
        VirtualKeyboardType = keyboard,
        CustomMinimumSize = new Vector2(0, LauncherTheme.TouchHeight),
        ContextMenuEnabled = false
    };
}
