﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests.RequestOrdering;

[ExportCSharpVisualBasicStatelessLspService(typeof(FailingRequestHandler)), PartNotDiscoverable, Shared]
[Method(MethodName)]
internal sealed class FailingRequestHandler : ILspServiceRequestHandler<TestRequest, TestResponse>
{
    public const string MethodName = nameof(FailingRequestHandler);
    private const int Delay = 100;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public FailingRequestHandler()
    {
    }

    public bool MutatesSolutionState => false;
    public bool RequiresLSPSolution => true;

    public async Task<TestResponse> HandleRequestAsync(TestRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);

        throw new InvalidOperationException();
    }
}
