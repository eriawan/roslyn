﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.ExternalAccess.FSharp.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;

namespace Microsoft.CodeAnalysis.ExternalAccess.FSharp.Internal.Diagnostics;

[Shared]
[ExportLanguageService(typeof(FSharpSimplifyNameDiagnosticAnalyzerService), LanguageNames.FSharp)]
internal class FSharpSimplifyNameDiagnosticAnalyzerService : ILanguageService
{
    private readonly IFSharpSimplifyNameDiagnosticAnalyzer _analyzer;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public FSharpSimplifyNameDiagnosticAnalyzerService(IFSharpSimplifyNameDiagnosticAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    public Task<ImmutableArray<Diagnostic>> AnalyzeSemanticsAsync(DiagnosticDescriptor descriptor, Document document, CancellationToken cancellationToken)
    {
        return _analyzer.AnalyzeSemanticsAsync(descriptor, document, cancellationToken);
    }
}

[DiagnosticAnalyzer(LanguageNames.FSharp)]
internal class FSharpSimplifyNameDiagnosticAnalyzer : DocumentDiagnosticAnalyzer, IBuiltInAnalyzer
{
    private readonly DiagnosticDescriptor _descriptor =
        new DiagnosticDescriptor(
                IDEDiagnosticIds.SimplifyNamesDiagnosticId,
                ExternalAccessFSharpResources.SimplifyName,
                ExternalAccessFSharpResources.NameCanBeSimplified,
                DiagnosticCategory.Style, DiagnosticSeverity.Hidden, isEnabledByDefault: true, customTags: FSharpDiagnosticCustomTags.Unnecessary);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_descriptor];

    public bool IsHighPriority => false;

    public override int Priority => 100; // Default = 50

    public override async Task<ImmutableArray<Diagnostic>> AnalyzeSemanticsAsync(TextDocument textDocument, SyntaxTree tree, CancellationToken cancellationToken)
    {
        var analyzer = textDocument.Project.Services.GetService<FSharpSimplifyNameDiagnosticAnalyzerService>();
        return analyzer is null || textDocument is not Document document
            ? []
            : await analyzer.AnalyzeSemanticsAsync(_descriptor, document, cancellationToken).ConfigureAwait(false);
    }

    public DiagnosticAnalyzerCategory GetAnalyzerCategory()
    {
        return DiagnosticAnalyzerCategory.SemanticDocumentAnalysis;
    }
}
