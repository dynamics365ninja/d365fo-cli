// <copyright file="MetadataBootstrap.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;

namespace D365FO.Bridge
{
    /// <summary>
    /// Late-bound accessor for Microsoft's <c>IMetadataProvider</c> stack.
    /// Resolves the Microsoft.Dynamics.AX.Metadata.* assemblies from the
    /// configured <c>D365FO_BIN_PATH</c> (or <c>D365FO_PACKAGES_PATH\bin</c>)
    /// at runtime, so this net48 console exe can be built on a workstation
    /// without the D365FO assemblies available and only actually wire up to
    /// them on a real VM.
    /// </summary>
    internal static class MetadataBootstrap
    {
        private static readonly object _lock = new object();
        private static bool _resolverInstalled;
        private static string _binPath;
        private static string _packagesPath;

        // Cached reflection artefacts per logical provider instance.
        private static object _provider; // IMetadataProvider
        private static string _lastError;

        internal static string BinPath { get { return _binPath; } }
        internal static string PackagesPath { get { return _packagesPath; } }
        internal static string LastError { get { return _lastError; } }
        internal static bool IsLoaded { get { return _provider != null; } }

        /// <summary>
        /// Ensure the assembly-resolve hook is installed and the metadata
        /// provider instantiated. Returns true on success, stores a descriptive
        /// error in <see cref="LastError"/> on failure. Safe to call repeatedly
        /// — only the first call pays the reflection cost.
        /// </summary>
        internal static bool TryInitialize()
        {
            if (_provider != null) return true;
            lock (_lock)
            {
                if (_provider != null) return true;
                _lastError = null;

                _binPath = ResolveBinPath(out _packagesPath);
                if (string.IsNullOrEmpty(_binPath) || !Directory.Exists(_binPath))
                {
                    _lastError = "D365FO_BIN_PATH (or D365FO_PACKAGES_PATH\\bin) is not set or does not exist.";
                    return false;
                }

                if (!_resolverInstalled)
                {
                    AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                    _resolverInstalled = true;
                }

                try
                {
                    var storage = Assembly.LoadFrom(Path.Combine(_binPath, "Microsoft.Dynamics.AX.Metadata.Storage.dll"));
                    _provider = CreateProvider(storage);
                    if (_provider == null)
                    {
                        _lastError = "MetadataProviderFactory did not return a provider.";
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    _lastError = ex.GetType().Name + ": " + ex.Message;
                    _provider = null;
                    return false;
                }
            }
        }

        /// <summary>Return the cached <c>IMetadataProvider</c> or null.</summary>
        internal static object GetProvider()
        {
            return TryInitialize() ? _provider : null;
        }

        /// <summary>
        /// Expose runtime diagnostics as a JSON object — consumed by the
        /// <c>ping</c> JSON-RPC method.
        /// </summary>
        internal static JsonObject Diagnostics()
        {
            TryInitialize();
            return new JsonObject
            {
                ["loaded"] = _provider != null,
                ["binPath"] = _binPath ?? string.Empty,
                ["packagesPath"] = _packagesPath ?? string.Empty,
                ["error"] = _lastError ?? string.Empty,
            };
        }

        private static string ResolveBinPath(out string packagesPath)
        {
            packagesPath = Environment.GetEnvironmentVariable("D365FO_PACKAGES_PATH");
            var bin = Environment.GetEnvironmentVariable("D365FO_BIN_PATH");
            if (string.IsNullOrEmpty(bin) && !string.IsNullOrEmpty(packagesPath))
            {
                bin = Path.Combine(packagesPath, "bin");
            }
            return bin;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (string.IsNullOrEmpty(_binPath)) return null;
            try
            {
                var name = new AssemblyName(args.Name).Name + ".dll";
                var path = Path.Combine(_binPath, name);
                if (File.Exists(path))
                {
                    return Assembly.LoadFrom(path);
                }
            }
            catch
            {
                // swallow — the resolver returning null lets the CLR try other handlers.
            }
            return null;
        }

        /// <summary>
        /// Late-bound equivalent of:
        /// <code>
        /// var config = new DiskProviderConfiguration();
        /// config.AddMetadataPath(packagesPath); // or AddXppMetadataFolder
        /// return new MetadataProviderFactory().CreateRuntimeProviderWithExtensions(config);
        /// </code>
        /// Falls back through a few factory method names to cope with minor
        /// API drift between D365FO platform updates.
        /// </summary>
        private static object CreateProvider(Assembly storage)
        {
            var factoryType = storage.GetType("Microsoft.Dynamics.AX.Metadata.Storage.MetadataProviderFactory");
            var configType = storage.GetType("Microsoft.Dynamics.AX.Metadata.Storage.DiskProvider.DiskProviderConfiguration");
            if (factoryType == null || configType == null)
            {
                throw new InvalidOperationException("MetadataProviderFactory / DiskProviderConfiguration types were not found in Microsoft.Dynamics.AX.Metadata.Storage.dll.");
            }

            var config = CreateConfig(configType);

            // Add package roots. Prefer AddMetadataPath (returns void), fall
            // back to MetadataPaths collection Add.
            if (!string.IsNullOrEmpty(_packagesPath))
            {
                var add = configType.GetMethod("AddMetadataPath", new[] { typeof(string) });
                if (add != null)
                {
                    add.Invoke(config, new object[] { _packagesPath });
                }
                else
                {
                    var prop = configType.GetProperty("MetadataPaths");
                    var list = prop != null ? prop.GetValue(config) : null;
                    if (list != null)
                    {
                        var addItem = list.GetType().GetMethod("Add", new[] { typeof(string) });
                        if (addItem != null) addItem.Invoke(list, new object[] { _packagesPath });
                    }
                }
            }

            var factory = Activator.CreateInstance(factoryType);

            // Preferred order: CreateRuntimeProviderWithExtensions (overlay +
            // extension merge, matches what Visual Studio shows), then
            // CreateRuntimeProvider, then CreateDiskProvider as last resort.
            foreach (var methodName in new[] { "CreateRuntimeProviderWithExtensions", "CreateRuntimeProvider", "CreateDiskProvider" })
            {
                var m = factoryType.GetMethod(methodName, new[] { configType });
                if (m != null)
                {
                    return m.Invoke(factory, new object[] { config });
                }
            }

            throw new MissingMethodException("MetadataProviderFactory has no known Create*Provider(DiskProviderConfiguration) method.");
        }

        /// <summary>
        /// Cope with both <c>DiskProviderConfiguration()</c> (older PUs) and
        /// <c>DiskProviderConfiguration(string packageRoot)</c> signatures.
        /// </summary>
        private static object CreateConfig(Type configType)
        {
            // Try parameterless first — it's the most stable shape across PUs.
            var noArg = configType.GetConstructor(Type.EmptyTypes);
            if (noArg != null) return noArg.Invoke(null);

            var stringCtor = configType.GetConstructor(new[] { typeof(string) });
            if (stringCtor != null) return stringCtor.Invoke(new object[] { _packagesPath ?? _binPath });

            // Fallback: first public ctor, filled with nulls/defaults.
            var anyCtor = configType.GetConstructors()[0];
            var args = new object[anyCtor.GetParameters().Length];
            for (int i = 0; i < args.Length; i++)
            {
                var pt = anyCtor.GetParameters()[i].ParameterType;
                args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
            }
            return anyCtor.Invoke(args);
        }

        /// <summary>
        /// Read a single <c>Ax*</c> artefact through the provider collection
        /// named by <paramref name="collectionName"/> (e.g. "Classes",
        /// "Tables"). Returns the Ax object or null when not found.
        /// </summary>
        internal static object ReadArtifact(string collectionName, string name)
        {
            var provider = GetProvider();
            if (provider == null) return null;
            var coll = provider.GetType().GetProperty(collectionName)?.GetValue(provider);
            if (coll == null) return null;
            var read = coll.GetType().GetMethod("Read", new[] { typeof(string) });
            if (read == null) return null;
            try
            {
                return read.Invoke(coll, new object[] { name });
            }
            catch (TargetInvocationException tex)
            {
                // Provider raises TypeLoadException / FileNotFoundException
                // for missing artefacts on some SKUs — treat as "not found".
                _lastError = tex.InnerException?.Message ?? tex.Message;
                return null;
            }
        }

        /// <summary>
        /// Kernel-level fallback for system enums that are compiled into the
        /// X++ runtime assemblies (e.g. <c>NoYes</c>, <c>Exists</c>, the
        /// <c>Microsoft.Dynamics.Ax.Xpp.*</c> types). We probe the typical
        /// Xpp support assemblies and, if the type is a CLR enum, materialise
        /// a synthetic JsonObject that mirrors the shape an AxEnum would
        /// produce — just enough for agent prompts.
        /// </summary>
        internal static JsonObject TryResolveKernelEnum(string name)
        {
            if (string.IsNullOrEmpty(_binPath)) return null;
            var candidates = new[]
            {
                "Microsoft.Dynamics.AX.Xpp.Support.dll",
                "Microsoft.Dynamics.AX.Xpp.AxShared.dll",
                "Microsoft.Dynamics.AX.Xpp.Redirect.dll",
            };
            foreach (var dll in candidates)
            {
                var path = Path.Combine(_binPath, dll);
                if (!File.Exists(path)) continue;
                Assembly asm;
                try { asm = Assembly.LoadFrom(path); }
                catch { continue; }

                // Try the XPP primary namespace and a generic scan as a
                // fallback — some enums live under nested namespaces.
                var t = asm.GetType("Microsoft.Dynamics.Ax.Xpp." + name, false)
                        ?? asm.GetType("Microsoft.Dynamics.AX.Xpp." + name, false);
                if (t == null)
                {
                    foreach (var et in asm.GetExportedTypes())
                    {
                        if (et.IsEnum && string.Equals(et.Name, name, StringComparison.Ordinal))
                        {
                            t = et;
                            break;
                        }
                    }
                }
                if (t == null || !t.IsEnum) continue;

                var values = new System.Text.Json.Nodes.JsonArray();
                foreach (var v in Enum.GetValues(t))
                {
                    values.Add(new System.Text.Json.Nodes.JsonObject
                    {
                        ["Name"]  = v.ToString(),
                        ["Value"] = Convert.ToInt32(v).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
                }
                return new System.Text.Json.Nodes.JsonObject
                {
                    ["Name"]             = name,
                    ["EnumValues"]       = values,
                    ["IsKernelEnum"]     = "True",
                    ["ClrType"]          = t.FullName,
                    ["ClrAssembly"]      = asm.GetName().Name,
                };
            }
            return null;
        }

        /// <summary>
        /// Collection metadata: the short provider-collection name ("Classes"),
        /// the matching MetaModel type name ("AxClass"), and the assembly that
        /// ships it (Microsoft.Dynamics.AX.Metadata.dll).
        /// </summary>
        internal static readonly System.Collections.Generic.Dictionary<string, string> KindToCollection =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "class", "Classes" },
                { "table", "Tables" },
                { "edt",   "Edts"    },
                { "enum",  "Enums"   },
                { "form",  "Forms"   },
            };

        private static readonly System.Collections.Generic.Dictionary<string, string> KindToTypeName =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "class", "Microsoft.Dynamics.AX.Metadata.MetaModel.AxClass" },
                { "table", "Microsoft.Dynamics.AX.Metadata.MetaModel.AxTable" },
                { "edt",   "Microsoft.Dynamics.AX.Metadata.MetaModel.AxEdt"   },
                { "enum",  "Microsoft.Dynamics.AX.Metadata.MetaModel.AxEnum"  },
                { "form",  "Microsoft.Dynamics.AX.Metadata.MetaModel.AxForm"  },
            };

        /// <summary>
        /// Look up the <c>AxClass</c>/<c>AxTable</c>/... Type from the loaded
        /// Microsoft.Dynamics.AX.Metadata assembly, for use with reflection-based
        /// construction and XmlSerializer-based deserialization.
        /// </summary>
        internal static Type GetMetaModelType(string kind)
        {
            if (!KindToTypeName.TryGetValue(kind, out var typeName)) return null;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                var t = asm.GetType(typeName, false);
                if (t != null) return t;
            }
            // Force-load if not present yet.
            try
            {
                var meta = Assembly.LoadFrom(Path.Combine(_binPath, "Microsoft.Dynamics.AX.Metadata.dll"));
                return meta.GetType(typeName, false);
            }
            catch { return null; }
        }

        /// <summary>
        /// Resolve the on-disk folder that owns <paramref name="modelName"/>
        /// according to <c>ModelManifest.GetFolderForModel</c>. Returns null
        /// and fills <paramref name="error"/> when the model is unknown.
        /// </summary>
        internal static string GetModelFolder(string modelName, out string error)
        {
            error = null;
            var info = ReadModelInfo(modelName);
            if (info == null) { error = "MODEL_NOT_FOUND"; return null; }
            var provider = GetProvider();
            var mm = provider?.GetType().GetProperty("ModelManifest")?.GetValue(provider);
            if (mm == null) { error = "MANIFEST_UNAVAILABLE"; return null; }
            var get = mm.GetType().GetMethod("GetFolderForModel", new[] { info.GetType().GetInterface("IModelReference") ?? info.GetType() });
            if (get == null)
            {
                // Try any overload.
                foreach (var m in mm.GetType().GetMethods())
                {
                    if (m.Name == "GetFolderForModel" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(info.GetType()))
                    {
                        get = m; break;
                    }
                }
            }
            if (get == null) { error = "GET_FOLDER_NOT_FOUND"; return null; }
            try
            {
                return (string)get.Invoke(mm, new object[] { info });
            }
            catch (TargetInvocationException tex)
            {
                error = tex.InnerException?.Message ?? tex.Message;
                return null;
            }
        }

        /// <summary>
        /// Load a <c>ModelInfo</c> for the given model name (as surfaced in
        /// <c>Descriptor\Model.xml</c>). Returns null when unknown.
        /// </summary>
        internal static object ReadModelInfo(string modelName)
        {
            var provider = GetProvider();
            if (provider == null) return null;
            var mm = provider.GetType().GetProperty("ModelManifest")?.GetValue(provider);
            if (mm == null) return null;
            var read = mm.GetType().GetMethod("Read", new[] { typeof(string) });
            if (read == null) return null;
            try { return read.Invoke(mm, new object[] { modelName }); }
            catch (TargetInvocationException tex)
            {
                _lastError = tex.InnerException?.Message ?? tex.Message;
                return null;
            }
        }

        /// <summary>
        /// Build a <c>ModelSaveInfo</c> from a <c>ModelInfo</c> — prefers the
        /// <c>.ctor(IModelReference)</c> shape, falls back to parameterless ctor
        /// plus property-copy.
        /// </summary>
        internal static object BuildModelSaveInfo(object modelInfo)
        {
            if (modelInfo == null) return null;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Type saveInfoType = null;
            foreach (var asm in assemblies)
            {
                saveInfoType = asm.GetType("Microsoft.Dynamics.AX.Metadata.MetaModel.ModelSaveInfo", false);
                if (saveInfoType != null) break;
            }
            if (saveInfoType == null) return null;

            // ctor(IModelReference) — ModelInfo implements IModelReference.
            foreach (var ctor in saveInfoType.GetConstructors())
            {
                var ps = ctor.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(modelInfo.GetType()))
                {
                    return ctor.Invoke(new object[] { modelInfo });
                }
            }

            // Fallback: parameterless + copy Name/Id/Layer/SequenceId from ModelInfo.
            var def = saveInfoType.GetConstructor(Type.EmptyTypes);
            if (def == null) return null;
            var si = def.Invoke(null);
            CopyProp(modelInfo, si, "Name");
            CopyProp(modelInfo, si, "Id");
            CopyProp(modelInfo, si, "Layer");
            CopyProp(modelInfo, si, "SequenceId");
            return si;
        }

        private static void CopyProp(object src, object dst, string name)
        {
            var sp = src.GetType().GetProperty(name);
            var dp = dst.GetType().GetProperty(name);
            if (sp == null || dp == null || !dp.CanWrite) return;
            try { dp.SetValue(dst, sp.GetValue(src)); } catch { }
        }

        /// <summary>
        /// Perform a write operation (<c>Create</c>, <c>Update</c>, or
        /// <c>Delete</c>) on the collection matching <paramref name="kind"/>.
        /// For Create/Update, the <c>AxClass</c>/<c>AxTable</c>/etc instance
        /// must be provided in <paramref name="axInstance"/>. For Delete, the
        /// name is passed in <paramref name="nameForDelete"/> and
        /// <paramref name="axInstance"/> is ignored.
        /// Returns (true, null) on success; (false, message) on failure.
        /// </summary>
        internal static (bool ok, string error) SaveArtifact(string kind, string op, object axInstance, string nameForDelete, object modelSaveInfo)
        {
            if (!KindToCollection.TryGetValue(kind, out var collectionName))
            {
                return (false, "Unknown kind: " + kind);
            }
            var provider = GetProvider();
            if (provider == null) return (false, _lastError ?? "provider unavailable");
            var coll = provider.GetType().GetProperty(collectionName)?.GetValue(provider);
            if (coll == null) return (false, collectionName + " provider not exposed");

            MethodInfo m;
            object[] callArgs;
            if (string.Equals(op, "delete", StringComparison.OrdinalIgnoreCase))
            {
                m = coll.GetType().GetMethod("Delete", new[] { typeof(string), modelSaveInfo.GetType() });
                callArgs = new[] { (object)nameForDelete, modelSaveInfo };
            }
            else
            {
                var methodName = string.Equals(op, "update", StringComparison.OrdinalIgnoreCase) ? "Update" : "Create";
                m = coll.GetType().GetMethod(methodName, new[] { axInstance.GetType(), modelSaveInfo.GetType() });
                callArgs = new[] { axInstance, modelSaveInfo };
            }
            if (m == null)
            {
                return (false, op + " method not found on " + collectionName);
            }
            try
            {
                m.Invoke(coll, callArgs);
                return (true, null);
            }
            catch (TargetInvocationException tex)
            {
                var inner = tex.InnerException ?? tex;
                return (false, inner.GetType().Name + ": " + inner.Message);
            }
            catch (Exception ex)
            {
                return (false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Enumerate all names in a provider collection (e.g. "Classes").
        /// Returns an empty list on failure.
        /// </summary>
        internal static System.Collections.Generic.List<string> ListNames(string collectionName)
        {
            var result = new System.Collections.Generic.List<string>();
            var provider = GetProvider();
            if (provider == null) return result;
            var coll = provider.GetType().GetProperty(collectionName)?.GetValue(provider);
            if (coll == null) return result;
            var listNames = coll.GetType().GetMethod("ListObjectNames", Type.EmptyTypes)
                           ?? coll.GetType().GetMethod("ListNames", Type.EmptyTypes);
            if (listNames == null) return result;
            try
            {
                var enumerable = listNames.Invoke(coll, null) as System.Collections.IEnumerable;
                if (enumerable == null) return result;
                foreach (var s in enumerable) result.Add(s?.ToString());
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
            return result;
        }
    }
}
