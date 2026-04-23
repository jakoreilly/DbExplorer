using System.Text.RegularExpressions;

namespace DbExplorer.Core.Validation;

/// <summary>
/// Provides safe quoting and static format validation for SQL Server identifiers.
/// Actual catalog existence checks are done by IIdentifierValidator.
/// </summary>
public static class SqlIdentifierHelper
{
    // SQL Server max identifier length is 128 chars.
    private static readonly Regex ValidPattern = new(
        @"^[A-Za-z_#@][A-Za-z0-9_#@$]*$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Returns a safely bracket-quoted identifier, e.g. [MySchema]
    /// </summary>
    public static string Quote(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Identifier must not be empty.", nameof(identifier));
        if (identifier.Length > 128)
            throw new ArgumentException("Identifier exceeds maximum length of 128.", nameof(identifier));
        // Escape embedded brackets per T-SQL rules
        return "[" + identifier.Replace("]", "]]") + "]";
    }

    /// <summary>
    /// Performs a quick static syntax check on an identifier.
    /// NOTE: this does NOT validate against the live catalog; use IIdentifierValidator for that.
    /// </summary>
    public static bool IsValidIdentifierFormat(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 128)
            return false;
        // Allow quoted identifiers that have been stripped of brackets
        return ValidPattern.IsMatch(identifier);
    }

    /// <summary>
    /// Throws if the format is invalid.
    /// </summary>
    public static void ThrowIfInvalidFormat(string identifier, string paramName)
    {
        if (!IsValidIdentifierFormat(identifier))
            throw new ArgumentException(
                $"'{identifier}' is not a valid SQL Server identifier format.", paramName);
    }
}
