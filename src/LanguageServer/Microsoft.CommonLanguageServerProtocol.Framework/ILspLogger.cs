﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// This is consumed as 'generated' code in a source package and therefore requires an explicit nullable enable
#nullable enable

using System;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

internal interface ILspLogger
{
    IDisposable? CreateContext(string context);
    IDisposable? CreateLanguageContext(string? language);

    void LogDebug(string message, params object[] @params);
    void LogInformation(string message, params object[] @params);
    void LogWarning(string message, params object[] @params);
    void LogError(string message, params object[] @params);
    void LogException(Exception exception, string? message = null, params object[] @params);
}
