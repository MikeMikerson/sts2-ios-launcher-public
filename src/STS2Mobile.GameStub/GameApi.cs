// The members of the game that STS2Mobile.GameHost and STS2Mobile.GameAdapters use, declared with no implementation.
// This is the whole of what the app knows about the game's code at build time, and it never ships. Add only what those two
// projects need. `MonoBuildTool check-game-references` confirms every reference against the real assembly.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace MegaCrit.Sts2.addons.mega_text
{
    public class MegaLabel : Label
    {
        public void SetTextAutoSize(string text) => throw null;
    }

    public class MegaRichTextLabel : RichTextLabel
    {
    }
}

namespace MegaCrit.Sts2.Core.Assets
{
    public class TpSheetData
    {
        public List<TpSheetTexture> Textures { get; set; }
    }

    public class TpSheetTexture
    {
        public string Image { get; set; }
        public TpSheetSize Size { get; set; }
        public List<TpSheetSprite> Sprites { get; set; }
    }

    public class TpSheetSprite
    {
        public string Filename { get; set; }
        public TpSheetRect Region { get; set; }
        public TpSheetRect Margin { get; set; }
    }

    public class TpSheetSize
    {
        public int W { get; set; }
        public int H { get; set; }
    }

    public class TpSheetRect
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
    }
}

namespace MegaCrit.Sts2.Core.Localization
{
    public class LocString
    {
        public LocString(string locTable, string locEntryKey) => throw null;
    }
}

namespace MegaCrit.Sts2.Core.Nodes
{
    public class NGame : Control
    {
        public MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu MainMenu => throw null;
        public Task GameStartupComplete => throw null;
    }
}

namespace MegaCrit.Sts2.Core.Nodes.CommonUi
{
    public class NModalContainer : Control
    {
        public static NModalContainer Instance => throw null;
        public void Add(Node modalToCreate, bool showBackstop = true) => throw null;
    }

    public class NVerticalPopup : Control
    {
        public void SetText(string title, string body) => throw null;
    }
}

namespace MegaCrit.Sts2.Core.Nodes.GodotExtensions
{
    public class NClickableControl : Control
    {
    }

    public class NButton : NClickableControl
    {
    }
}

namespace MegaCrit.Sts2.Core.Nodes.Multiplayer
{
    public class NGenericPopup : Control
    {
        public static NGenericPopup Create() => throw null;

        public Task<bool> WaitForConfirmation(
            MegaCrit.Sts2.Core.Localization.LocString body,
            MegaCrit.Sts2.Core.Localization.LocString header,
            MegaCrit.Sts2.Core.Localization.LocString noButton,
            MegaCrit.Sts2.Core.Localization.LocString yesButton) => throw null;
    }
}

namespace MegaCrit.Sts2.Core.Nodes.Screens.MainMenu
{
    public class NMainMenu : Control
    {
    }

    public class NSubmenu : Control
    {
    }
}

namespace MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen
{
    public class NModdingScreen : MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu
    {
    }
}

namespace MegaCrit.Sts2.Core.Nodes.Screens.Settings
{
    public interface IResettableSettingNode
    {
        void SetFromSettings();
    }

    public class NSettingsScreen : MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu
    {
    }

    public class NSettingsPanel : Control
    {
        public VBoxContainer Content => throw null;

        public new class MethodName : Control.MethodName
        {
            public static readonly StringName RefreshSize;
            public static readonly StringName UpdateNavigation;
        }
    }

    // The nested classes are what Godot's source generators derive from in a subclass.
    public class NPaginator : Control
    {
        protected MegaCrit.Sts2.addons.mega_text.MegaLabel _label;
        protected List<string> _options;
        protected int _currentIndex;

        protected void ConnectSignals() => throw null;
        protected virtual void OnIndexChanged(int index) => throw null;

        public new class MethodName : Control.MethodName
        {
        }

        public new class PropertyName : Control.PropertyName
        {
        }

        public new class SignalName : Control.SignalName
        {
        }
    }
}

namespace MegaCrit.Sts2.Core.Saves
{
    public class SettingsSave
    {
        public int FpsLimit { get; set; }
        public int Msaa { get; set; }
    }

    public class SaveManager
    {
        public static SaveManager Instance => throw null;
        public SettingsSave SettingsSave => throw null;
        public bool IsProfileInitialized => throw null;
        public void SaveSettings() => throw null;
        public void SaveProfile() => throw null;
        public void SaveProgressFile() => throw null;
        public void SavePrefsFile() => throw null;
    }
}
