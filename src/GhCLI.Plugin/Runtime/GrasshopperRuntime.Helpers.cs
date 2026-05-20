using System.Collections;
using System.Reflection;
using System.Text.Json;
using GhCLI.Core.Hashing;
using GhCLI.Protocol;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace GhCLI.Plugin.Runtime;

internal sealed partial class GrasshopperRuntime
{
    private sealed class PortSchemaSpec
    {
        public string Name { get; init; } = string.Empty;
        public string Access { get; init; } = "item";
        public string? TypeName { get; init; }
        public string? TypeHint { get; init; }
    }

    private static PortSummaryModel BuildPortSummary(IGH_Param param, string direction)
    {
        return new PortSummaryModel
        {
            Name = SafePortName(param, "port"),
            Direction = direction,
            TypeName = param.TypeName,
            TypeHint = TryGetPropertyValue(param, "TypeHint")?.ToString(),
            Access = param.Access.ToString().ToLowerInvariant(),
            Connected = direction == "in"
                ? param.SourceCount > 0
                : CountRecipients(param) > 0
        };
    }

    private static string SafePortName(IGH_Param param, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(param.NickName))
        {
            return param.NickName;
        }

        if (!string.IsNullOrWhiteSpace(param.Name))
        {
            return param.Name;
        }

        return fallback;
    }

    private static int CountRecipients(IGH_Param param)
    {
        if (TryGetPropertyValue(param, "Recipients") is IEnumerable recipients)
        {
            var count = 0;
            foreach (var _ in recipients)
            {
                count++;
            }

            return count;
        }

        return 0;
    }

    private static bool IsSelected(IGH_DocumentObject obj)
    {
        return obj.Attributes?.Selected ?? false;
    }

    private static string DetermineKind(IGH_DocumentObject obj)
    {
        if (IsPythonNode(obj))
        {
            return IsIronPythonNode(obj) ? "python_ironpython" : "python_cpython3";
        }

        if (obj is GH_NumberSlider)
        {
            return "slider";
        }

        if (obj is GH_BooleanToggle)
        {
            return "toggle";
        }

        if (obj is GH_Panel)
        {
            return "panel";
        }

        if (obj is GH_Scribble)
        {
            return "group";
        }

        var typeName = obj.GetType().Name;
        if (typeName.Contains("ValueList", StringComparison.OrdinalIgnoreCase))
        {
            return "value_list";
        }

        if (typeName.Contains("ClusterInput", StringComparison.OrdinalIgnoreCase))
        {
            return "cluster_input_hook";
        }

        if (typeName.Contains("ClusterOutput", StringComparison.OrdinalIgnoreCase))
        {
            return "cluster_output_hook";
        }

        if (typeName.Contains("Cluster", StringComparison.OrdinalIgnoreCase))
        {
            return "cluster_instance";
        }

        if (typeName.Contains("Group", StringComparison.OrdinalIgnoreCase))
        {
            return "group";
        }

        if (obj is IGH_Component)
        {
            return "native_component";
        }

        if (obj is IGH_Param)
        {
            return "param";
        }

        return "unknown";
    }

    private static bool IsPythonNode(IGH_DocumentObject obj)
    {
        var fullName = obj.GetType().FullName ?? string.Empty;
        var name = obj.Name ?? string.Empty;
        var nickname = obj.NickName ?? string.Empty;

        return fullName.Contains("Python", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Python", StringComparison.OrdinalIgnoreCase)
               || nickname.Contains("Python", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIronPythonNode(IGH_DocumentObject obj)
    {
        var fullName = obj.GetType().FullName ?? string.Empty;
        var name = obj.Name ?? string.Empty;
        return fullName.Contains("IronPython", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("IronPython", StringComparison.OrdinalIgnoreCase);
    }

    private static string KindPrefix(string kind)
    {
        return kind switch
        {
            "python_cpython3" => "PY",
            "python_ironpython" => "PY2",
            "slider" => "SLIDER",
            "toggle" => "TOGGLE",
            "panel" => "PANEL",
            "value_list" => "VLIST",
            "group" => "GROUP",
            "param" => "PARAM",
            _ => "NODE"
        };
    }

    private static string? GetSourceHashFromObject(IGH_DocumentObject node)
    {
        var source = TryGetPythonSource(node);
        return string.IsNullOrWhiteSpace(source) ? null : Sha256Hasher.ShortHash(source);
    }

    private static bool TryGetBoolPropertyValue(object target, string property)
    {
        var value = TryGetPropertyValue(target, property);
        return value is bool boolValue && boolValue;
    }

    private static object? TryGetPropertyValue(object? target, string property)
    {
        if (target is null)
        {
            return null;
        }

        var info = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return info?.GetValue(target);
    }

    private static Type? TryGetPropertyType(object? target, string property)
    {
        if (target is null)
        {
            return null;
        }

        return target.GetType()
            .GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .PropertyType;
    }

    private static bool TrySetPropertyValue(object target, string property, object? value)
    {
        var info = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (info is null || !info.CanWrite)
        {
            return false;
        }

        try
        {
            var converted = value is null
                ? null
                : ConvertToType(value, info.PropertyType);
            info.SetValue(target, converted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvokeNumeric(object target, string methodName, params double[] values)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(x => x.Name == methodName)
            .ToArray();

        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != values.Length)
            {
                continue;
            }

            var args = new object?[values.Length];
            var compatible = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                var converted = ConvertToType(values[i], parameters[i].ParameterType);
                if (converted is null)
                {
                    compatible = false;
                    break;
                }

                args[i] = converted;
            }

            if (!compatible)
            {
                continue;
            }

            try
            {
                method.Invoke(target, args!);
                return true;
            }
            catch
            {
                // try next overload
            }
        }

        return false;
    }

    private static bool TryInvokeNoArg(object target, params string[] methodNames)
    {
        foreach (var methodName in methodNames)
        {
            var method = target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(x => x.Name == methodName && x.GetParameters().Length == 0);

            if (method is null)
            {
                continue;
            }

            try
            {
                method.Invoke(target, Array.Empty<object>());
                return true;
            }
            catch
            {
                // Try the next possible method name.
            }
        }

        return false;
    }

    private static object? ConvertToType(object value, Type? targetType)
    {
        if (targetType is null)
        {
            return value;
        }

        var nonNullable = Nullable.GetUnderlyingType(targetType) ?? targetType;

        try
        {
            if (nonNullable == typeof(string))
            {
                return value.ToString();
            }

            if (nonNullable == typeof(bool))
            {
                return value switch
                {
                    bool b => b,
                    string s => bool.Parse(s),
                    _ => Convert.ToBoolean(value)
                };
            }

            if (nonNullable.IsEnum)
            {
                if (value is string enumName && Enum.TryParse(nonNullable, enumName, true, out var parsed))
                {
                    return parsed;
                }

                return Enum.ToObject(nonNullable, Convert.ToInt32(value));
            }

            return Convert.ChangeType(value, nonNullable);
        }
        catch
        {
            return null;
        }
    }

    private void ApplyPortSchema(
        IGH_DocumentObject node,
        IReadOnlyList<JsonElement>? inputSchema,
        IReadOnlyList<JsonElement>? outputSchema,
        List<string> warnings)
    {
        if (node is not IGH_Component component)
        {
            return;
        }

        var inputSpecs = inputSchema is null ? null : ParsePortSchema(inputSchema, "x");
        var outputSpecs = outputSchema is null ? null : ParsePortSchema(outputSchema, "a");

        if (node is IGH_VariableParameterComponent variableParams)
        {
            if (inputSpecs is not null)
            {
                EnsurePortCount(component, variableParams, GH_ParameterSide.Input, inputSpecs.Count);
            }

            if (outputSpecs is not null)
            {
                EnsurePortCount(component, variableParams, GH_ParameterSide.Output, outputSpecs.Count);
            }

            variableParams.VariableParameterMaintenance();
        }
        else if ((inputSpecs?.Count ?? 0) > component.Params.Input.Count ||
                 (outputSpecs?.Count ?? 0) > component.Params.Output.Count)
        {
            warnings.Add($"Node '{node.NickName}' does not support variable params; schema count could not be expanded.");
        }

        if (inputSpecs is not null)
        {
            ApplyScriptParamSchema(node, "Inputs", inputSpecs);
        }

        if (outputSpecs is not null)
        {
            ApplyScriptParamSchema(node, "Outputs", outputSpecs);
        }

        if (node is IGH_VariableParameterComponent schemaVariableParams)
        {
            // Script parameter metadata (type/access) can require a maintenance pass to materialize.
            schemaVariableParams.VariableParameterMaintenance();
            component.Params.OnParametersChanged();

            if (outputSpecs is not null && NeedsOutputTypeRebuild(component, outputSpecs))
            {
                warnings.Add("Detected stale Python output port typing; rebuilding outputs to enforce requested type schema.");
                RebuildPortsForSide(component, schemaVariableParams, GH_ParameterSide.Output, outputSpecs.Count);
                ApplyScriptParamSchema(node, "Outputs", outputSpecs);
                schemaVariableParams.VariableParameterMaintenance();
            }
        }

        if (inputSpecs is not null)
        {
            ApplyParamSchema(component.Params.Input, inputSpecs, warnings, "input");
        }

        if (outputSpecs is not null)
        {
            ApplyParamSchema(component.Params.Output, outputSpecs, warnings, "output");
        }

        component.Params.OnParametersChanged();

        if (outputSpecs is not null &&
            NeedsOutputTypeRebuild(component, outputSpecs) &&
            node is IGH_VariableParameterComponent fallbackVariableParams &&
            outputSpecs.Count > 0)
        {
            warnings.Add("Detected unresolved Python output typing after apply; running automatic schema-cycle fallback.");
            if (TryRunOutputSchemaCycle(node, component, fallbackVariableParams, outputSpecs, warnings))
            {
                ApplyParamSchema(component.Params.Output, outputSpecs, warnings, "output");
                component.Params.OnParametersChanged();
            }
        }

        if (node is IGH_VariableParameterComponent finalVariableParams)
        {
            if (inputSpecs is not null && component.Params.Input.Count != inputSpecs.Count)
            {
                EnsurePortCount(component, finalVariableParams, GH_ParameterSide.Input, inputSpecs.Count);
            }

            if (outputSpecs is not null && component.Params.Output.Count != outputSpecs.Count)
            {
                EnsurePortCount(component, finalVariableParams, GH_ParameterSide.Output, outputSpecs.Count);
            }

            finalVariableParams.VariableParameterMaintenance();
            component.Params.OnParametersChanged();

            if (inputSpecs is not null)
            {
                ApplyScriptParamSchema(node, "Inputs", inputSpecs);
                ApplyParamSchema(component.Params.Input, inputSpecs, warnings, "input");
            }

            if (outputSpecs is not null)
            {
                ApplyScriptParamSchema(node, "Outputs", outputSpecs);
                ApplyParamSchema(component.Params.Output, outputSpecs, warnings, "output");
            }

            component.Params.OnParametersChanged();
        }

        ValidatePortSchemaMaterialized(component.Params.Input, inputSpecs, node.NickName, "input");
        ValidatePortSchemaMaterialized(component.Params.Output, outputSpecs, node.NickName, "output");

        if (outputSpecs is not null && NeedsOutputTypeRebuild(component, outputSpecs))
        {
            warnings.Add("Some Python output ports still report Text after schema apply and fallback. Inspect node.read output typeName and consider recreating the node.");
        }
    }

    private static void ValidatePortSchemaMaterialized(
        IReadOnlyList<IGH_Param> ports,
        IReadOnlyList<PortSchemaSpec>? specs,
        string nodeName,
        string direction)
    {
        if (specs is null)
        {
            return;
        }

        if (ports.Count != specs.Count)
        {
            throw new GhCLI.Core.Errors.CommandValidationException(
                $"Python node '{nodeName}' requested {specs.Count} {direction} ports, but Grasshopper materialized {ports.Count}.");
        }

        for (var i = 0; i < specs.Count; i++)
        {
            var actual = SafePortName(ports[i], direction);
            if (!string.Equals(actual, specs[i].Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new GhCLI.Core.Errors.CommandValidationException(
                    $"Python node '{nodeName}' {direction} port {i} expected '{specs[i].Name}', but Grasshopper materialized '{actual}'.");
            }
        }
    }

    private static List<PortSchemaSpec> ParsePortSchema(IReadOnlyList<JsonElement> schema, string fallbackPrefix)
    {
        var specs = new List<PortSchemaSpec>();
        for (var i = 0; i < schema.Count; i++)
        {
            var entry = schema[i];
            string? name = null;
            string access = "item";
            string? typeName = null;
            string? typeHint = null;

            if (entry.ValueKind == JsonValueKind.String)
            {
                name = entry.GetString();
            }
            else if (entry.ValueKind == JsonValueKind.Object)
            {
                name = TryGetJsonString(entry, "name", "nickName", "nickname");
                access = TryGetJsonString(entry, "access", "port_access", "param_access") ?? "item";
                typeName = TryGetJsonString(entry, "typeName", "type_name", "type");
                typeHint = TryGetJsonString(entry, "typeHint", "type_hint", "hint");
            }

            specs.Add(new PortSchemaSpec
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"{fallbackPrefix}{i}" : name!,
                Access = string.IsNullOrWhiteSpace(access) ? "item" : access,
                TypeName = typeName,
                TypeHint = typeHint
            });
        }

        return specs;
    }

    private static void EnsurePortCount(
        IGH_Component component,
        IGH_VariableParameterComponent variableParams,
        GH_ParameterSide side,
        int targetCount)
    {
        var isInput = side == GH_ParameterSide.Input;
        var list = isInput ? component.Params.Input : component.Params.Output;

        while (list.Count < targetCount && variableParams.CanInsertParameter(side, list.Count))
        {
            var created = variableParams.CreateParameter(side, list.Count);
            if (created is null)
            {
                break;
            }

            if (isInput)
            {
                component.Params.RegisterInputParam(created);
            }
            else
            {
                component.Params.RegisterOutputParam(created);
            }
        }

        while (list.Count > targetCount && variableParams.CanRemoveParameter(side, list.Count - 1))
        {
            var target = list[list.Count - 1];
            if (isInput)
            {
                component.Params.UnregisterInputParameter(target, true);
            }
            else
            {
                component.Params.UnregisterOutputParameter(target, true);
            }
        }
    }

    private void ApplyParamSchema(
        IReadOnlyList<IGH_Param> ports,
        IReadOnlyList<PortSchemaSpec> specs,
        List<string> warnings,
        string direction)
    {
        var count = Math.Min(ports.Count, specs.Count);
        for (var i = 0; i < count; i++)
        {
            var port = ports[i];
            var spec = specs[i];
            port.Name = spec.Name;
            port.NickName = spec.Name;

            if (TryParseGhParamAccess(spec.Access, out var access))
            {
                port.Access = access;
            }
            else
            {
                warnings.Add($"Unsupported {direction} port access '{spec.Access}' on '{spec.Name}'.");
            }

            var appliedType = TryApplyDirectPortType(port, spec);
            if (HasExplicitType(spec) && !appliedType)
            {
                warnings.Add($"Could not directly apply {direction} port type '{GetRequestedType(spec)}' on '{spec.Name}'.");
            }
        }
    }

    private void ApplyScriptParamSchema(
        IGH_DocumentObject node,
        string propertyName,
        IReadOnlyList<PortSchemaSpec> specs)
    {
        if (TryGetPropertyValue(node, propertyName) is not IEnumerable raw)
        {
            return;
        }

        var scriptParams = raw.Cast<object>().ToList();
        var count = Math.Min(scriptParams.Count, specs.Count);

        for (var i = 0; i < count; i++)
        {
            var scriptParam = scriptParams[i];
            var spec = specs[i];

            _ = TrySetPropertyValue(scriptParam, "VariableName", spec.Name);
            _ = TrySetPropertyValue(scriptParam, "PrettyName", spec.Name);
            _ = TrySetPropertyValue(scriptParam, "Name", spec.Name);
            _ = TrySetPropertyValue(scriptParam, "Access", NormalizeScriptParamAccess(spec.Access));
            _ = TryApplyScriptParamType(scriptParam, spec);
        }
    }

    private bool TryApplyDirectPortType(IGH_Param port, PortSchemaSpec spec)
    {
        var requestedType = GetRequestedType(spec);
        if (string.IsNullOrWhiteSpace(requestedType))
        {
            return true;
        }

        var applied = false;
        applied |= TrySetPropertyValue(port, "TypeHint", requestedType);
        applied |= TrySetPropertyValue(port, "TypeHintName", requestedType);
        applied |= TrySetPropertyValue(port, "Hint", requestedType);
        applied |= TrySetPropertyValue(port, "TypeName", requestedType);
        applied |= TryInvokeSingleStringMethod(port, requestedType, "SetTypeHint", "SetHint", "SetTypeName");
        return applied;
    }

    private bool TryApplyScriptParamType(object scriptParam, PortSchemaSpec spec)
    {
        var requestedType = GetRequestedType(spec);
        if (string.IsNullOrWhiteSpace(requestedType))
        {
            return true;
        }

        var applied = false;
        if (TryResolveRuntimeParamType(requestedType) is { } resolved &&
            TrySetPropertyValue(scriptParam, "ValueType", resolved))
        {
            applied = true;
        }

        if (TryResolveRuntimeParamType(requestedType) is { } resolvedEnumLike)
        {
            applied |= TrySetPropertyValue(scriptParam, "ParamType", resolvedEnumLike);
            applied |= TrySetPropertyValue(scriptParam, "Type", resolvedEnumLike);
        }

        applied |= TrySetPropertyValue(scriptParam, "TypeName", requestedType);
        applied |= TrySetPropertyValue(scriptParam, "TypeHintName", requestedType);
        applied |= TrySetPropertyValue(scriptParam, "TypeHint", requestedType);
        applied |= TrySetPropertyValue(scriptParam, "Hint", requestedType);
        applied |= TrySetPropertyValue(scriptParam, "ValueTypeName", requestedType);
        applied |= TryInvokeSingleStringMethod(scriptParam, requestedType, "SetTypeName", "SetTypeHint", "SetHint", "SetType");
        return applied;
    }

    private static bool NeedsOutputTypeRebuild(IGH_Component component, IReadOnlyList<PortSchemaSpec> outputSpecs)
    {
        var count = Math.Min(component.Params.Output.Count, outputSpecs.Count);
        for (var i = 0; i < count; i++)
        {
            var spec = outputSpecs[i];
            if (!HasExplicitType(spec))
            {
                continue;
            }

            if (!IsTextRequested(spec) && IsTextLikePort(component.Params.Output[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static void RebuildPortsForSide(
        IGH_Component component,
        IGH_VariableParameterComponent variableParams,
        GH_ParameterSide side,
        int targetCount)
    {
        var isInput = side == GH_ParameterSide.Input;
        var list = isInput ? component.Params.Input : component.Params.Output;

        while (list.Count > 0)
        {
            var index = list.Count - 1;
            if (!variableParams.CanRemoveParameter(side, index))
            {
                break;
            }

            var target = list[index];
            if (isInput)
            {
                component.Params.UnregisterInputParameter(target, true);
            }
            else
            {
                component.Params.UnregisterOutputParameter(target, true);
            }
        }

        EnsurePortCount(component, variableParams, side, targetCount);
    }

    private bool TryRunOutputSchemaCycle(
        IGH_DocumentObject node,
        IGH_Component component,
        IGH_VariableParameterComponent variableParams,
        IReadOnlyList<PortSchemaSpec> outputSpecs,
        List<string> warnings)
    {
        try
        {
            EnsurePortCount(component, variableParams, GH_ParameterSide.Output, 0);
            variableParams.VariableParameterMaintenance();
            component.Params.OnParametersChanged();

            EnsurePortCount(component, variableParams, GH_ParameterSide.Output, outputSpecs.Count);
            ApplyScriptParamSchema(node, "Outputs", outputSpecs);
            variableParams.VariableParameterMaintenance();
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"Automatic output schema-cycle fallback failed: {ex.Message}");
            return false;
        }
    }

    private static bool HasExplicitType(PortSchemaSpec spec)
    {
        return !string.IsNullOrWhiteSpace(GetRequestedType(spec));
    }

    private static string? GetRequestedType(PortSchemaSpec spec)
    {
        return !string.IsNullOrWhiteSpace(spec.TypeHint) ? spec.TypeHint : spec.TypeName;
    }

    private static bool IsTextRequested(PortSchemaSpec spec)
    {
        var requestedType = GetRequestedType(spec);
        if (string.IsNullOrWhiteSpace(requestedType))
        {
            return false;
        }

        return requestedType.Contains("text", StringComparison.OrdinalIgnoreCase)
               || requestedType.Contains("string", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextLikePort(IGH_Param port)
    {
        var typeName = port.TypeName ?? string.Empty;
        if (typeName.Contains("text", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("string", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var runtimeType = port.GetType().Name;
        return runtimeType.Contains("Text", StringComparison.OrdinalIgnoreCase)
               || runtimeType.Contains("String", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryInvokeSingleStringMethod(object target, string value, params string[] methodNames)
    {
        foreach (var methodName in methodNames)
        {
            var candidates = target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(x => x.Name == methodName)
                .ToArray();

            foreach (var method in candidates)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
                {
                    continue;
                }

                try
                {
                    method.Invoke(target, new object[] { value });
                    return true;
                }
                catch
                {
                    // Continue trying other overloads.
                }
            }
        }

        return false;
    }

    private static string? TryGetJsonString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
            {
                return direct.GetString();
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }

        return null;
    }

    private static bool TryParseGhParamAccess(string access, out GH_ParamAccess value)
    {
        switch ((access ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "item":
                value = GH_ParamAccess.item;
                return true;
            case "list":
                value = GH_ParamAccess.list;
                return true;
            case "tree":
                value = GH_ParamAccess.tree;
                return true;
            default:
                value = GH_ParamAccess.item;
                return false;
        }
    }

    private static string NormalizeScriptParamAccess(string access)
    {
        return (access ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "list" => "List",
            "tree" => "Tree",
            _ => "Item"
        };
    }

    private static object? TryResolveRuntimeParamType(string requestedType)
    {
        if (string.IsNullOrWhiteSpace(requestedType))
        {
            return null;
        }

        var runtimeAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(x => string.Equals(x.GetName().Name, "Rhino.Runtime.Code", StringComparison.OrdinalIgnoreCase));

        runtimeAssembly ??= TryLoadAssembly("Rhino.Runtime.Code");
        var paramTypeType = runtimeAssembly?.GetType("Rhino.Runtime.Code.ParamType");
        if (paramTypeType is null)
        {
            return null;
        }

        foreach (var candidate in EnumerateTypeCandidates(requestedType))
        {
            var primitiveArgs = new object?[] { candidate, null };
            if (InvokeOutParamMethod(paramTypeType, "TryGetPrimitiveType", primitiveArgs))
            {
                return primitiveArgs[1];
            }

            var typeArgs = new object?[] { candidate, null };
            if (InvokeOutParamMethod(paramTypeType, "TryGetType", typeArgs))
            {
                return typeArgs[1];
            }

            var importedArgs = new object?[]
            {
                new[] { "System", "Rhino", "Rhino.Geometry" },
                candidate,
                null
            };

            if (InvokeOutParamMethod(
                    paramTypeType,
                    "TryGetType",
                    importedArgs,
                    new[] { typeof(IEnumerable<string>), typeof(string), paramTypeType.MakeByRefType() }))
            {
                return importedArgs[2];
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateTypeCandidates(string requestedType)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(requestedType))
        {
            yield return requestedType;
        }

        if (!requestedType.Contains('.'))
        {
            var rhinoGeometry = $"Rhino.Geometry.{requestedType}";
            if (seen.Add(rhinoGeometry))
            {
                yield return rhinoGeometry;
            }
        }

        if (string.Equals(requestedType, "float", StringComparison.OrdinalIgnoreCase) && seen.Add("double"))
        {
            yield return "double";
        }
    }

    private static bool InvokeOutParamMethod(
        Type targetType,
        string methodName,
        object?[] args,
        Type[]? signature = null)
    {
        var methods = signature is null
            ? targetType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(x => x.Name == methodName && x.GetParameters().Length == args.Length)
            : targetType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(x => x.Name == methodName && ParametersMatch(x.GetParameters(), signature));

        foreach (var method in methods)
        {
            var invocationArgs = (object?[])args.Clone();
            if (method.Invoke(null, invocationArgs) is bool ok && ok)
            {
                Array.Copy(invocationArgs, args, invocationArgs.Length);
                return true;
            }
        }

        return false;
    }

    private static bool ParametersMatch(ParameterInfo[] parameters, Type[] signature)
    {
        if (parameters.Length != signature.Length)
        {
            return false;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType != signature[i])
            {
                return false;
            }
        }

        return true;
    }

    private static Assembly? TryLoadAssembly(string name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch
        {
            return null;
        }
    }
}
