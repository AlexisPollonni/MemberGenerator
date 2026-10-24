using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using MemberGenerator.Ex;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace MemberGenerator;

[Generator(LanguageNames.CSharp)]
public class DefaultMemberGenerator : IIncrementalGenerator
{
    private const string AttributeSource =
        @"
namespace MemberGenerator
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
    public sealed class GenerateDefaultMembersAttribute : System.Attribute { }
}";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
            ctx.AddSource(
                "GenerateDefaultMembersAttribute.g.cs",
                SourceText.From(AttributeSource, Encoding.UTF8)
            )
        );

        var provider = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "MemberGenerator.GenerateDefaultMembersAttribute",
                predicate: (node, _) =>
                    node
                        is StructDeclarationSyntax
                            or ClassDeclarationSyntax
                            or RecordDeclarationSyntax,
                transform: TransformTarget
            )
            .Where(m => m is not null);

        context.RegisterSourceOutput(provider, (spc, model) => Execute(spc, model!));
    }

    private static TargetModel? TransformTarget(
        GeneratorAttributeSyntaxContext context,
        CancellationToken token
    )
    {
        if (context.TargetSymbol is not INamedTypeSymbol targetType)
            return null;

        var targetSyntax = (TypeDeclarationSyntax)context.TargetNode;
        var generatedMembers = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var isStruct = targetType.TypeKind == TypeKind.Struct;

        foreach (var member in targetType.AllInterfaces.SelectMany(iface => iface.GetMembers()))
        {
            token.ThrowIfCancellationRequested();

            if (member is not IMethodSymbol and not IPropertySymbol)
                continue;
            switch (member)
            {
                case IMethodSymbol { AssociatedSymbol: not null }:
                case IMethodSymbol { MethodKind: MethodKind.ExplicitInterfaceImplementation }:
                case IPropertySymbol { ExplicitInterfaceImplementations: { IsEmpty: false } }:
                    continue;
            }

            // --- NEW: Skip static utilities ---
            // Static methods in interfaces are only part of the contract if they are abstract or virtual.
            if (member.IsStatic && !member.IsVirtual && !member.IsAbstract)
                continue;

            var implementation = targetType.FindImplementationForInterfaceMember(member);

            if (implementation == null)
            {
                if (!member.IsAbstract)
                    implementation = member;
                else
                    continue;
            }

            if (SymbolEqualityComparer.Default.Equals(implementation.ContainingType, targetType))
                continue;
            if (implementation.IsAbstract)
                continue;
            if (implementation.IsSealed)
                continue;
            if (implementation.DeclaringSyntaxReferences.IsEmpty)
                continue;
            if (generatedMembers.ContainsKey(implementation))
                continue;

            var syntaxRef = implementation.DeclaringSyntaxReferences[0];
            var syntaxNode = syntaxRef.GetSyntax(token);

            // Direct syntax check for 'sealed'
            // Roslyn sometimes drops the IsSealed flag for static methods.
            if (
                syntaxNode is MemberDeclarationSyntax memDecl
                && memDecl.Modifiers.Any(SyntaxKind.SealedKeyword)
            )
                continue;

            var semanticModel = context.SemanticModel.Compilation.GetSemanticModel(
                syntaxRef.SyntaxTree
            );

            var implInterface = implementation.ContainingType;
            var typeMap = new Dictionary<ITypeSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            for (var i = 0; i < implInterface.TypeArguments.Length; i++)
            {
                typeMap[implInterface.OriginalDefinition.TypeParameters[i]] =
                    implInterface.TypeArguments[i];
            }

            // Pass isStruct to the rewriter
            var rewriter = new DimRewriter(semanticModel, typeMap, isStruct);
            var rewrittenNode = rewriter.Visit(syntaxNode);

            generatedMembers.Add(implementation, rewrittenNode.ToFullString());
        }

        if (generatedMembers.Count == 0)
            return null;

        return new(
            targetType.ContainingNamespace.IsGlobalNamespace
                ? null
                : targetType.ContainingNamespace.ToDisplayString(),
            targetType.Name,
            GetMirrorKeywords(targetSyntax).ToString(),
            targetType.TypeParameters.IsDefaultOrEmpty
                ? ""
                : $"<{string.Join(", ", targetType.TypeParameters.Select(t => t.Name))}>",
            generatedMembers.Values.ToImmutableArray()
        );
    }

    private static void Execute(SourceProductionContext context, TargetModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");

        if (model.Namespace != null)
        {
            sb.AppendLine($"namespace {model.Namespace}");
            sb.AppendLine("{");
        }

        sb.AppendLine($"    {model.TypeKind} {model.Name}{model.Generics}");
        sb.AppendLine("    {");

        foreach (var member in model.GeneratedMembers)
        {
            sb.AppendLine(member);
            sb.AppendLine();
        }

        sb.AppendLine("    }");

        if (model.Namespace != null)
        {
            sb.AppendLine("}");
        }

        context.AddSource(
            $"{model.Name}.DefaultMembers.g.cs",
            SourceText.From(sb.ToString(), Encoding.UTF8)
        );
    }

    private static SyntaxTokenList GetMirrorKeywords(TypeDeclarationSyntax originalType)
    {
        // 1. Start with the core modifiers (e.g., public, readonly, partial, static, internal)
        var tokens = new List<SyntaxToken>(originalType.Modifiers);

        // 2. If it's a record, append the "record" and "struct/class" context keywords
        if (originalType is not RecordDeclarationSyntax recordDecl)
            return SyntaxFactory.TokenList(tokens).Add(originalType.Keyword);

        if (!recordDecl.Keyword.IsKind(SyntaxKind.None))
        {
            tokens.Add(recordDecl.Keyword);
        }

        if (!recordDecl.ClassOrStructKeyword.IsKind(SyntaxKind.None))
        {
            tokens.Add(recordDecl.ClassOrStructKeyword);
        }

        return SyntaxFactory.TokenList(tokens);
    }
}

internal record TargetModel(
    string? Namespace,
    string Name,
    string TypeKind,
    string Generics,
    ImmutableArray<string> GeneratedMembers
)
{
    public virtual bool Equals(TargetModel? other)
    {
        if (other is null)
            return false;
        return Namespace == other.Namespace
            && Name == other.Name
            && TypeKind == other.TypeKind
            && Generics == other.Generics
            && GeneratedMembers.SequenceEqual(other.GeneratedMembers);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (Namespace?.GetHashCode() ?? 0);
            hash = hash * 31 + Name.GetHashCode();
            hash = hash * 31 + TypeKind.GetHashCode();
            hash = hash * 31 + Generics.GetHashCode();
            foreach (var member in GeneratedMembers)
                hash = hash * 31 + member.GetHashCode();
            return hash;
        }
    }
}
