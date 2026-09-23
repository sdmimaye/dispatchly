using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Dispatchly.SourceGenerators;

internal sealed class MessageShape
{
    public bool ParameterizedConstructor { get; set; }

    public PropertyModel[] Properties { get; set; } = [];

    public string[] ConstructorParameterNames { get; set; } = [];
}

internal static class MessageShapeAnalyzer
{
    public static bool TryDescribe(INamedTypeSymbol message, out MessageShape shape, out string? error)
    {
        shape = new MessageShape();
        error = null;
        if (message.IsGenericType || message.IsValueType)
        {
            error = "generic and value-type messages are not supported. Register them with AddHandler and a JsonTypeInfo.";
            return false;
        }

        var properties = new List<PropertyModel>();
        foreach (var property in message.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.DeclaredAccessibility != Accessibility.Public || property.IsStatic || property.IsIndexer)
            {
                continue;
            }

            if (!TryMap(property.Type, out var kind, out var typeDisplay, out var nullable))
            {
                error = $"property '{property.Name}' has unsupported type '{property.Type.ToDisplayString()}'.";
                return false;
            }

            var jsonName = JsonName(property);
            properties.Add(new PropertyModel(
                property.Name,
                jsonName,
                "Prop_" + Sanitize(jsonName),
                typeDisplay,
                kind,
                nullable,
                property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false },
                false));
        }

        if (properties.Count == 0)
        {
            error = "it has no public properties.";
            return false;
        }

        IMethodSymbol? constructor = null;
        var bestCount = -1;
        foreach (var candidate in message.InstanceConstructors)
        {
            if (candidate.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (candidate.Parameters.Length == 1
                && SymbolEqualityComparer.Default.Equals(candidate.Parameters[0].Type, message))
            {
                continue;
            }

            var matched = true;
            foreach (var parameter in candidate.Parameters)
            {
                if (!properties.Any(property =>
                        string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
                        && SymbolEqualityComparer.Default.Equals(parameter.Type, FindType(message, property.Name))))
                {
                    matched = false;
                    break;
                }
            }

            if (matched && candidate.Parameters.Length > bestCount)
            {
                constructor = candidate;
                bestCount = candidate.Parameters.Length;
            }
        }

        if (constructor is null)
        {
            error = "it has no public constructor whose parameters match properties.";
            return false;
        }

        var parameterNames = constructor.Parameters.Select(parameter => parameter.Name).ToArray();
        for (var i = 0; i < properties.Count; i++)
        {
            var property = properties[i];
            var isParameter = parameterNames.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase));
            properties[i] = property with { IsConstructorParameter = isParameter };
            if (!isParameter && !property.HasSetter)
            {
                error = $"property '{property.Name}' is not a constructor parameter and has no public setter.";
                return false;
            }

            if (!constructor.Parameters.Any() && !property.HasSetter)
            {
                error = $"property '{property.Name}' has no public setter.";
                return false;
            }
        }

        var ordered = new List<PropertyModel>();
        foreach (var parameter in constructor.Parameters)
        {
            var property = properties.First(item =>
                string.Equals(item.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            ordered.Add(property);
        }

        foreach (var property in properties)
        {
            if (!property.IsConstructorParameter)
            {
                ordered.Add(property);
            }
        }

        shape.ParameterizedConstructor = constructor.Parameters.Length > 0;
        shape.Properties = ordered.ToArray();
        shape.ConstructorParameterNames = parameterNames;
        return true;
    }

    private static ITypeSymbol? FindType(INamedTypeSymbol message, string propertyName)
    {
        foreach (var property in message.GetMembers().OfType<IPropertySymbol>())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.Ordinal))
            {
                return property.Type;
            }
        }

        return null;
    }

    private static bool TryMap(ITypeSymbol type, out string kind, out string typeDisplay, out bool nullable)
    {
        nullable = false;
        if (type is INamedTypeSymbol named
            && named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1)
        {
            nullable = true;
            type = named.TypeArguments[0];
        }
        else if (type.NullableAnnotation == NullableAnnotation.Annotated && type.SpecialType == SpecialType.System_String)
        {
            nullable = true;
        }

        kind = type.SpecialType switch
        {
            SpecialType.System_String => "string",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Byte => "byte",
            SpecialType.System_Int16 => "short",
            SpecialType.System_Int32 => "int",
            SpecialType.System_Int64 => "long",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_Decimal => "decimal",
            SpecialType.System_Char => "char",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_UInt64 => "ulong",
            _ => "",
        };

        if (kind.Length == 0)
        {
            var ns = type.ContainingNamespace.ToDisplayString();
            kind = (ns, type.Name) switch
            {
                ("System", "Guid") => "guid",
                ("System", "DateTime") => "datetime",
                ("System", "DateTimeOffset") => "datetimeoffset",
                ("System", "DateOnly") => "dateonly",
                ("System", "TimeOnly") => "timeonly",
                ("System", "TimeSpan") => "timespan",
                _ => "",
            };
        }

        typeDisplay = kind switch
        {
            "string" => nullable ? "string?" : "string",
            "bool" => nullable ? "bool?" : "bool",
            "byte" => nullable ? "byte?" : "byte",
            "short" => nullable ? "short?" : "short",
            "int" => nullable ? "int?" : "int",
            "long" => nullable ? "long?" : "long",
            "float" => nullable ? "float?" : "float",
            "double" => nullable ? "double?" : "double",
            "decimal" => nullable ? "decimal?" : "decimal",
            "char" => nullable ? "char?" : "char",
            "sbyte" => nullable ? "sbyte?" : "sbyte",
            "ushort" => nullable ? "ushort?" : "ushort",
            "uint" => nullable ? "uint?" : "uint",
            "ulong" => nullable ? "ulong?" : "ulong",
            "guid" => nullable ? "global::System.Guid?" : "global::System.Guid",
            "datetime" => nullable ? "global::System.DateTime?" : "global::System.DateTime",
            "datetimeoffset" => nullable ? "global::System.DateTimeOffset?" : "global::System.DateTimeOffset",
            "dateonly" => nullable ? "global::System.DateOnly?" : "global::System.DateOnly",
            "timeonly" => nullable ? "global::System.TimeOnly?" : "global::System.TimeOnly",
            "timespan" => nullable ? "global::System.TimeSpan?" : "global::System.TimeSpan",
            _ => "",
        };

        return kind.Length > 0;
    }

    private static string JsonName(IPropertySymbol property)
    {
        foreach (var attribute in property.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == "JsonPropertyNameAttribute"
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string name
                && name.Length > 0)
            {
                return name;
            }
        }

        var text = property.Name;
        return char.ToLowerInvariant(text[0]) + text.Substring(1);
    }

    private static string Sanitize(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }
}
