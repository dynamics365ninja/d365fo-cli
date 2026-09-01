// <copyright file="Handlers.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Serialization;

namespace D365FO.Bridge
{
    /// <summary>
    /// JSON-RPC method implementations. All handlers return a <see cref="JsonNode"/>
    /// that becomes the <c>result</c> field. Handlers must not throw — the caller
    /// wraps exceptions into JSON-RPC errors.
    /// </summary>
    internal sealed class Handlers
    {
        internal JsonObject Ping()
        {
            var diag = MetadataBootstrap.Diagnostics();
            return new JsonObject
            {
                ["pong"] = true,
                ["version"] = Program.BridgeVersion,
                ["clr"] = Environment.Version.ToString(),
                ["framework"] = RuntimeInformation(),
                ["binPath"] = (string)diag["binPath"],
                ["packagesPath"] = (string)diag["packagesPath"],
                ["metadataLoaded"] = (bool)diag["loaded"],
                ["metadataError"] = (string)diag["error"],
            };
        }

        internal JsonObject ReadClass(JsonObject args) { return ReadArtifact(args, "Classes", "class"); }
        internal JsonObject ReadTable(JsonObject args) { return ReadArtifact(args, "Tables", "table"); }
        internal JsonObject ReadEdt(JsonObject args) { return ReadArtifact(args, "Edts", "edt"); }
        internal JsonObject ReadEnum(JsonObject args)
        {
            var result = ReadArtifact(args, "Enums", "enum");
            FixPositionalEnumValues(result);
            return result;
        }

        internal JsonObject ReadForm(JsonObject args) { return ReadArtifact(args, "Forms", "form"); }

        /// <summary>
        /// Read an artefact and hand back its <b>raw XmlSerializer XML</b> (not the
        /// reflection-based JSON projection <see cref="AxSerializer"/> produces) —
        /// the byte-for-byte counterpart of the <c>xml</c> blob <see cref="UpdateObject"/>
        /// accepts. Two consumers:
        /// <list type="bullet">
        /// <item><description><c>d365fo modify method</c> (issue #112) round-trips a single
        /// method body through <c>IMetadataProvider</c> without ever touching on-disk XML
        /// directly: read here, do a structured (XDocument-element) replace of one
        /// &lt;Method&gt;'s &lt;Source&gt;, write back via <see cref="UpdateObject"/>.</description></item>
        /// <item><description>The modification journal / <c>d365fo undo</c> (issue #113) calls
        /// this before a bridge update/delete to capture the exact pre-image, so undo can
        /// round-trip it straight back through <c>updateObject</c>/<c>createObject</c> — the
        /// JSON shape from <c>readClass</c>/<c>readTable</c>/... is lossy for that purpose (it
        /// is shaped for display, not for re-serialization).</description></item>
        /// </list>
        /// The XML shape is whatever this process's <see cref="XmlSerializer"/> produces for
        /// the live Ax* instance — it need not match Visual Studio's on-disk formatting because
        /// it never reaches disk itself; <see cref="MetadataBootstrap.SaveArtifact"/>
        /// re-serialises through the provider on write, same as every other write path here.
        /// </summary>

        /// <summary>Exception type and message, with the whole inner chain appended.</summary>
        private static string Detail(Exception ex)
        {
            var sb = new System.Text.StringBuilder(ex.GetType().Name + ": " + ex.Message);
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                sb.Append(" / ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
            return sb.ToString();
        }

        internal JsonObject ReadObjectXml(JsonObject args)
        {
            string kind = args != null ? (string)args["kind"] : null;
            string name = args != null ? (string)args["name"] : null;
            if (string.IsNullOrWhiteSpace(kind)) return Fail("MISSING_ARG", "kind is required");
            if (string.IsNullOrWhiteSpace(name)) return Fail("MISSING_ARG", "name is required");
            if (!MetadataBootstrap.KindToCollection.TryGetValue(kind, out var collectionName))
            {
                return Fail("INVALID_KIND", "kind must be one of: " + string.Join(", ", MetadataBootstrap.KindToCollection.Keys));
            }

            if (!MetadataBootstrap.TryInitialize())
            {
                return Fail("METADATA_UNAVAILABLE",
                    MetadataBootstrap.LastError ??
                    "IMetadataProvider failed to initialise; set D365FO_PACKAGES_PATH on a D365FO VM.");
            }

            object artifact;
            try
            {
                artifact = MetadataBootstrap.ReadArtifact(collectionName, name);
            }
            catch (Exception ex)
            {
                return Fail("READ_FAILED", ex.GetType().Name + ": " + ex.Message);
            }

            if (artifact == null)
            {
                return Fail("NOT_FOUND", kind + " '" + name + "' was not returned by IMetadataProvider.");
            }

            string xml;
            try
            {
                // DataContractSerializer, not XmlSerializer — the same reason validateArtifact
                // gives below, and the reason this path was BROKEN for the most-used kinds.
                // XmlSerializer reflects a type eagerly and refuses anything implementing
                // IEnumerable without Add(object); Microsoft's own AccessGrant does exactly
                // that, so reading ANY artifact transitively referencing it — AxTable,
                // AxSecurityPrivilege, AxMenuItem* — failed with "There was an error reflecting
                // type 'AxTable'". `modify` reads through here, so on a real installation it
                // could not touch a table at all. The MetaModel types are DataContract-annotated
                // and that contract is what the on-disk format encodes, so this is also the
                // shape the caller expects to edit and hand back.
                var serializer = new System.Runtime.Serialization.DataContractSerializer(artifact.GetType());
                using (var sw = new StringWriter())
                using (var xw = System.Xml.XmlWriter.Create(sw, new System.Xml.XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
                {
                    serializer.WriteObject(xw, artifact);
                    xw.Flush();
                    xml = sw.ToString();
                }
            }
            catch (Exception ex)
            {
                // "There was an error reflecting type 'AxTable'" says nothing on its own; the
                // reason is always in the inner chain, and the validate path already chains it.
                // Without this, a serializer limitation and a genuinely unreadable artifact
                // produce the same undiagnosable message.
                return Fail("SERIALIZE_FAILED", Detail(ex));
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["kind"] = kind,
                ["name"] = name,
                ["source"] = "bridge",
                ["xml"] = xml,
            };
        }

        /// <summary>
        /// UseEnumValue=No (required for extensible enums) omits the &lt;Value&gt;
        /// element from the XML entirely, so AxEnumValue.Value deserialises to its
        /// int default (0) for every member — no exception, no fallback. The value
        /// that actually gets compiled is position-based (0,1,2,… by declaration
        /// order), so only trust the serialised Value when UseEnumValue=Yes and
        /// substitute the declaration-order index otherwise.
        /// </summary>
        private static void FixPositionalEnumValues(JsonObject result)
        {
            if (result == null || !(result["ok"] is JsonNode okNode) || !string.Equals(okNode.ToString(), "true", StringComparison.OrdinalIgnoreCase)) return;
            if (!(result["data"] is JsonObject data)) return;
            var useEnumValue = data["UseEnumValue"] != null ? data["UseEnumValue"].ToString() : null;
            if (string.Equals(useEnumValue, "Yes", StringComparison.OrdinalIgnoreCase)) return;
            if (!(data["EnumValues"] is JsonArray values)) return;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is JsonObject v && v["Value"] != null)
                {
                    v["Value"] = JsonValue.Create(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        // --- write path -----------------------------------------------------

        internal JsonObject SaveObject(JsonObject args) { return WriteArtifact(args, "create"); }
        internal JsonObject UpdateObject(JsonObject args) { return WriteArtifact(args, "update"); }
        internal JsonObject DeleteObject(JsonObject args) { return WriteArtifact(args, "delete"); }

        // --- xref (DYNAMICSXREFDB) -------------------------------------------

        internal JsonObject FindReferences(JsonObject args)
        {
            string symbol = args != null ? (string)args["symbol"] : null;
            string kind   = args != null ? (string)args["kind"]   : null;
            int    limit  = 200;
            if (args != null && args["limit"] is JsonNode ln && ln.GetValue<object>() is object lv && int.TryParse(lv.ToString(), out var li)) limit = li;
            return XrefRepository.Find(symbol, kind, limit);
        }

        // --- model manifest -------------------------------------------------

        internal JsonObject GetModelFolder(JsonObject args)
        {
            string name = args != null ? (string)args["name"] : null;
            if (string.IsNullOrWhiteSpace(name)) return Fail("MISSING_ARG", "name is required");
            if (!MetadataBootstrap.TryInitialize())
            {
                return Fail("METADATA_UNAVAILABLE", MetadataBootstrap.LastError ?? "IMetadataProvider failed to initialise.");
            }
            var folder = MetadataBootstrap.GetModelFolder(name, out var err);
            if (folder == null) return Fail(err ?? "MODEL_NOT_FOUND", err ?? ("Model '" + name + "' was not returned by ModelManifest."));
            return new JsonObject
            {
                ["ok"] = true,
                ["name"] = name,
                ["folder"] = folder,
                ["source"] = "bridge",
            };
        }

        // --- validation (no model, nothing written) ---------------------------

        /// <summary>
        /// Answer "would <c>IMetadataProvider</c> accept this XML?" without writing
        /// anything: deserialize the blob into its MetaModel type exactly as
        /// <see cref="WriteArtifact"/> would, then serialize the resulting object back and
        /// report what the round-trip lost.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The lossy half is the point. <c>XmlSerializer</c> ignores elements the type does
        /// not declare, so a misspelled or invented property does not fail — it vanishes.
        /// The file still looks right on disk, offline validators still pass, and the object
        /// is quietly missing the property until a compile or a runtime somewhere disagrees.
        /// Comparing input against the round-trip is what turns that silent drop into a
        /// finding (audit findings R2/R6).
        /// </para>
        /// <para>
        /// Deserialization failure is reported as a verdict, not an RPC error, so a caller
        /// can validate a whole directory in one pass and get one row per file.
        /// </para>
        /// </remarks>
        internal JsonObject ValidateArtifact(JsonObject args)
        {
            string kind = args != null ? (string)args["kind"] : null;
            string xml = args != null ? (string)args["xml"] : null;

            if (string.IsNullOrWhiteSpace(xml)) return Fail("MISSING_ARG", "xml is required");

            if (!MetadataBootstrap.TryInitialize())
            {
                return Fail("METADATA_UNAVAILABLE",
                    MetadataBootstrap.LastError ??
                    "IMetadataProvider failed to initialise; set D365FO_BIN_PATH to the D365FO bin folder.");
            }

            string rootLocalName;
            string rootXsiType;
            try
            {
                using (var sr = new StringReader(xml))
                using (var xr = System.Xml.XmlReader.Create(sr, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit }))
                {
                    xr.MoveToContent();
                    rootLocalName = xr.NodeType == System.Xml.XmlNodeType.Element ? xr.LocalName : null;
                    rootXsiType = xr.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance");
                }
            }
            catch (Exception ex)
            {
                return Verdict(kind, null, null, false, "XML_PARSE_FAILED", ex.Message, null);
            }

            if (string.IsNullOrEmpty(rootLocalName))
                return Verdict(kind, null, null, false, "XML_PARSE_FAILED", "Could not read the root element.", null);

            var serializerType = MetadataBootstrap.GetMetaModelTypeByShortName(rootLocalName)
                                 ?? (string.IsNullOrWhiteSpace(kind) ? null : MetadataBootstrap.GetMetaModelType(kind));
            if (serializerType == null)
            {
                return Verdict(kind, rootLocalName, null, false, "TYPE_NOT_FOUND",
                    "No MetaModel type matches root element '" + rootLocalName +
                    "'. The AOT has no such object type, so nothing can read this file.", null);
            }
            if (serializerType.IsAbstract && string.IsNullOrEmpty(rootXsiType))
            {
                return Verdict(kind, rootLocalName, serializerType.FullName, false, "ABSTRACT_TYPE",
                    "Root element '" + rootLocalName + "' maps to abstract type '" + serializerType.FullName +
                    "'. Pin the concrete subtype via i:type.", null);
            }

            object ax;
            try
            {
                // DataContractSerializer, not XmlSerializer: the MetaModel types are
                // DataContract-annotated, and that contract is what the on-disk format
                // encodes — each type's own namespace (AxTable "", AxForm …V6,
                // AxMenuItem* …V1, AxWorkflow* …V2) and its member order. Validating with
                // XmlSerializer would happily accept files the platform cannot read, and
                // reject shipped Microsoft files it can, which is worse than no check.
                var serializer = new System.Runtime.Serialization.DataContractSerializer(serializerType);
                using (var sr = new StringReader(xml))
                using (var xr = System.Xml.XmlReader.Create(sr, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit }))
                {
                    ax = serializer.ReadObject(xr, true);
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? " / " + ex.InnerException.Message : string.Empty;
                return Verdict(kind, rootLocalName, serializerType.FullName, false,
                    "XML_DESERIALIZE_FAILED", ex.Message + inner, null);
            }

            var dropped = UnappliedValues(xml, ax);
            var droppedArray = new JsonArray();
            foreach (var d in dropped) droppedArray.Add(d);

            return Verdict(kind, rootLocalName, ax.GetType().FullName, true, null, null, droppedArray);
        }

        private static JsonObject Verdict(
            string kind, string rootElement, string clrType, bool deserialized,
            string errorCode, string errorMessage, JsonArray dropped)
        {
            var result = new JsonObject
            {
                ["ok"] = true,
                ["source"] = "bridge",
                ["kind"] = kind,
                ["rootElement"] = rootElement,
                ["clrType"] = clrType,
                ["deserialized"] = deserialized,
                ["valid"] = deserialized && (dropped == null || dropped.Count == 0),
                ["errorCode"] = errorCode,
                ["errorMessage"] = errorMessage,
                ["droppedCount"] = dropped == null ? 0 : dropped.Count,
            };
            if (dropped != null) result["dropped"] = dropped;
            return result;
        }

        /// <summary>
        /// Values the file states that the deserialized object does not carry — the honest
        /// definition of "silently dropped".
        /// </summary>
        /// <remarks>
        /// <para>
        /// The obvious implementation — re-serialize and diff the XML — does not work, and its
        /// failure mode is worth recording: <c>DataContractSerializer</c> omits members whose
        /// value equals the CLR default, so a perfectly applied
        /// <c>&lt;JoinMode&gt;InnerJoin&lt;/JoinMode&gt;</c> (InnerJoin being 0) is absent from
        /// the round-trip and looks lost. A validator that cries wolf about correct files is
        /// worse than none.
        /// </para>
        /// <para>
        /// So this walks the input against the object the provider actually built: for every
        /// element, resolve the member it names and compare what landed. A member the type
        /// does not declare, or a value that did not arrive, is reported; anything the
        /// serializer merely chose not to write back is not.
        /// </para>
        /// </remarks>
        private static System.Collections.Generic.List<string> UnappliedValues(string xml, object ax)
        {
            var lost = new System.Collections.Generic.List<string>();
            System.Xml.Linq.XDocument doc;
            try
            {
                doc = System.Xml.Linq.XDocument.Parse(xml);
            }
            catch
            {
                return lost;
            }

            if (doc.Root != null) CompareElement(doc.Root, ax, string.Empty, lost);
            lost.Sort(StringComparer.Ordinal);
            return lost;
        }

        private static void CompareElement(
            System.Xml.Linq.XElement element, object instance, string path,
            System.Collections.Generic.List<string> lost)
        {
            if (instance == null) return;

            foreach (var child in element.Elements())
            {
                var name = child.Name.LocalName;
                var childPath = path.Length == 0 ? name : path + "/" + name;

                // X++ source is modelled as an untyped property bag (AxPropertyCollection),
                // so it does not come back through the DataContract even for shipped
                // Microsoft files — reporting it would flag every class and form ever
                // written. The compiler is the oracle for source; this check is about
                // metadata properties.
                if (name == "SourceCode") continue;

                var member = FindMember(instance.GetType(), name);
                if (member == null)
                {
                    lost.Add(childPath + " — no such property on " + instance.GetType().Name);
                    continue;
                }

                object value;
                try { value = member.GetValue(instance, null); }
                catch { continue; }

                if (!child.HasElements)
                {
                    var written = child.Value.Trim();
                    if (written.Length == 0) continue; // nothing asserted
                    var actual = value == null ? string.Empty : Convert.ToString(value);
                    if (!string.Equals(written, actual, StringComparison.OrdinalIgnoreCase))
                        lost.Add(childPath + " = " + written + " (object has " +
                                 (value == null ? "<null>" : actual) + ")");
                    continue;
                }

                if (value == null)
                {
                    lost.Add(childPath + " — the whole element did not apply");
                    continue;
                }

                var items = value as System.Collections.IEnumerable;
                if (items != null && !(value is string))
                {
                    CompareCollection(child, items, childPath, lost);
                    continue;
                }

                CompareElement(child, value, childPath, lost);
            }
        }

        /// <summary>
        /// Collection members are matched by the item's <c>Name</c> — AOT collections are keyed
        /// that way — falling back to position when an item has no name.
        /// </summary>
        private static void CompareCollection(
            System.Xml.Linq.XElement element, System.Collections.IEnumerable items, string path,
            System.Collections.Generic.List<string> lost)
        {
            var actual = new System.Collections.Generic.List<object>();
            foreach (var item in items) actual.Add(item);

            var index = 0;
            foreach (var child in element.Elements())
            {
                var wantedName = NameOf(child);
                object match = null;

                if (wantedName != null)
                {
                    foreach (var candidate in actual)
                    {
                        var member = candidate == null ? null : FindMember(candidate.GetType(), "Name");
                        var candidateName = member == null ? null : Convert.ToString(member.GetValue(candidate, null));
                        if (string.Equals(candidateName, wantedName, StringComparison.OrdinalIgnoreCase))
                        {
                            match = candidate;
                            break;
                        }
                    }
                }
                else if (index < actual.Count)
                {
                    match = actual[index];
                }

                if (match == null)
                {
                    lost.Add(path + "/" + child.Name.LocalName +
                             (wantedName == null ? string.Empty : " '" + wantedName + "'") +
                             " — not present in the loaded object");
                }
                else
                {
                    CompareElement(child, match, path + "/" + child.Name.LocalName, lost);
                }

                index++;
            }
        }

        private static string NameOf(System.Xml.Linq.XElement element)
        {
            foreach (var child in element.Elements())
                if (child.Name.LocalName == "Name") return child.Value.Trim();
            return null;
        }

        /// <summary>
        /// Contract members are private <c>___serialize_*</c> properties on the declaring class,
        /// which a flattened lookup on the derived type cannot see — so walk the hierarchy.
        /// </summary>
        private static PropertyInfo FindMember(Type type, string memberName)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var direct = t.GetProperty(memberName, flags);
                if (direct != null && direct.CanRead) return direct;

                var serialized = t.GetProperty("___serialize_" + memberName, flags);
                if (serialized != null && serialized.CanRead) return serialized;
            }
            return null;
        }

        /// <summary>
        /// Shared implementation for create/update/delete. Accepts args with
        /// <c>kind</c>, <c>name</c>, <c>model</c>, and for create/update an
        /// optional <c>xml</c> blob (full Ax* XML as on disk). When xml is
        /// missing on create, produces a minimal artefact with just
        /// <c>Name</c> set.
        /// </summary>
        private JsonObject WriteArtifact(JsonObject args, string op)
        {
            string kind  = args != null ? (string)args["kind"]  : null;
            string name  = args != null ? (string)args["name"]  : null;
            string model = args != null ? (string)args["model"] : null;
            string xml   = args != null ? (string)args["xml"]   : null;

            if (string.IsNullOrWhiteSpace(kind))  return Fail("MISSING_ARG", "kind is required");
            if (string.IsNullOrWhiteSpace(name))  return Fail("MISSING_ARG", "name is required");
            if (string.IsNullOrWhiteSpace(model)) return Fail("MISSING_ARG", "model is required");
            if (string.Equals(op, "update", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(xml))
            {
                return Fail("MISSING_ARG", "xml is required for update");
            }
            if (!MetadataBootstrap.KindToCollection.ContainsKey(kind))
            {
                return Fail("INVALID_KIND", "kind must be one of: " + string.Join(", ", MetadataBootstrap.KindToCollection.Keys));
            }

            if (!MetadataBootstrap.TryInitialize())
            {
                return Fail("METADATA_UNAVAILABLE",
                    MetadataBootstrap.LastError ??
                    "IMetadataProvider failed to initialise; set D365FO_PACKAGES_PATH on a D365FO VM.");
            }

            var modelInfo = MetadataBootstrap.ReadModelInfo(model);
            if (modelInfo == null)
            {
                return Fail("MODEL_NOT_FOUND", "Model '" + model + "' was not returned by ModelManifest.");
            }
            var msi = MetadataBootstrap.BuildModelSaveInfo(modelInfo);
            if (msi == null) return Fail("MODEL_SAVE_INFO_FAILED", "Could not construct ModelSaveInfo for '" + model + "'.");

            // Delete path — no Ax instance needed.
            if (string.Equals(op, "delete", StringComparison.OrdinalIgnoreCase))
            {
                var (ok, err) = MetadataBootstrap.SaveArtifact(kind, "delete", null, name, msi);
                if (!ok) return Fail("DELETE_FAILED", err);
                return new JsonObject { ["ok"] = true, ["kind"] = kind, ["name"] = name, ["model"] = model, ["source"] = "bridge", ["op"] = "delete" };
            }

            // Create/Update — need an Ax* instance. For polymorphic kinds (edt, edtExtension)
            // the kind→base-type mapping resolves to an abstract class; the concrete subtype
            // must come from the input XML's root element name (e.g. AxEdtString).
            Type axType;
            object ax;
            if (!string.IsNullOrEmpty(xml))
            {
                string rootLocalName;
                string rootXsiType = null;
                try
                {
                    using (var sr = new StringReader(xml))
                    using (var xr = System.Xml.XmlReader.Create(sr, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit }))
                    {
                        xr.MoveToContent();
                        rootLocalName = xr.NodeType == System.Xml.XmlNodeType.Element ? xr.LocalName : null;
                        // Some scaffolds emit the abstract base element with the concrete
                        // subtype pinned via xsi:type (e.g. <AxEdt i:type="AxEdtString">).
                        // Capture it so the polymorphic root can still be constructed.
                        rootXsiType = xr.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance");
                    }
                }
                catch (Exception ex)
                {
                    return Fail("XML_PARSE_FAILED", ex.Message);
                }
                if (string.IsNullOrEmpty(rootLocalName))
                    return Fail("XML_PARSE_FAILED", "Could not read root element of input xml.");

                // The XmlSerializer must be built on the type whose XML root matches the
                // document's root ELEMENT name. For a concrete root (AxTable, AxClass,
                // AxEnum, AxForm) that is the type itself. For a polymorphic root emitted
                // as the abstract base with a pinned discriminator (e.g.
                // <AxEdt i:type="AxEdtString">), the serializer is built on the abstract
                // base — XmlInclude metadata lets xsi:type select the concrete subtype.
                var serializerType = MetadataBootstrap.GetMetaModelTypeByShortName(rootLocalName)
                                     ?? MetadataBootstrap.GetMetaModelType(kind);
                if (serializerType == null)
                    return Fail("TYPE_NOT_FOUND", "Could not resolve Ax type for root element '" + rootLocalName + "'.");
                if (serializerType.IsAbstract && string.IsNullOrEmpty(rootXsiType))
                    return Fail("ABSTRACT_TYPE", "Root element '" + rootLocalName + "' maps to abstract type '" + serializerType.FullName + "'. Pin a concrete subtype via xsi:type or use a concrete root such as AxEdtString.");

                try
                {
                    // DataContractSerializer for the same reason the read and validate paths use
                    // it: XmlSerializer refuses to reflect any type transitively holding an
                    // AccessGrant (IEnumerable with no Add(object)), which is every AxTable,
                    // AxSecurityPrivilege and AxMenuItem*. With XmlSerializer here, a write to a
                    // table failed before it reached IMetadataProvider — and the read path had
                    // the identical fault, so `modify` never worked against a real installation
                    // for the kinds people use most. It is also the serializer whose contract the
                    // on-disk format encodes, so what is parsed here matches what was handed out.
                    var serializer = new System.Runtime.Serialization.DataContractSerializer(serializerType);
                    using (var reader = new StringReader(xml))
                    using (var xr = System.Xml.XmlReader.Create(reader, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit }))
                    {
                        ax = serializer.ReadObject(xr, true);
                    }
                }
                catch (Exception ex)
                {
                    return Fail("XML_DESERIALIZE_FAILED", Detail(ex));
                }

                // Use the deserialized instance's runtime (concrete) type from here on —
                // for a polymorphic root this is the xsi:type subtype, not the base.
                axType = ax.GetType();
            }
            else
            {
                axType = MetadataBootstrap.GetMetaModelType(kind);
                if (axType == null) return Fail("TYPE_NOT_FOUND", "Could not resolve Ax type for kind '" + kind + "'.");
                if (axType.IsAbstract)
                    return Fail("ABSTRACT_TYPE", "Cannot construct kind '" + kind + "' without xml — base type '" + axType.FullName + "' is abstract. Provide xml with a concrete root element such as AxEdtString.");
                var ctor = axType.GetConstructor(Type.EmptyTypes);
                if (ctor == null) return Fail("TYPE_NOT_FOUND", "Ax type '" + axType.Name + "' has no parameterless ctor.");
                ax = ctor.Invoke(null);
            }

            // Always enforce Name from the request — it's authoritative.
            var nameProp = axType.GetProperty("Name");
            if (nameProp != null && nameProp.CanWrite) nameProp.SetValue(ax, name);

            var (ok2, err2) = MetadataBootstrap.SaveArtifact(kind, op, ax, null, msi);
            if (!ok2)
            {
                return Fail(op.ToUpperInvariant() + "_FAILED", err2);
            }
            return new JsonObject
            {
                ["ok"] = true,
                ["kind"] = kind,
                ["name"] = name,
                ["model"] = model,
                ["source"] = "bridge",
                ["op"] = op,
            };
        }

        private JsonObject ReadArtifact(JsonObject args, string collectionName, string kind)
        {
            string name = args != null ? (string)args["name"] : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return Fail("MISSING_ARG", "name is required");
            }

            if (!MetadataBootstrap.TryInitialize())
            {
                return Fail("METADATA_UNAVAILABLE",
                    MetadataBootstrap.LastError ??
                    "IMetadataProvider failed to initialise; set D365FO_PACKAGES_PATH on a D365FO VM.");
            }

            object artifact;
            try
            {
                artifact = MetadataBootstrap.ReadArtifact(collectionName, name);
            }
            catch (Exception ex)
            {
                return Fail("READ_FAILED", ex.GetType().Name + ": " + ex.Message);
            }

            if (artifact == null)
            {
                // Kernel-enum fallback: NoYes, Exists, ... are CLR enums
                // compiled into the X++ runtime assemblies. Only attempt the
                // probe when the original request was for an enum.
                if (string.Equals(kind, "enum", StringComparison.OrdinalIgnoreCase))
                {
                    var kernel = MetadataBootstrap.TryResolveKernelEnum(name);
                    if (kernel != null)
                    {
                        return new JsonObject
                        {
                            ["ok"] = true,
                            ["kind"] = kind,
                            ["name"] = name,
                            ["source"] = "bridge-kernel",
                            ["data"] = kernel,
                        };
                    }
                }
                return Fail("NOT_FOUND", kind + " '" + name + "' was not returned by IMetadataProvider.");
            }

            JsonNode body;
            try
            {
                body = AxSerializer.ToJson(artifact);
            }
            catch (Exception ex)
            {
                // "There was an error reflecting type 'AxTable'" says nothing on its own; the
                // reason is always in the inner chain, and the validate path already chains it.
                // Without this, a serializer limitation and a genuinely unreadable artifact
                // produce the same undiagnosable message.
                return Fail("SERIALIZE_FAILED", Detail(ex));
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["kind"] = kind,
                ["name"] = name,
                ["source"] = "bridge",
                ["data"] = body,
            };
        }

        private static JsonObject Fail(string code, string message)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = code,
                ["message"] = message,
            };
        }

        private static string RuntimeInformation()
        {
            try
            {
                var asm = typeof(object).Assembly;
                var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                return attr != null ? attr.InformationalVersion : asm.GetName().Version != null ? asm.GetName().Version.ToString() : "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
