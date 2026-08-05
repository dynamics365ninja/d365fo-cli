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
        internal JsonObject ReadObjectXml(JsonObject args)
        {
            string kind = args != null ? (string)args["kind"] : null;
            string name = args != null ? (string)args["name"] : null;
            if (string.IsNullOrWhiteSpace(kind)) return Fail("MISSING_ARG", "kind is required");
            if (string.IsNullOrWhiteSpace(name)) return Fail("MISSING_ARG", "name is required");
            if (!MetadataBootstrap.KindToCollection.TryGetValue(kind, out var collectionName))
            {
                return Fail("INVALID_KIND", "kind must be one of: class, table, edt, enum, form");
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
                var serializer = new XmlSerializer(artifact.GetType());
                using (var sw = new StringWriter())
                {
                    serializer.Serialize(sw, artifact);
                    xml = sw.ToString();
                }
            }
            catch (Exception ex)
            {
                return Fail("SERIALIZE_FAILED", ex.GetType().Name + ": " + ex.Message);
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
                return Fail("INVALID_KIND", "kind must be one of: class, table, edt, enum, form");
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
                    var serializer = new XmlSerializer(serializerType);
                    using (var reader = new StringReader(xml))
                    {
                        ax = serializer.Deserialize(reader);
                    }
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException?.Message;
                    return Fail("XML_DESERIALIZE_FAILED", ex.Message + (inner != null ? " / " + inner : string.Empty));
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
                return Fail("SERIALIZE_FAILED", ex.GetType().Name + ": " + ex.Message);
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
