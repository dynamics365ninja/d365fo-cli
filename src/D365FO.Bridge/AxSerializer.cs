// <copyright file="AxSerializer.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace D365FO.Bridge
{
    /// <summary>
    /// Reflection-based serialiser for Microsoft Ax* DTOs. Walks public
    /// readable properties up to <see cref="MaxDepth"/>, emitting a JsonObject
    /// that mirrors the shape of our CLI/MCP response envelopes. Handles the
    /// common cases (strings, primitives, enums, IEnumerable collections of
    /// Ax* objects). Deliberately lenient: unknown / inaccessible properties
    /// are silently skipped so a single API drift between D365FO PUs does not
    /// poison the whole response.
    /// </summary>
    internal static class AxSerializer
    {
        internal const int MaxDepth = 6;

        /// <summary>Top-level entry point — equivalent to <see cref="Serialize"/> at depth 0.</summary>
        internal static JsonNode ToJson(object ax)
        {
            if (ax == null) return null;
            var visited = new HashSet<object>(new ReferenceComparer());
            return Serialize(ax, 0, visited);
        }

        private static JsonNode Serialize(object value, int depth, HashSet<object> visited)
        {
            if (value == null) return null;

            var type = value.GetType();

            // Primitives and strings → JsonValue directly.
            if (type == typeof(string)) return JsonValue.Create((string)value);
            if (type.IsPrimitive) return JsonValue.Create(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            if (type.IsEnum) return JsonValue.Create(value.ToString());
            if (value is DateTime dt) return JsonValue.Create(dt.ToString("O"));
            if (value is Guid) return JsonValue.Create(value.ToString());
            if (value is decimal dec) return JsonValue.Create(dec.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (depth >= MaxDepth)
            {
                // Fallback: emit a shallow name if the object exposes one.
                var nameProp = type.GetProperty("Name");
                return JsonValue.Create(nameProp?.GetValue(value)?.ToString() ?? type.Name);
            }

            // Collections (but not strings).
            if (value is IEnumerable enumerable && !(value is string))
            {
                var arr = new JsonArray();
                foreach (var item in enumerable)
                {
                    arr.Add(Serialize(item, depth + 1, visited));
                }
                return arr;
            }

            // Cycle guard: Microsoft's Ax* objects occasionally link back up
            // to the owner (e.g. a field referencing its parent table). We
            // only visit each reference once; repeats emit a shallow Name.
            if (!type.IsValueType)
            {
                if (!visited.Add(value))
                {
                    var nameProp = type.GetProperty("Name");
                    return JsonValue.Create(nameProp?.GetValue(value)?.ToString() ?? type.Name);
                }
            }

            // Everything else: walk public instance properties.
            var obj = new JsonObject();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                // Skip a few noisy / infinite-recursion properties seen on
                // Microsoft's Ax* types.
                switch (prop.Name)
                {
                    case "SyncRoot":
                    case "IsSynchronized":
                    case "Item":
                    case "Count" when depth > 0:
                    case "Attributes" when prop.PropertyType == typeof(Type):
                        continue;
                }
                object v;
                try { v = prop.GetValue(value); }
                catch { continue; }
                if (v == null) continue;

                var json = Serialize(v, depth + 1, visited);
                // Drop empty arrays to keep the envelope lean.
                if (json is JsonArray a && a.Count == 0) continue;
                obj[prop.Name] = json;
            }

            return obj;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
