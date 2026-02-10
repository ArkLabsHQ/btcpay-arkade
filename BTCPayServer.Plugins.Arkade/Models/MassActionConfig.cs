using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Html;

namespace BTCPayServer.Plugins.Arkade.Models;

/// <summary>
/// Configuration for the mass action table partial.
/// </summary>
public class MassActionConfig
{
    /// <summary>
    /// The form action URL for mass actions.
    /// </summary>
    public required string FormAction { get; set; }

    /// <summary>
    /// The store ID (for form submission).
    /// </summary>
    public string? StoreId { get; set; }

    /// <summary>
    /// The contract script for sublist context (for form submission).
    /// </summary>
    public string? ContractScript { get; set; }

    /// <summary>
    /// The HTML ID for the table element.
    /// </summary>
    public string TableId { get; set; } = "mass-action-table";

    /// <summary>
    /// Number of columns (excluding the checkbox column) for colspan.
    /// </summary>
    public int ColumnCount { get; set; } = 5;

    /// <summary>
    /// Whether to use compact (table-sm) styling for sublists.
    /// </summary>
    public bool Compact { get; set; } = false;

    /// <summary>
    /// The list of available mass actions.
    /// </summary>
    public List<MassActionButton> Actions { get; set; } = new();

    /// <summary>
    /// The table header content (rendered inside the header tr, after checkbox th).
    /// </summary>
    public IHtmlContent? HeaderContent { get; set; }

    /// <summary>
    /// The table body content (rendered inside tbody).
    /// Each row should include the checkbox td and be wrapped in a tr.mass-action-row.
    /// </summary>
    public IHtmlContent? BodyContent { get; set; }
}

/// <summary>
/// Represents a mass action button.
/// </summary>
public class MassActionButton
{
    public MassActionButton() { }

    [SetsRequiredMembers]
    public MassActionButton(string command, string label, string? iconSymbol = null, string? confirmMessage = null)
    {
        Command = command;
        Label = label;
        IconSymbol = iconSymbol;
        ConfirmMessage = confirmMessage;
    }

    /// <summary>
    /// The command value submitted with the form.
    /// </summary>
    public required string Command { get; set; }

    /// <summary>
    /// The display label for the button.
    /// </summary>
    public required string Label { get; set; }

    /// <summary>
    /// Optional BTCPay icon symbol name.
    /// </summary>
    public string? IconSymbol { get; set; }

    /// <summary>
    /// Optional confirmation message (uses data-confirm attribute).
    /// </summary>
    public string? ConfirmMessage { get; set; }
}
