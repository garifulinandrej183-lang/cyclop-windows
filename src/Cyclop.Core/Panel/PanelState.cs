namespace Cyclop.Core.Panel;

public enum PanelMode { Collapsed, Expanded, TextInput, ReceivingFiles, Hidden }
public enum PanelTab { Spotify, Shelf, Clipboard, Translate, Settings }

/// <summary>Windows adaptation of upstream Notch/PanelState.swift. No UI or timers.</summary>
public sealed class PanelState
{
    public PanelMode Mode { get; private set; } = PanelMode.Collapsed;
    public PanelTab Tab { get; private set; }
    public bool ContextMenuOpen { get; set; }
    public bool IsExpanded => Mode is PanelMode.Expanded or PanelMode.TextInput or PanelMode.ReceivingFiles;
    public bool CanAutoCollapse => Mode == PanelMode.Expanded && !ContextMenuOpen;
    public event Action? Changed;

    public void Open() { if (Mode != PanelMode.Hidden) Set(PanelMode.Expanded); }
    public void Select(PanelTab tab) { Tab = tab; if (Mode != PanelMode.Hidden) Set(PanelMode.Expanded); }
    public void Edit(bool editing)
    {
        if (Mode is PanelMode.Hidden or PanelMode.ReceivingFiles) return;
        Set(editing ? PanelMode.TextInput : IsExpanded ? PanelMode.Expanded : PanelMode.Collapsed);
    }
    public void BeginDrop() { if (Mode == PanelMode.Hidden) return; Tab = PanelTab.Shelf; Set(PanelMode.ReceivingFiles); }
    public void EndDrop() { if (Mode == PanelMode.ReceivingFiles) Set(PanelMode.Expanded); }
    public void PointerLeft() { if (CanAutoCollapse) Set(PanelMode.Collapsed); }
    public void Escape() { ContextMenuOpen = false; if (Mode != PanelMode.Hidden) Set(PanelMode.Collapsed); }
    public void Hide() { ContextMenuOpen = false; Set(PanelMode.Hidden); }
    public void Unhide() { if (Mode == PanelMode.Hidden) Set(PanelMode.Collapsed); }
    private void Set(PanelMode mode) { Mode = mode; Changed?.Invoke(); }
}
