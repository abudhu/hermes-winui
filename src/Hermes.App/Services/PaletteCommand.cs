using System;
using System.Threading.Tasks;

namespace Hermes.App.Services;

/// <summary>
/// One row in the command palette. <see cref="Invoke"/> is a pre-bound
/// async action that runs when the user picks this command — the palette
/// itself stays decoupled from page view-models. <see cref="Score"/> is
/// transient, written into the record by the fuzzy matcher before sort.
/// </summary>
public sealed class PaletteCommand
{
    /// <summary>Stable identifier — `app.newChat`, `nav.settings`,
    /// `session.open:&lt;sessionId&gt;`, `job.pause:&lt;jobId&gt;`. Useful
    /// for diagnostics and dedup; not surfaced to the user.</summary>
    public required string Id { get; init; }

    /// <summary>Primary line shown to the user. The thing they type to match.</summary>
    public required string Title { get; init; }

    /// <summary>Secondary line — e.g. session preview, job schedule, command
    /// group. Searched too, but at a lower weight.</summary>
    public string? Subtitle { get; init; }

    /// <summary>"Navigation" / "Chat" / "Jobs" / "Sessions". Drives the
    /// group header and tie-breaking when scores are equal.</summary>
    public required string Group { get; init; }

    /// <summary>Segoe Fluent icon glyph (codepoint). Optional — palette
    /// renders a blank slot when null.</summary>
    public string? IconGlyph { get; init; }

    /// <summary>Order hint within the static command set. Lower wins on
    /// score ties. Dynamic items (sessions, jobs) get higher numbers.</summary>
    public int Rank { get; init; } = 1000;

    /// <summary>Runs the command. Awaited by the palette host AFTER hiding
    /// the popup, so the action is free to open its own dialogs without
    /// hitting "one ContentDialog per XamlRoot".</summary>
    public required Func<Task> Invoke { get; init; }

    /// <summary>Last computed fuzzy score against the user's query. Set
    /// by <see cref="CommandRegistry.Score"/> immediately before sort;
    /// not meaningful outside a search cycle.</summary>
    public int LastScore { get; set; }
}
