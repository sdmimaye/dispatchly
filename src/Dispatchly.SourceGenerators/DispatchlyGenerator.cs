using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dispatchly.SourceGenerators;

/// <summary>Emits handler registration and a <c>JsonSerializerContext</c> for Dispatchly messages.</summary>
[Generator]
public sealed class DispatchlyGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => IsCandidate(node), static (syntax, _) => HandlerCollector.Collect(syntax))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        context.RegisterSourceOutput(handlers.Collect(), static (source, models) => SourceEmitter.Emit(source, models));
    }

    private static bool IsCandidate(SyntaxNode node) =>
        node is ClassDeclarationSyntax { BaseList: not null } or RecordDeclarationSyntax { BaseList: not null };
}

internal static class HandlerCollector
{
    public static HandlerModel? Collect(GeneratorSyntaxContext context)
    {
        if (context.Node is not TypeDeclarationSyntax declaration)
        {
            return null;
        }

        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol handler)
        {
            return null;
        }

        if (handler.TypeKind != TypeKind.Class
            || handler.IsAbstract
            || handler.IsStatic
            || !IsExternallyPublic(handler))
        {
            return null;
        }

        INamedTypeSymbol? message = null;
        foreach (var implemented in handler.AllInterfaces)
        {
            if (implemented.Name != "IMessageHandler"
                || implemented.TypeArguments.Length != 1
                || implemented.ContainingNamespace.ToDisplayString() != "Dispatchly.Abstractions")
            {
                continue;
            }

            if (implemented.TypeArguments[0] is INamedTypeSymbol messageType)
            {
                message = messageType;
                break;
            }
        }

        if (message is null)
        {
            return null;
        }

        return HandlerModel.Create(handler, message);
    }

    private static bool IsExternallyPublic(ISymbol symbol)
    {
        for (ISymbol? current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record HandlerModel(
    string HandlerType,
    string MessageType,
    string MessageIdentifier,
    string TableName,
    bool ParameterizedConstructor,
    EquatableArray<PropertyModel> Properties,
    EquatableArray<string> ConstructorParameterNames,
    string? DiagnosticId,
    string? DiagnosticMessage)
{
    public static HandlerModel Create(INamedTypeSymbol handler, INamedTypeSymbol message)
    {
        var handlerType = handler.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var messageType = message.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var messageIdentifier = Sanitize(message.Name);
        var table = TableNameResolver.Resolve(message, out var tableError);
        if (tableError is not null)
        {
            return Invalid(handlerType, messageType, messageIdentifier, "DLY001", tableError);
        }

        if (!MessageShapeAnalyzer.TryDescribe(message, out var shape, out var shapeError))
        {
            return Invalid(handlerType, messageType, messageIdentifier, "DLY002", shapeError ?? "Unsupported message shape.");
        }

        return new HandlerModel(
            handlerType,
            messageType,
            messageIdentifier,
            table!,
            shape.ParameterizedConstructor,
            new EquatableArray<PropertyModel>(shape.Properties),
            new EquatableArray<string>(shape.ConstructorParameterNames),
            null,
            null);
    }

    private static HandlerModel Invalid(
        string handlerType,
        string messageType,
        string messageIdentifier,
        string diagnosticId,
        string message) =>
        new(handlerType, messageType, messageIdentifier, "", false, default, default, diagnosticId, message);

    private static string Sanitize(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        if (builder.Length == 0 || !char.IsLetter(builder[0]))
        {
            builder.Insert(0, 'M');
        }

        return builder.ToString();
    }
}

internal sealed record PropertyModel(
    string Name,
    string JsonName,
    string EncodedField,
    string TypeDisplay,
    string Kind,
    bool IsNullable,
    bool HasSetter,
    bool IsConstructorParameter) : IEquatable<PropertyModel>;
