using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MemberGenerator;

internal class DimRewriter(
    SemanticModel semanticModel,
    Dictionary<ITypeSymbol, ITypeSymbol> typeMap,
    bool isStruct
) : CSharpSyntaxRewriter
{
    public override SyntaxToken VisitToken(SyntaxToken token)
    {
        var newToken = base.VisitToken(token);

        if (newToken.HasLeadingTrivia)
        {
            var leading = newToken.LeadingTrivia.Where(t =>
                !t.IsKind(SyntaxKind.RegionDirectiveTrivia)
                && !t.IsKind(SyntaxKind.EndRegionDirectiveTrivia)
            );
            newToken = newToken.WithLeadingTrivia(SyntaxFactory.TriviaList(leading));
        }

        if (newToken.HasTrailingTrivia)
        {
            var trailing = newToken.TrailingTrivia.Where(t =>
                !t.IsKind(SyntaxKind.RegionDirectiveTrivia)
                && !t.IsKind(SyntaxKind.EndRegionDirectiveTrivia)
            );
            newToken = newToken.WithTrailingTrivia(SyntaxFactory.TriviaList(trailing));
        }

        return newToken;
    }

    public override SyntaxNode? VisitAttribute(AttributeSyntax node)
    {
        var rewritten = (AttributeSyntax)base.VisitAttribute(node)!;

        var symbolInfo = semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

        ITypeSymbol? attrType = symbol is IMethodSymbol ms
            ? ms.ContainingType
            : symbol as ITypeSymbol;

        if (attrType != null)
        {
            var fullyQualifiedName = ReplaceGenericsAndQualify(attrType);
            rewritten = rewritten.WithName(
                SyntaxFactory.ParseName(fullyQualifiedName).WithTriviaFrom(rewritten.Name)
            );
        }

        return rewritten;
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
        HandleName(node, n => base.VisitIdentifierName((IdentifierNameSyntax)n));

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node) =>
        HandleName(node, n => base.VisitGenericName((GenericNameSyntax)n));

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node) =>
        HandleName(node, n => base.VisitQualifiedName((QualifiedNameSyntax)n));

    private SyntaxNode? HandleName(NameSyntax node, Func<NameSyntax, SyntaxNode?> fallback)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(node);
        if (symbolInfo.Symbol != null)
        {
            var symbol = symbolInfo.Symbol;

            if (symbol is ITypeSymbol typeSymbol)
            {
                if (node.Parent is MemberAccessExpressionSyntax mae && mae.Name == node)
                    return fallback(node);

                if (node.Parent is QualifiedNameSyntax qns && qns.Right == node)
                    return fallback(node);

                if (node.Parent is AliasQualifiedNameSyntax aqns && aqns.Name == node)
                    return fallback(node);

                var newName = ReplaceGenericsAndQualify(typeSymbol);
                return SyntaxFactory.ParseTypeName(newName).WithTriviaFrom(node);
            }
            //Qualify implicitly accessed static members ---
            else if (
                symbol.IsStatic
                && (symbol is IMethodSymbol || symbol is IPropertySymbol || symbol is IFieldSymbol)
            )
            {
                // Skip local functions
                if (symbol is IMethodSymbol ms && ms.MethodKind == MethodKind.LocalFunction)
                    return fallback(node);

                // Skip if it is already qualified (e.g. Interface.Length())
                if (node.Parent is MemberAccessExpressionSyntax mae && mae.Name == node)
                    return fallback(node);

                // Skip if it is part of an already qualified name
                if (node.Parent is QualifiedNameSyntax qns && qns.Right == node)
                    return fallback(node);

                // Skip named arguments (e.g. paramName: value)
                if (node.Parent is NameColonSyntax)
                    return fallback(node);

                var containingType = symbol.ContainingType;
                if (containingType != null && node is SimpleNameSyntax)
                {
                    var qualifiedTypeName = ReplaceGenericsAndQualify(containingType);
                    var typeNameSyntax = SyntaxFactory
                        .ParseTypeName(qualifiedTypeName)
                        .WithTriviaFrom(node);

                    // Call the fallback first to ensure generic arguments inside the node itself get substituted
                    var rewrittenNode = (SimpleNameSyntax)fallback(node)!;

                    // Wrap 'Length' into 'global::InterfaceName.Length'
                    return SyntaxFactory
                        .MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            typeNameSyntax,
                            rewrittenNode.WithoutTrivia() // Trivia is shifted to the outer member access
                        )
                        .WithTriviaFrom(node);
                }
            }
        }

        return fallback(node);
    }

    private string ReplaceGenericsAndQualify(ITypeSymbol typeSymbol)
    {
        var parts = typeSymbol.ToDisplayParts(SymbolDisplayFormat.FullyQualifiedFormat);
        var sb = new StringBuilder();

        foreach (var part in parts)
        {
            if (
                part.Symbol is ITypeSymbol ts
                && typeMap.TryGetValue(ts.OriginalDefinition, out var replacement)
            )
            {
                sb.Append(replacement.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
            else
            {
                sb.Append(part.ToString());
            }
        }

        return sb.ToString();
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var methodSymbol = semanticModel.GetDeclaredSymbol(node);
        var wasExplicit = node.ExplicitInterfaceSpecifier != null;

        var rewritten = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        rewritten = rewritten.WithExplicitInterfaceSpecifier(null);

        if (!wasExplicit || methodSymbol is not { TypeParameters.Length: > 0 })
            return FixModifiersAndTrivia(
                rewritten,
                rewritten.Modifiers,
                (n, m) => n.WithModifiers(m),
                n => n.ReturnType,
                (n, t) => n.WithReturnType((TypeSyntax)t)
            );
        var constraints = GetConstraints(methodSymbol);
        if (constraints.Any())
        {
            rewritten = rewritten.WithConstraintClauses(constraints);
        }

        return FixModifiersAndTrivia(
            rewritten,
            rewritten.Modifiers,
            (n, m) => n.WithModifiers(m),
            n => n.ReturnType,
            (n, t) => n.WithReturnType((TypeSyntax)t)
        );
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var rewritten = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
        rewritten = rewritten.WithExplicitInterfaceSpecifier(null);
        return FixModifiersAndTrivia(
            rewritten,
            rewritten.Modifiers,
            (n, m) => n.WithModifiers(m),
            n => n.Type,
            (n, t) => n.WithType((TypeSyntax)t)
        );
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        var rewritten = (IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!;
        rewritten = rewritten.WithExplicitInterfaceSpecifier(null);
        return FixModifiersAndTrivia(
            rewritten,
            rewritten.Modifiers,
            (n, m) => n.WithModifiers(m),
            n => n.Type,
            (n, t) => n.WithType((TypeSyntax)t)
        );
    }

    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        var rewritten = (OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node)!;
        rewritten = rewritten.WithExplicitInterfaceSpecifier(null);
        return FixModifiersAndTrivia(
            rewritten,
            rewritten.Modifiers,
            (n, m) => n.WithModifiers(m),
            n => n.ReturnType,
            (n, t) => n.WithReturnType((TypeSyntax)t)
        );
    }

    public override SyntaxNode? VisitConversionOperatorDeclaration(
        ConversionOperatorDeclarationSyntax node
    )
    {
        var rewritten = (ConversionOperatorDeclarationSyntax)
            base.VisitConversionOperatorDeclaration(node)!;
        rewritten = rewritten.WithExplicitInterfaceSpecifier(null);
        return FixModifiersAndTrivia(
            rewritten,
            rewritten.Modifiers,
            (n, m) => n.WithModifiers(m),
            n => n.Type,
            (n, t) => n.WithType((TypeSyntax)t)
        );
    }

    private SyntaxList<TypeParameterConstraintClauseSyntax> GetConstraints(
        IMethodSymbol methodSymbol
    )
    {
        var clauses = new List<TypeParameterConstraintClauseSyntax>();

        foreach (var typeParam in methodSymbol.TypeParameters)
        {
            var constraints = new List<TypeParameterConstraintSyntax>();

            if (typeParam.HasUnmanagedTypeConstraint)
                constraints.Add(
                    SyntaxFactory.TypeConstraint(SyntaxFactory.IdentifierName("unmanaged"))
                );
            else if (typeParam.HasValueTypeConstraint)
                constraints.Add(SyntaxFactory.ClassOrStructConstraint(SyntaxKind.StructConstraint));
            else if (typeParam.HasReferenceTypeConstraint)
                constraints.Add(SyntaxFactory.ClassOrStructConstraint(SyntaxKind.ClassConstraint));

            if (typeParam.HasNotNullConstraint)
                constraints.Add(
                    SyntaxFactory.TypeConstraint(SyntaxFactory.IdentifierName("notnull"))
                );

            constraints.AddRange(
                typeParam
                    .ConstraintTypes.Select(constraintType =>
                        constraintType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    )
                    .Select(typeName =>
                        SyntaxFactory.TypeConstraint(SyntaxFactory.ParseTypeName(typeName))
                    )
                    .Cast<TypeParameterConstraintSyntax>()
            );

            if (typeParam.HasConstructorConstraint)
                constraints.Add(SyntaxFactory.ConstructorConstraint());

            if (constraints.Count > 0)
            {
                var clause = SyntaxFactory
                    .TypeParameterConstraintClause(SyntaxFactory.IdentifierName(typeParam.Name))
                    .WithConstraints(SyntaxFactory.SeparatedList(constraints))
                    .NormalizeWhitespace()
                    .WithLeadingTrivia(
                        SyntaxFactory.TriviaList(
                            SyntaxFactory.CarriageReturnLineFeed,
                            SyntaxFactory.Whitespace("        ")
                        )
                    );
                clauses.Add(clause);
            }
        }

        return SyntaxFactory.List(clauses);
    }

    private T FixModifiersAndTrivia<T>(
        T node,
        SyntaxTokenList originalModifiers,
        Func<T, SyntaxTokenList, T> withModifiers,
        Func<T, SyntaxNode> getTypeNode,
        Func<T, SyntaxNode, T> withTypeNode
    )
        where T : MemberDeclarationSyntax
    {
        var tokens = originalModifiers
            .Where(m => !m.IsKind(SyntaxKind.VirtualKeyword) && !m.IsKind(SyntaxKind.SealedKeyword))
            .ToList();

        if (isStruct)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].IsKind(SyntaxKind.ProtectedKeyword))
                {
                    tokens[i] = SyntaxFactory
                        .Token(SyntaxKind.PrivateKeyword)
                        .WithTriviaFrom(tokens[i]);
                }
            }

            if (tokens.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)))
            {
                tokens = tokens.Where(m => !m.IsKind(SyntaxKind.InternalKeyword)).ToList();
            }
        }

        if (
            tokens.Any(m =>
                m.IsKind(SyntaxKind.PublicKeyword)
                || m.IsKind(SyntaxKind.PrivateKeyword)
                || m.IsKind(SyntaxKind.ProtectedKeyword)
                || m.IsKind(SyntaxKind.InternalKeyword)
            )
        )
        {
            return withModifiers(node, SyntaxFactory.TokenList(tokens));
        }

        var publicToken = SyntaxFactory.Token(SyntaxKind.PublicKeyword);

        if (tokens.Count > 0)
        {
            publicToken = publicToken
                .WithLeadingTrivia(tokens[0].LeadingTrivia)
                .WithTrailingTrivia(SyntaxFactory.Space);
            tokens[0] = tokens[0].WithLeadingTrivia(SyntaxFactory.TriviaList());
        }
        else
        {
            var typeNode = getTypeNode(node);
            publicToken = publicToken
                .WithLeadingTrivia(typeNode.GetLeadingTrivia())
                .WithTrailingTrivia(SyntaxFactory.Space);
            node = withTypeNode(node, typeNode.WithLeadingTrivia(SyntaxFactory.TriviaList()));
        }

        tokens.Insert(0, publicToken);
        return withModifiers(node, SyntaxFactory.TokenList(tokens));
    }
}
