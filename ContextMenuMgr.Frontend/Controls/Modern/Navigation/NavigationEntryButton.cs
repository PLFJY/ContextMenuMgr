using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace ContextMenuMgr.Frontend.Controls.Modern.Navigation;

/// <summary>Exposes the existing navigation branch action to assistive technology.</summary>
public sealed class NavigationEntryButton : Button
{
    public static readonly DependencyProperty IsExpandableProperty = DependencyProperty.Register(
        nameof(IsExpandable), typeof(bool), typeof(NavigationEntryButton), new PropertyMetadata(false));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(NavigationEntryButton),
        new PropertyMetadata(false, OnExpandedChanged));

    public bool IsExpandable { get => (bool)GetValue(IsExpandableProperty); set => SetValue(IsExpandableProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    protected override AutomationPeer OnCreateAutomationPeer() => new NavigationEntryButtonAutomationPeer(this);

    private static void OnExpandedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (UIElementAutomationPeer.FromElement((UIElement)sender) is NavigationEntryButtonAutomationPeer peer)
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                (bool)e.OldValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed,
                (bool)e.NewValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }

    private sealed class NavigationEntryButtonAutomationPeer(NavigationEntryButton owner)
        : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public override object GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.ExpandCollapse && owner.IsExpandable
                ? this : base.GetPattern(patternInterface);

        public ExpandCollapseState ExpandCollapseState => !owner.IsExpandable
            ? ExpandCollapseState.LeafNode
            : owner.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public void Expand() => SetExpanded(true);
        public void Collapse() => SetExpanded(false);

        private void SetExpanded(bool expanded)
        {
            owner.Dispatcher.Invoke(() =>
            {
                if (!owner.IsEnabled) throw new ElementNotEnabledException();
                if (owner.IsExpandable && owner.IsExpanded != expanded)
                    ((IInvokeProvider)GetPattern(PatternInterface.Invoke)).Invoke();
            });
        }
    }
}
