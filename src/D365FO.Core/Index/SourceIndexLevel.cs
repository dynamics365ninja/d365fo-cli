namespace D365FO.Core.Index;

/// <summary>
/// Values of <c>Models.SourceIndexed</c>: which method bodies a model's last extract put into
/// <c>MethodSourceFts</c>.
/// </summary>
/// <remarks>
/// A level rather than a flag because the index grew: form bodies joined class and table bodies
/// after the first source-indexed databases were built. A model indexed before that has its
/// classes and tables in the index and its forms only on disk, and a search has to know which of
/// the two to read for each.
/// </remarks>
public static class SourceIndexLevel
{
    /// <summary>No method bodies in the index; every search reads the model from disk.</summary>
    public const int None = 0;

    /// <summary>Class and table method bodies are indexed; form methods are read from disk.</summary>
    public const int ClassesAndTables = 1;

    /// <summary>Class, table and form method bodies are indexed. What every extract with source writes now.</summary>
    public const int WithForms = 2;
}
