﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.VisualStudio.LanguageServices.Options;

namespace Microsoft.VisualStudio.LanguageServices.CSharp;

internal static class CSharpVisualStudioOptionStorageReadFallbacks
{
    [ExportVisualStudioStorageReadFallback("csharp_space_between_parentheses"), Shared]
    internal sealed class SpaceBetweenFarentheses : IVisualStudioStorageReadFallback
    {
        private static ImmutableArray<(string key, int flag)> s_storages => ImmutableArray.Create(
            ("TextEditor.CSharp.Specific.SpaceWithinExpressionParentheses", (int)SpacePlacementWithinParentheses.Expressions),
            ("TextEditor.CSharp.Specific.SpaceWithinCastParentheses", (int)SpacePlacementWithinParentheses.Expressions),
            ("TextEditor.CSharp.Specific.SpaceWithinOtherParentheses", (int)SpacePlacementWithinParentheses.ControlFlowStatements));

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public SpaceBetweenFarentheses()
        {
        }

        public Optional<object?> TryRead(string? language, TryReadValueDelegate readValue)
            => TryReadFlags(s_storages, readValue, out var intValue) ? (SpacePlacementWithinParentheses)intValue : default;
    }

    [ExportVisualStudioStorageReadFallback("csharp_new_line_before_open_brace"), Shared]
    internal sealed class NewLinesForBraces : IVisualStudioStorageReadFallback
    {
        private static ImmutableArray<(string key, int flag)> s_storages => ImmutableArray.Create(
            ("TextEditor.CSharp.Specific.NewLinesForBracesInTypes", (int)NewLineBeforeOpenBracePlacement.Types),
            ("TextEditor.CSharp.Specific.NewLinesForBracesInAnonymousTypes", (int)NewLineBeforeOpenBracePlacement.AnonymousTypes),
            ("TextEditor.CSharp.Specific.NewLinesForBracesInObjectCollectionArrayInitializers", (int)NewLineBeforeOpenBracePlacement.ObjectCollectionArrayInitializers),
            ("TextEditor.CSharp.Specific.NewLinesForBracesInProperties", (int)NewLineBeforeOpenBracePlacement.Properties),
            ("TextEditor.CSharp.Specific.NewLinesForBracesInMethods", (int)NewLineBeforeOpenBracePlacement.Methods),
            ("TextEditor.CSharp.Specific.NewLinesForBracesInAccessors", (int)NewLineBeforeOpenBracePlacement.Accessors),
            ("TextEditor.CSharp.Specific.NewLinesForBracesInAnonymousMethods", (int)NewLineBeforeOpenBracePlacement.AnonymousMethods),
            ("TextEditor.CSharp.Specific.NewLinesForBracesInLambdaExpressionBody", (int)NewLineBeforeOpenBracePlacement.LambdaExpressionBody),
            ("TextEditor.CSharp.Specific.NewLinesForBracesInControlBlocks", (int)NewLineBeforeOpenBracePlacement.ControlBlocks));

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public NewLinesForBraces()
        {
        }

        public Optional<object?> TryRead(string? language, TryReadValueDelegate readValue)
            => TryReadFlags(s_storages, readValue, out var intValue) ? (NewLineBeforeOpenBracePlacement)intValue : default;
    }

    private static bool TryReadFlags(ImmutableArray<(string key, int flag)> storages, TryReadValueDelegate read, out int result)
    {
        var hasAnyFlag = false;
        result = 0;
        foreach (var (key, flag) in storages)
        {
            var value = read(key, typeof(bool));
            if (value.HasValue)
            {
                if ((bool)value.Value!)
                {
                    result |= flag;
                }

                hasAnyFlag = true;
            }
        }

        return hasAnyFlag;
    }
}
