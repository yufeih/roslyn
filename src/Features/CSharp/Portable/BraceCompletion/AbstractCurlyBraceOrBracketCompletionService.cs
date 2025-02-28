﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.BraceCompletion;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Formatting.Rules;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Indentation;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.BraceCompletion
{
    internal abstract class AbstractCurlyBraceOrBracketCompletionService : AbstractCSharpBraceCompletionService
    {
        /// <summary>
        /// Annotation used to find the closing brace location after formatting changes are applied.
        /// The closing brace location is then used as the caret location.
        /// </summary>
        private static readonly SyntaxAnnotation s_closingBraceSyntaxAnnotation = new(nameof(s_closingBraceSyntaxAnnotation));

        protected abstract ImmutableArray<AbstractFormattingRule> GetBraceFormattingIndentationRulesAfterReturn(IndentationOptions options);

        protected abstract int AdjustFormattingEndPoint(ParsedDocument document, int startPoint, int endPoint);

        public sealed override BraceCompletionResult? GetTextChangesAfterCompletion(BraceCompletionContext context, IndentationOptions options, CancellationToken cancellationToken)
        {
            // After the closing brace is completed we need to format the span from the opening point to the closing point.
            // E.g. when the user triggers completion for an if statement ($$ is the caret location) we insert braces to get
            // if (true){$$}
            // We then need to format this to
            // if (true) { $$}

            if (!options.AutoFormattingOptions.FormatOnCloseBrace)
            {
                return null;
            }

            var (formattingChanges, finalCurlyBraceEnd) = FormatTrackingSpan(
                context.Document,
                context.OpeningPoint,
                context.ClosingPoint,
                // We're not trying to format the indented block here, so no need to pass in additional rules.
                braceFormattingIndentationRules: ImmutableArray<AbstractFormattingRule>.Empty,
                options,
                cancellationToken);

            if (formattingChanges.IsEmpty)
            {
                return null;
            }

            // The caret location should be at the start of the closing brace character.
            var formattedText = context.Document.Text.WithChanges(formattingChanges);
            var caretLocation = formattedText.Lines.GetLinePosition(finalCurlyBraceEnd - 1);

            return new BraceCompletionResult(formattingChanges, caretLocation);
        }

        private static bool ContainsOnlyWhitespace(SourceText text, int openingPosition, int closingBraceEndPoint)
        {
            // Set the start point to the character after the opening brace.
            var start = openingPosition + 1;
            // Set the end point to the closing brace start character position.
            var end = closingBraceEndPoint - 1;

            for (var i = start; i < end; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public sealed override BraceCompletionResult? GetTextChangeAfterReturn(
            BraceCompletionContext context,
            IndentationOptions options,
            CancellationToken cancellationToken)
        {
            var document = context.Document;
            var closingPoint = context.ClosingPoint;
            var openingPoint = context.OpeningPoint;
            var originalDocumentText = document.Text;

            // check whether shape of the braces are what we support
            // shape must be either "{|}" or "{ }". | is where caret is. otherwise, we don't do any special behavior
            if (!ContainsOnlyWhitespace(originalDocumentText, openingPoint, closingPoint))
            {
                return null;
            }

            var openingPointLine = originalDocumentText.Lines.GetLineFromPosition(openingPoint).LineNumber;
            var closingPointLine = originalDocumentText.Lines.GetLineFromPosition(closingPoint).LineNumber;

            // If there are already multiple empty lines between the braces, don't do anything.
            // We need to allow a single empty line between the braces to account for razor scenarios where they insert a line.
            if (closingPointLine - openingPointLine > 2)
            {
                return null;
            }

            // If there is not already an empty line inserted between the braces, insert one.
            TextChange? newLineEdit = null;
            var textToFormat = originalDocumentText;
            if (closingPointLine - openingPointLine == 1)
            {
                var newLineString = options.FormattingOptions.NewLine;
                newLineEdit = new TextChange(new TextSpan(closingPoint - 1, 0), newLineString);
                textToFormat = originalDocumentText.WithChanges(newLineEdit.Value);

                // Modify the closing point location to adjust for the newly inserted line.
                closingPoint += newLineString.Length;
            }

            var documentToFormat = document.WithChangedText(textToFormat, cancellationToken);

            // Format the text that contains the newly inserted line.
            var (formattingChanges, newClosingPoint) = FormatTrackingSpan(
                documentToFormat,
                openingPoint,
                closingPoint,
                braceFormattingIndentationRules: GetBraceFormattingIndentationRulesAfterReturn(options),
                options,
                cancellationToken);

            closingPoint = newClosingPoint;
            var formattedText = textToFormat.WithChanges(formattingChanges);

            // Get the empty line between the curly braces.
            var desiredCaretLine = GetLineBetweenCurlys(closingPoint, formattedText);
            Debug.Assert(desiredCaretLine.GetFirstNonWhitespacePosition() == null, "the line between the formatted braces is not empty");

            // Set the caret position to the properly indented column in the desired line.
            var newDocument = document.WithChangedText(formattedText, cancellationToken);
            var caretPosition = GetIndentedLinePosition(newDocument, newDocument.Text, desiredCaretLine.LineNumber, options, cancellationToken);

            // The new line edit is calculated against the original text, d0, to get text d1.
            // The formatting edits are calculated against d1 to get text d2.
            // Merge the formatting and new line edits into a set of whitespace only text edits that all apply to d0.
            var overallChanges = newLineEdit != null ? GetMergedChanges(newLineEdit.Value, formattingChanges, formattedText) : formattingChanges;
            return new BraceCompletionResult(overallChanges, caretPosition);

            static TextLine GetLineBetweenCurlys(int closingPosition, SourceText text)
            {
                var closingBraceLineNumber = text.Lines.GetLineFromPosition(closingPosition - 1).LineNumber;
                return text.Lines[closingBraceLineNumber - 1];
            }

            static LinePosition GetIndentedLinePosition(ParsedDocument document, SourceText sourceText, int lineNumber, IndentationOptions options, CancellationToken cancellationToken)
            {
                var indentationService = document.LanguageServices.GetRequiredService<IIndentationService>();
                var indentation = indentationService.GetIndentation(document, lineNumber, options, cancellationToken);

                var baseLinePosition = sourceText.Lines.GetLinePosition(indentation.BasePosition);
                var offsetOfBacePosition = baseLinePosition.Character;
                var totalOffset = offsetOfBacePosition + indentation.Offset;
                var indentedLinePosition = new LinePosition(lineNumber, totalOffset);
                return indentedLinePosition;
            }

            static ImmutableArray<TextChange> GetMergedChanges(TextChange newLineEdit, ImmutableArray<TextChange> formattingChanges, SourceText formattedText)
            {
                var newRanges = TextChangeRangeExtensions.Merge(
                    ImmutableArray.Create(newLineEdit.ToTextChangeRange()),
                    formattingChanges.SelectAsArray(f => f.ToTextChangeRange()));

                using var _ = ArrayBuilder<TextChange>.GetInstance(out var mergedChanges);
                var amountToShift = 0;
                foreach (var newRange in newRanges)
                {
                    var newTextChangeSpan = newRange.Span;
                    // Get the text to put in the text change by looking at the span in the formatted text.
                    // As the new range start is relative to the original text, we need to adjust it assuming the previous changes were applied
                    // to get the correct start location in the formatted text.
                    // E.g. with changes
                    //     1. Insert "hello" at 2
                    //     2. Insert "goodbye" at 3
                    // "goodbye" is after "hello" at location 3 + 5 (length of "hello") in the new text.
                    var newTextChangeText = formattedText.GetSubText(new TextSpan(newRange.Span.Start + amountToShift, newRange.NewLength)).ToString();
                    amountToShift += (newRange.NewLength - newRange.Span.Length);
                    mergedChanges.Add(new TextChange(newTextChangeSpan, newTextChangeText));
                }

                return mergedChanges.ToImmutable();
            }
        }

        /// <summary>
        /// Formats the span between the opening and closing points, options permitting.
        /// Returns the text changes that should be applied to the input document to 
        /// get the formatted text and the end of the close curly brace in the formatted text.
        /// </summary>
        private (ImmutableArray<TextChange> textChanges, int finalBraceEnd) FormatTrackingSpan(
            ParsedDocument document,
            int openingPoint,
            int closingPoint,
            ImmutableArray<AbstractFormattingRule> braceFormattingIndentationRules,
            IndentationOptions options,
            CancellationToken cancellationToken)
        {
            var startPoint = openingPoint;
            var endPoint = AdjustFormattingEndPoint(document, startPoint, closingPoint);

            if (options.IndentStyle == FormattingOptions2.IndentStyle.Smart)
            {
                // Set the formatting start point to be the beginning of the first word to the left 
                // of the opening brace location.
                // skip whitespace
                while (startPoint >= 0 && char.IsWhiteSpace(document.Text[startPoint]))
                {
                    startPoint--;
                }

                // skip tokens in the first word to the left.
                startPoint--;
                while (startPoint >= 0 && !char.IsWhiteSpace(document.Text[startPoint]))
                {
                    startPoint--;
                }
            }

            var spanToFormat = TextSpan.FromBounds(Math.Max(startPoint, 0), endPoint);
            var rules = FormattingRuleUtilities.GetFormattingRules(document, spanToFormat, braceFormattingIndentationRules);

            // Annotate the original closing brace so we can find it after formatting.
            var annotatedRoot = GetSyntaxRootWithAnnotatedClosingBrace(document.Root, closingPoint);

            var result = Formatter.GetFormattingResult(
                annotatedRoot, SpecializedCollections.SingletonEnumerable(spanToFormat), document.SolutionServices, options.FormattingOptions, rules, cancellationToken);

            if (result == null)
            {
                return (ImmutableArray<TextChange>.Empty, closingPoint);
            }

            var newRoot = result.GetFormattedRoot(cancellationToken);
            var newClosingPoint = newRoot.GetAnnotatedTokens(s_closingBraceSyntaxAnnotation).Single().SpanStart + 1;

            var textChanges = result.GetTextChanges(cancellationToken).ToImmutableArray();
            return (textChanges, newClosingPoint);

            SyntaxNode GetSyntaxRootWithAnnotatedClosingBrace(SyntaxNode originalRoot, int closingBraceEndPoint)
            {
                var closeBraceToken = originalRoot.FindToken(closingBraceEndPoint - 1);
                Debug.Assert(IsValidClosingBraceToken(closeBraceToken));

                var newCloseBraceToken = closeBraceToken.WithAdditionalAnnotations(s_closingBraceSyntaxAnnotation);
                return originalRoot.ReplaceToken(closeBraceToken, newCloseBraceToken);
            }
        }
    }
}
