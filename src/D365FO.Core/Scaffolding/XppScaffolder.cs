using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Generates AOT-shaped XML for new D365FO objects. Outputs are intentionally
/// minimal; the point is to scaffold a compile-safe skeleton that Visual
/// Studio / the workspace tooling can pick up. All generators return the XML
/// as <see cref="XDocument"/> so the caller can validate, format, or round-trip
/// before writing to disk (see <see cref="ScaffoldFileWriter"/>).
/// </summary>
public static class XppScaffolder
{
    public static XDocument Table(string name, string? label = null, IEnumerable<TableFieldSpec>? fields = null)
    {
        var fieldEls = (fields ?? Enumerable.Empty<TableFieldSpec>()).Select(f =>
        {
            var el = new XElement("AxTableField",
                new XElement("Name", f.Name),
                new XElement("ExtendedDataType", f.Edt ?? "Name"));
            if (!string.IsNullOrEmpty(f.Label)) el.Add(new XElement("Label", f.Label));
            if (f.Mandatory) el.Add(new XElement("Mandatory", "Yes"));
            return el;
        });

        return new XDocument(
            new XElement("AxTable",
                new XElement("Name", name),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                new XElement("Fields", fieldEls),
                new XElement("FieldGroups",
                    new XElement("AxTableFieldGroup",
                        new XElement("Name", "AutoReport")))));
    }

    public static XDocument Class(string name, string? extends = null, bool isFinal = true)
    {
        var decl = isFinal ? "public final class" : "public class";
        var extendsClause = string.IsNullOrEmpty(extends) ? string.Empty : $" extends {extends}";
        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", name),
                extends is null ? null : new XElement("Extends", extends),
                new XElement("SourceCode",
                    new XElement("Declaration",
                        $"{decl} {name}{extendsClause}\n{{\n}}"))));
    }

    public static XDocument CocExtension(string targetClass, params string[] wrappedMethods)
    {
        var name = targetClass + "_Extension";
        var methodEls = wrappedMethods.Select(m => new XElement("Method",
            new XElement("Name", m),
            new XElement("Source",
                $"public void {m}()\n{{\n    next {m}();\n    // extension logic here\n}}\n")));

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", name),
                new XElement("SourceCode",
                    new XElement("Declaration",
                        $"[ExtensionOf(classStr({targetClass}))]\nfinal class {name}\n{{\n}}")),
                new XElement("Methods", methodEls)));
    }

    public static XDocument SimpleList(string formName, string dataSourceTable)
    {
        return new XDocument(
            new XElement("AxForm",
                new XElement("Name", formName),
                new XElement("DataSources",
                    new XElement("AxFormDataSource",
                        new XElement("Name", dataSourceTable),
                        new XElement("Table", dataSourceTable))),
                new XElement("Design",
                    new XElement("Pattern", "SimpleList"),
                    new XElement("PatternVersion", "1.0"))));
    }
}

public sealed record TableFieldSpec(string Name, string? Edt, string? Label, bool Mandatory);

/// <summary>
/// Writes a scaffolded XML document atomically: a .tmp sibling is written and
/// then moved onto the target path. Any pre-existing file is kept as .bak
/// unless the <c>overwrite</c> flag is false (in which case the operation
/// fails before touching disk).
/// </summary>
public static class ScaffoldFileWriter
{
    public sealed record WriteResult(string Path, long Bytes, string? BackupPath);

    public static WriteResult Write(XDocument doc, string path, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string? backup = null;
        if (File.Exists(full))
        {
            if (!overwrite)
                throw new IOException($"Target exists; pass --overwrite to replace: {full}");
            backup = full + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(full, backup);
        }

        var tmp = full + ".tmp";
        using (var fs = File.Create(tmp))
        {
            doc.Save(fs);
        }
        File.Move(tmp, full);
        var bytes = new FileInfo(full).Length;
        return new WriteResult(full, bytes, backup);
    }
}
