﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.BackgroundWorkIndicator;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Notification;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.ExtractMethod;

[Export(typeof(ICommandHandler))]
[ContentType(ContentTypeNames.CSharpContentType)]
[ContentType(ContentTypeNames.VisualBasicContentType)]
[Name(PredefinedCommandHandlerNames.ExtractMethod)]
[Order(After = PredefinedCommandHandlerNames.DocumentationComments)]
internal sealed class ExtractMethodCommandHandler : ICommandHandler<ExtractMethodCommandArgs>
{
    private readonly IThreadingContext _threadingContext;
    private readonly ITextBufferUndoManagerProvider _undoManager;
    private readonly IInlineRenameService _renameService;
    private readonly IAsynchronousOperationListener _asyncListener;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public ExtractMethodCommandHandler(
        IThreadingContext threadingContext,
        ITextBufferUndoManagerProvider undoManager,
        IInlineRenameService renameService,
        IAsynchronousOperationListenerProvider asyncListenerProvider)
    {
        Contract.ThrowIfNull(threadingContext);
        Contract.ThrowIfNull(undoManager);
        Contract.ThrowIfNull(renameService);

        _threadingContext = threadingContext;
        _undoManager = undoManager;
        _renameService = renameService;
        _asyncListener = asyncListenerProvider.GetListener(FeatureAttribute.ExtractMethod);
    }

    public string DisplayName => EditorFeaturesResources.Extract_Method;

    public CommandState GetCommandState(ExtractMethodCommandArgs args)
    {
        var spans = args.TextView.Selection.GetSnapshotSpansOnBuffer(args.SubjectBuffer);
        if (spans.Count(s => s.Length > 0) != 1)
        {
            return CommandState.Unspecified;
        }

        if (!args.SubjectBuffer.TryGetWorkspace(out var workspace) ||
            !workspace.CanApplyChange(ApplyChangesKind.ChangeDocument) ||
            !args.SubjectBuffer.SupportsRefactorings())
        {
            return CommandState.Unspecified;
        }

        return CommandState.Available;
    }

    public bool ExecuteCommand(ExtractMethodCommandArgs args, CommandExecutionContext context)
    {
        // Finish any rename that had been started. We'll do this here before we enter the
        // wait indicator for Extract Method
        if (_renameService.ActiveSession != null)
        {
            _threadingContext.JoinableTaskFactory.Run(() => _renameService.ActiveSession.CommitAsync(previewChanges: false, context.OperationContext));
        }

        if (!args.SubjectBuffer.SupportsRefactorings())
            return false;

        var view = args.TextView;
        var textBuffer = args.SubjectBuffer;
        var spans = view.Selection.GetSnapshotSpansOnBuffer(textBuffer).Where(s => s.Length > 0).ToList();
        if (spans.Count != 1)
            return false;

        var span = spans[0];

        var document = args.SubjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
        if (document is null)
            return false;

        _ = ExecuteAsync(view, textBuffer, document, span);
        return true;
    }

    private async Task ExecuteAsync(
        ITextView view,
        ITextBuffer textBuffer,
        Document document,
        SnapshotSpan span)
    {
        _threadingContext.ThrowIfNotOnUIThread();
        var indicatorFactory = document.Project.Solution.Services.GetRequiredService<IBackgroundWorkIndicatorFactory>();

        // Note: we do not want to cancel on 'focus lost'.  That's because extract-method may show the user a
        // notification dialog about proceeding or not.  We don't want the act of showing them a dialog about proceeding
        // to then cause the whole operation to then fail.
        using var indicatorContext = indicatorFactory.Create(
            view, span, EditorFeaturesResources.Applying_Extract_Method_refactoring, cancelOnEdit: true, cancelOnFocusLost: false);

        using var asyncToken = _asyncListener.BeginAsyncOperation(nameof(ExecuteCommand));
        await ExecuteWorkerAsync(view, textBuffer, span.Span.ToTextSpan(), indicatorContext).ConfigureAwait(false);
    }

    private async Task ExecuteWorkerAsync(
        ITextView view,
        ITextBuffer textBuffer,
        TextSpan span,
        IBackgroundWorkIndicatorContext waitContext)
    {
        _threadingContext.ThrowIfNotOnUIThread();

        var cancellationToken = waitContext.UserCancellationToken;

        var document = await textBuffer.CurrentSnapshot.GetFullyLoadedOpenDocumentInCurrentContextWithChangesAsync(waitContext).ConfigureAwait(false);
        if (document is null)
            return;

        var options = await document.GetExtractMethodGenerationOptionsAsync(cancellationToken).ConfigureAwait(false);

        var result = await ExtractMethodService.ExtractMethodAsync(document, span, localFunction: false, options, cancellationToken).ConfigureAwait(false);

        if (!Succeeded(result))
        {
            // Extract method didn't succeed.  Or succeeded, but had some reasons to notify the user about.  See if
            // extracting a local function would be better..

            var localFunctionResult = await ExtractMethodService.ExtractMethodAsync(document, span, localFunction: true, options, cancellationToken).ConfigureAwait(false);
            if (Succeeded(localFunctionResult))
            {
                // Extract local function completely succeeded.  Use that instead.
                result = localFunctionResult;
            }
            else if (!result.Succeeded && localFunctionResult.Succeeded)
            {
                // Extract method entirely failed.  But extract local function was able to proceed, albeit with reasons
                // to notify the user about.  Continue one with extract local function instead.
                result = localFunctionResult;
            }
            else
            {
                // Extract local function was just as bad as extract method.  Just report the extract method issues below.
            }
        }

        Contract.ThrowIfNull(result);

        result = await NotifyUserIfNecessaryAsync(document, result, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return;

        var (formattedDocument, methodNameAtInvocation) = await result.GetDocumentAsync(cancellationToken).ConfigureAwait(false);
        var changes = await formattedDocument.GetTextChangesAsync(document, cancellationToken).ConfigureAwait(false);

        await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        await ApplyChangeAsync(textBuffer, changes, waitContext).ConfigureAwait(true);

        if (methodNameAtInvocation != null)
        {
            // start inline rename to allow the user to change the name if they want.
            var textSnapshot = textBuffer.CurrentSnapshot;
            document = textSnapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document != null)
                _renameService.StartInlineSession(document, methodNameAtInvocation.Value.Span, cancellationToken);

            // select invocation span
            view.TryMoveCaretToAndEnsureVisible(new SnapshotPoint(textSnapshot, methodNameAtInvocation.Value.Span.End));
            view.SetSelection(methodNameAtInvocation.Value.Span.ToSnapshotSpan(textSnapshot));
        }
    }

    private async Task ApplyChangeAsync(
        ITextBuffer textBuffer, IEnumerable<TextChange> changes, IBackgroundWorkIndicatorContext waitContext)
    {
        await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(waitContext.AllowCancellation);

        using var undoTransaction = _undoManager.GetTextBufferUndoManager(textBuffer).TextBufferUndoHistory.CreateTransaction("Extract Method");

        // We're about to make an edit ourselves.  so disable the cancellation that happens on editing.
        var disposable = await waitContext.SuppressAutoCancelAsync().ConfigureAwait(true);
        await using var _ = disposable.ConfigureAwait(true);

        textBuffer.ApplyChanges(changes);

        // apply changes
        undoTransaction.Complete();
    }

    private static bool Succeeded(ExtractMethodResult result)
        => result is { Succeeded: true, Reasons.Length: 0 };

    private async Task<ExtractMethodResult?> NotifyUserIfNecessaryAsync(
        Document document, ExtractMethodResult result, CancellationToken cancellationToken)
    {
        // If we succeeded without any problems, just proceed without notifying the user.
        if (Succeeded(result))
            return result;

        // We have some sort of issue.  See what the user wants to do.  If we have no way to inform the user bail
        // out rather than doing something wrong.
        var notificationService = document.Project.Solution.Services.GetService<INotificationService>();
        if (notificationService is null)
            return null;

        // We're about to show an notification to the user.  Switch to the ui thread to do so.
        await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // The alternative approach wasn't better.  If we failed, just let the user know and bail out.  Otherwise,
        // if we succeeded with messages, tell the user and let them decide if they want to proceed or not.
        if (!result.Succeeded)
        {
            notificationService.SendNotification(
                EditorFeaturesResources.Extract_method_encountered_the_following_issues + Environment.NewLine +
                string.Join("", result.Reasons.Select(r => Environment.NewLine + "  " + r)),
                title: EditorFeaturesResources.Extract_Method,
                severity: NotificationSeverity.Error);

            return null;
        }

        // We succeed and have a not null document.  We must them have some issues to report to the user (otherwise
        // we would have fast returned at the top of this method).  Tell the user about them and let them decide
        // what they want to do.
        Contract.ThrowIfTrue(result.Reasons.Length == 0);

        if (!notificationService.ConfirmMessageBox(
                EditorFeaturesResources.Extract_method_encountered_the_following_issues + Environment.NewLine +
                string.Join("", result.Reasons.Select(r => Environment.NewLine + "  " + r)) + Environment.NewLine + Environment.NewLine +
                EditorFeaturesResources.Do_you_still_want_to_proceed_This_may_produce_broken_code,
                title: EditorFeaturesResources.Extract_Method,
                severity: NotificationSeverity.Warning))
        {
            return null;
        }

        return result;
    }
}
