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
        internal JsonObject ReadEnum(JsonObject args) { return ReadArtifact(args, "Enums", "enum"); }
        internal JsonObject ReadForm(JsonObject args) { return ReadArtifact(args, "Forms", "form"); }

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

            // Create/Update — need an Ax* instance.
            var axType = MetadataBootstrap.GetMetaModelType(kind);
            if (axType == null) return Fail("TYPE_NOT_FOUND", "Could not resolve Ax type for kind '" + kind + "'.");

            object ax;
            if (!string.IsNullOrEmpty(xml))
            {
                try
                {
                    var serializer = new XmlSerializer(axType);
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
            }
            else
            {
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
