// <copyright file="XrefQuery.Members.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

#nullable disable

using System;
using System.Collections.Generic;

namespace D365FO.Core.Xref
{
    /// <summary>
    /// Where a member of an object lives in <c>DYNAMICSXREFDB</c>. The compiler writes a target in
    /// one of two shapes — code references as <c>/Tables/T/Fields/f</c>, metadata references as
    /// <c>Table/T/TableFieldString/f</c> — and what an extension adds under the extension only.
    /// Asking for the code shape alone answered "no callers" for members with thousands of
    /// references. Every shape here was read off a live database.
    /// </summary>
    /// <remarks>Compiled into <c>D365FO.Bridge</c> too: see <see cref="XrefQuery"/>.</remarks>
    public static partial class XrefQuery
    {
        /// <summary>The metadata root names a code container is stored under.</summary>
        private static readonly Dictionary<string, string[]> MetadataTypes = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "Tables", new[] { "Table" } },
            { "Classes", new[] { "Class" } },
            { "Forms", new[] { "Form" } },
            { "Views", new[] { "View" } },
            { "DataEntityViews", new[] { "DataEntityView" } },
            { "Queries", new[] { "QuerySimple", "QueryComposite" } },
            { "Maps", new[] { "Map" } },
        };

        /// <summary>
        /// The segment a field is stored under in the metadata shape: it names the field's type, so
        /// there is one per type. Listed, not matched with <c>TableField%</c> — <c>%</c> crosses
        /// <c>/</c> and would pull in the field group of the same name.
        /// </summary>
        private static readonly Dictionary<string, string[]> MetadataFieldSegments = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            {
                "Tables", new[]
                {
                    "TableFieldString", "TableFieldEnum", "TableFieldReal", "TableFieldInt64", "TableFieldInt",
                    "TableFieldDate", "TableFieldUtcDateTime", "TableFieldGuid", "TableFieldContainer", "TableFieldTime",
                }
            },
            {
                "Maps", new[]
                {
                    "MapFieldString", "MapFieldEnum", "MapFieldReal", "MapFieldInt64", "MapFieldInt",
                    "MapFieldDate", "MapFieldUtcDateTime", "MapFieldGuid", "MapFieldContainer", "MapFieldTime",
                }
            },
            {
                "Views", new[]
                {
                    "ViewFieldBound", "ViewFieldComputedString", "ViewFieldComputedEnum", "ViewFieldComputedReal",
                    "ViewFieldComputedInt64", "ViewFieldComputedInt", "ViewFieldComputedDate", "ViewFieldComputedUtcDateTime",
                }
            },
            {
                "DataEntityViews", new[]
                {
                    "DataEntityViewMappedField", "DataEntityViewUnmappedFieldString", "DataEntityViewUnmappedFieldEnum",
                    "DataEntityViewUnmappedFieldReal", "DataEntityViewUnmappedFieldInt64", "DataEntityViewUnmappedFieldInt",
                    "DataEntityViewUnmappedFieldDate", "DataEntityViewUnmappedFieldUtcDateTime",
                    "DataEntityViewUnmappedFieldGuid", "DataEntityViewUnmappedFieldContainer",
                }
            },
        };

        /// <summary>The member segments of a four-segment code path.</summary>
        private static readonly string[] MemberSegments = { "Methods", "Fields", "EnumValues", "TableIndexs", "TableFieldGroups" };

        /// <summary>
        /// The candidate paths of <c>Owner.member</c>, <c>Enum.Value</c> or
        /// <c>Form.DataSource.method</c>, in both shapes.
        /// </summary>
        /// <param name="owner">The declaring object, or the form.</param>
        /// <param name="member">The member, or <c>DataSource.method</c>.</param>
        /// <param name="extensionClasses">
        /// <c>[ExtensionOf]</c> classes that declare the member: a method an extension class adds is
        /// recorded under that class only.
        /// </param>
        private static TargetPaths MemberPaths(string owner, string member, IList<string> extensionClasses)
        {
            var exact = new List<string>();
            var like = new List<string>();

            var parts = member.Split('.');
            if (parts.Length == 2)
            {
                // A form data source's method. Every data source is stored at the root under its
                // own name, joined ones included.
                AddCodePath("/Forms/" + owner + "/DataSources/" + parts[0] + "/Methods/" + parts[1], exact, like);
                return new TargetPaths(exact, like, true);
            }

            foreach (var container in MetadataTypes.Keys)
            {
                AddCodePath("/" + container + "/" + owner + "/Methods/" + member, exact, like);
                AddCodePath("/" + container + "/" + owner + "/Fields/" + member, exact, like);
            }

            AddCodePath("/Enums/" + owner + "/EnumValues/" + member, exact, like);

            if (extensionClasses != null)
                foreach (var extension in extensionClasses)
                    if (!string.IsNullOrWhiteSpace(extension))
                        AddCodePath("/Classes/" + extension + "/Methods/" + member, exact, like);

            return new TargetPaths(exact, like, true);
        }

        /// <summary>
        /// An index or field group named <paramref name="member"/>. Asked only when no field,
        /// method or enum value has the name: many tables name a field group like a field.
        /// </summary>
        internal static TargetPaths IndexAndGroupPaths(string owner, string member)
        {
            var exact = new List<string>();
            var like = new List<string>();
            foreach (var container in new[] { "Tables", "Maps" })
                AddCodePath("/" + container + "/" + owner + "/TableIndexs/" + member, exact, like);
            foreach (var container in new[] { "Tables", "Views", "DataEntityViews", "Maps" })
                AddCodePath("/" + container + "/" + owner + "/TableFieldGroups/" + member, exact, like);
            return new TargetPaths(exact, like, true);
        }

        /// <summary>
        /// Add a code-shaped path with its metadata twins and, for a table field or an enum value,
        /// the patterns for the extension that may add it. An extension is always named
        /// <c>&lt;base&gt;.&lt;suffix&gt;</c>, so only the suffix is a wildcard.
        /// </summary>
        private static void AddCodePath(string path, List<string> exact, List<string> like)
        {
            if (!exact.Contains(path)) exact.Add(path);
            var p = path.Trim('/').Split('/');

            if (p.Length == 6 && p[0] == "Forms" && p[2] == "DataSources" && p[4] == "Methods")
            {
                exact.Add("Form/" + p[1] + "/FormDataSourceRoot/" + p[3] + "/Method/" + p[5]);
                return;
            }

            if (p.Length == 4 && p[0] == "Enums" && p[2] == "EnumValues")
            {
                exact.Add("Enum/" + p[1] + "/EnumValue/" + p[3]);
                like.Add("/EnumExtensions/" + EscapeLike(p[1]) + ".%/EnumValues/" + EscapeLike(p[3]));
                like.Add("EnumExtension/" + EscapeLike(p[1]) + ".%/EnumValue/" + EscapeLike(p[3]));
                return;
            }

            string[] types;
            if (p.Length != 4 || !MetadataTypes.TryGetValue(p[0], out types)) return;

            var singular = p[2] == "Methods" ? "Method"
                : p[2] == "TableIndexs" ? "TableIndex"
                : p[2] == "TableFieldGroups" ? "TableFieldGroup"
                : null;
            if (singular != null)
            {
                foreach (var type in types) exact.Add(type + "/" + p[1] + "/" + singular + "/" + p[3]);
                return;
            }

            string[] segments;
            if (p[2] != "Fields" || !MetadataFieldSegments.TryGetValue(p[0], out segments)) return;
            foreach (var type in types)
                foreach (var segment in segments)
                    exact.Add(type + "/" + p[1] + "/" + segment + "/" + p[3]);

            if (p[0] != "Tables") return;
            like.Add("/TableExtensions/" + EscapeLike(p[1]) + ".%/Fields/" + EscapeLike(p[3]));
            foreach (var segment in segments)
                like.Add("TableExtension/" + EscapeLike(p[1]) + ".%/" + segment + "/" + EscapeLike(p[3]));
        }

        /// <summary>Whether a code-shaped AOT path names one member rather than an object.</summary>
        private static bool IsMemberPath(string path)
        {
            var p = path.Trim('/').Split('/');
            if (p.Length == 4) return Array.IndexOf(MemberSegments, p[2]) >= 0;
            return p.Length == 6 && p[4] == "Methods";
        }
    }
}
