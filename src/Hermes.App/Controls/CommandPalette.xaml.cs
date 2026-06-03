using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Hermes.App.Controls;

/// <summary>
/// Command palette popup. Renders static commands instantly on open and
/// merges dynamic (gateway-backed) commands as they arrive. Dismissal is
/// handled by <see cref="Microsoft.UI.Xaml.Controls.Primitives.Popup"/>'s
/// own <c>IsLightDismissEnabled</c> — Esc and outside-click both close.
///
/// <para>The host (MainWindow) constructs one of these and calls
/// <see cref="Open"/> on Ctrl+K. The popup keeps a single
/// <see cref="CommandRegistry"/> reference passed in via
/// <see cref="Initialize"/>; the registry is built fresh per open against
/// the live HermesApiClient / ChatViewModel.</para>
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private CommandRegistry? _registry;
    private readonly List<PaletteCommand> _allItems = new();
    private readonly ObservableCollection<PaletteCommand> _filtered = new();
    private CancellationTokenSource? _loadCts;

    /// <summary>
    /// Generation counter — incremented on every Open() call. The async
    /// dynamic-load worker checks this against its captured generation
    /// before applying results, so a stale build from a previous open
    /// can't pollute the current palette.
    /// </summary>
    private int _generation;

    /// <summary>Invoked AFTER the popup has hidden, so the action can
    /// open its own dialogs without colliding with the popup.</summary>
    public event EventHandler<PaletteCommand>? CommandInvoked;

    public CommandPalette()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _filtered;
    }

    /// <summary>Wire up the registry once after construction. Pulled out of
    /// the ctor because MainWindow builds the registry's
    /// <see cref="CommandRegistry.PaletteHost"/> from its own instance
    /// methods, and the palette is field-instantiated by the XAML loader.</summary>
    public void Initialize(CommandRegistry registry)
    {
        _registry = registry;
    }

    public bool IsOpen => PaletteRoot.IsOpen;

    /// <summary>
    /// Opens the palette, anchored centered over the supplied parent
    /// (typically the MainWindow's root Grid). Rebuilds the command list
    /// from scratch — static rows render immediately, dynamic rows merge
    /// in as the async load completes.
    /// </summary>
    public void Open(FrameworkElement positioningRoot)
    {
        if (_registry is null)
        {
            throw new InvalidOperationException(
                "CommandPalette.Open called before Initialize — host must wire the registry first.");
        }

        // Bump the generation so any in-flight load from a previous open
        // discards its results when it returns.
        _generation++;
        var myGeneration = _generation;

        // Position centered horizontally, ~120px from the top — feels
        // closer to the title bar than dead-center and matches the
        // visual rhythm of VS Code / Spotlight.
        PaletteRoot.XamlRoot = positioningRoot.XamlRoot;
        var available = positioningRoot.ActualSize;
        PaletteRoot.HorizontalOffset = Math.Max(0, (available.X - PaletteShell.Width) / 2);
        PaletteRoot.VerticalOffset = 120;

        // Reset state from any prior open.
        SearchBox.Text = "";
        _allItems.Clear();
        _filtered.Clear();
        EmptyState.Visibility = Visibility.Collapsed;

        // Static commands render immediately — no awaits, no gateway hop.
        _allItems.AddRange(_registry.GetStatic());
        ApplyFilter();

        PaletteRoot.IsOpen = true;

        // Defer focus + dynamic load one dispatcher tick so the popup has
        // realized its visual tree. The TextBox.Focus call returns false
        // if invoked before the popup's content is loaded.
        DispatcherQueue.TryEnqueue(() =>
        {
            SearchBox.Focus(FocusState.Programmatic);
            _ = LoadDynamicAsync(myGeneration);
        });
    }

    public void Close() => PaletteRoot.IsOpen = false;

    private async Task LoadDynamicAsync(int generation)
    {
        if (_registry is null) return;

        // Replace any prior in-flight CTS so a rapid Esc-then-Ctrl+K
        // doesn't leak the previous load (and doesn't pollute this one).
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _loadCts = cts;

        List<PaletteCommand> dynamicCmds;
        try
        {
            dynamicCmds = await _registry.LoadDynamicAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // CommandRegistry already swallows per-fetch errors. If a
            // bigger surprise leaked out, drop it — the palette stays
            // usable with whatever static commands it has.
            return;
        }

        // Generation guard — palette may have been re-opened, in which
        // case this load belongs to the previous opening.
        if (generation != _generation || !PaletteRoot.IsOpen) return;

        _allItems.AddRange(dynamicCmds);
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    /// <summary>
    /// Re-runs the fuzzy match against <see cref="_allItems"/> and updates
    /// <see cref="_filtered"/>. Selects the top row so Enter is always
    /// meaningful as long as there's at least one match.
    /// </summary>
    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";

        // No query: show everything in registry-rank order so the user
        // sees the static commands first.
        IEnumerable<PaletteCommand> ranked;
        if (query.Length == 0)
        {
            ranked = _allItems.OrderBy(c => c.Rank);
        }
        else
        {
            var scored = new List<PaletteCommand>(_allItems.Count);
            foreach (var item in _allItems)
            {
                var score = CommandRegistry.Score(item, query);
                if (score is null) continue;
                item.LastScore = score.Value;
                scored.Add(item);
            }
            // Higher score wins; rank breaks ties (lower wins).
            ranked = scored.OrderByDescending(c => c.LastScore).ThenBy(c => c.Rank);
        }

        _filtered.Clear();
        // Cap at 50 rows so a huge dynamic list doesn't drown the static
        // commands. With <= 30 rows visible in the dropdown the user
        // gets to the items they want via typing, not scrolling.
        foreach (var item in ranked.Take(50))
        {
            _filtered.Add(item);
        }

        if (_filtered.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
            EmptyState.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Only show the empty-state when the user has actually typed
            // something. With no query and no items the palette is just
            // mid-load — leave the area blank to avoid flicker.
            EmptyState.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Routes keys from the search box: Down/Up move the ListView
    /// selection without losing TextBox focus; Enter invokes the
    /// currently-selected command. Esc is handled by the Popup itself
    /// via IsLightDismissEnabled.
    /// </summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_filtered.Count == 0) return;

        switch (e.Key)
        {
            case VirtualKey.Down:
                ResultsList.SelectedIndex = Math.Min(_filtered.Count - 1, ResultsList.SelectedIndex + 1);
                ScrollSelectedIntoView();
                e.Handled = true;
                break;
            case VirtualKey.Up:
                ResultsList.SelectedIndex = Math.Max(0, ResultsList.SelectedIndex - 1);
                ScrollSelectedIntoView();
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if (ResultsList.SelectedItem is PaletteCommand cmd)
                {
                    InvokeAndClose(cmd);
                    e.Handled = true;
                }
                break;
        }
    }

    private void ScrollSelectedIntoView()
    {
        if (ResultsList.SelectedItem is { } sel)
        {
            ResultsList.ScrollIntoView(sel);
        }
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PaletteCommand cmd)
        {
            InvokeAndClose(cmd);
        }
    }

    /// <summary>
    /// Hides the popup FIRST, then raises <see cref="CommandInvoked"/>.
    /// Ordering matters — the invoked action may itself open a
    /// ContentDialog (e.g. NewChat → stream cancel could surface an
    /// error dialog), and we want the popup out of the way first so the
    /// dialog isn't visually layered under it.
    /// </summary>
    private void InvokeAndClose(PaletteCommand cmd)
    {
        PaletteRoot.IsOpen = false;
        CommandInvoked?.Invoke(this, cmd);
    }

    private void PaletteRoot_Closed(object? sender, object e)
    {
        // Cancel any in-flight dynamic load — user has dismissed the
        // palette and doesn't care about results anymore.
        _loadCts?.Cancel();
    }
}
