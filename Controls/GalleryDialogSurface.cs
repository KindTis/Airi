using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
namespace Airi.Controls;

/// <summary>A read-only tooltip surface in the Popup HWND, with an addressable UIA peer.</summary>
public sealed class GalleryDialogSurface : Border
{
    public static readonly DependencyProperty IsDialogOpenProperty = DependencyProperty.Register(nameof(IsDialogOpen), typeof(bool), typeof(GalleryDialogSurface), new PropertyMetadata(false, (d,e) =>
    {
        if (UIElementAutomationPeer.FromElement((UIElement)d) is GalleryDialogSurfacePeer peer)
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, (bool)e.OldValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed, (bool)e.NewValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }));
    public bool IsDialogOpen { get => (bool)GetValue(IsDialogOpenProperty); set => SetValue(IsDialogOpenProperty, value); }
    public Action? CloseRequested { get; set; }
    protected override AutomationPeer OnCreateAutomationPeer() => new GalleryDialogSurfacePeer(this);
}
public sealed class GalleryDialogSurfacePeer(GalleryDialogSurface owner) : FrameworkElementAutomationPeer(owner), IExpandCollapseProvider
{
    protected override string GetClassNameCore() => nameof(GalleryDialogSurface);
    protected override string GetLocalizedControlTypeCore() => "도움말";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ToolTip;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
    public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.ExpandCollapse ? this : base.GetPattern(pattern);
    public ExpandCollapseState ExpandCollapseState => owner.IsDialogOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
    public void Collapse() => owner.Dispatcher.Invoke(() => owner.CloseRequested?.Invoke());
    public void Expand() { if (!owner.IsDialogOpen) throw new InvalidOperationException("영상 카드에서 상세를 열어 주세요."); }
}
