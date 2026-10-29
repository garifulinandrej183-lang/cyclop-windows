using Cyclop.Core.Panel;
using Cyclop.Core.Settings;

namespace Cyclop.Tests;

public class PanelTests
{
    [Fact] public void EditingAndMenusPreventDismissal()
    {
        var panel = new PanelState(); panel.Open(); panel.Edit(true); panel.PointerLeft();
        Assert.Equal(PanelMode.TextInput, panel.Mode);
        panel.Edit(false); panel.ContextMenuOpen = true; panel.PointerLeft();
        Assert.Equal(PanelMode.Expanded, panel.Mode);
        panel.ContextMenuOpen = false; panel.PointerLeft(); Assert.Equal(PanelMode.Collapsed, panel.Mode);
    }
    [Fact] public void DropSelectsShelfAndHoldsUntilFinished()
    {
        var panel = new PanelState(); panel.Select(PanelTab.Translate); panel.BeginDrop(); panel.PointerLeft();
        Assert.Equal(PanelTab.Shelf, panel.Tab); Assert.Equal(PanelMode.ReceivingFiles, panel.Mode);
        panel.EndDrop(); panel.PointerLeft(); Assert.Equal(PanelMode.Collapsed, panel.Mode);
    }
    [Fact] public void HiddenPanelIgnoresOpenAndDrop()
    {
        var panel = new PanelState(); panel.Hide(); panel.Open(); panel.BeginDrop(); panel.Edit(true);
        Assert.Equal(PanelMode.Hidden, panel.Mode); panel.Unhide(); Assert.Equal(PanelMode.Collapsed, panel.Mode);
    }
    [Fact] public void EscapeReleasesEditingAndContextHold()
    {
        var panel = new PanelState(); panel.Open(); panel.Edit(true); panel.ContextMenuOpen = true; panel.Escape();
        Assert.Equal(PanelMode.Collapsed, panel.Mode); Assert.False(panel.ContextMenuOpen);
    }
    [Fact] public void SettingsAreOptInAndBounded()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json");
        try { var settings = new AppSettings { HistoryLimit = int.MaxValue }; settings.Save(path);
            var loaded = AppSettings.Load(path); Assert.Equal(500, loaded.HistoryLimit);
            Assert.False(loaded.CaptureClipboard); Assert.False(loaded.StartAtLogin); Assert.False(loaded.AutoSaveImages); }
        finally { File.Delete(path); }
    }
}
