// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
namespace UnityLlmTransport.Sse
{
    /// <summary>
    /// Local stand-in for the runtime-generated SR resource accessor.
    /// Strings copied verbatim from src/Resources/Strings.resx of the vendored reference source
    /// (netstandard2.1 spike shim: the runtime repo's auto-generated SR.cs is not part of the copied sources).
    /// </summary>
    internal static class SR
    {
        public const string ArgumentException_CannotBeNegative = "The argument cannot be a negative value.";
        public const string ArgumentException_CannotContainLineBreaks = "The argument cannot contain line breaks.";
        public const string InvalidDataException_SseExceededMaxLength = "SSE data exceeds maximum allowed length.";
        public const string InvalidOperation_EnumerateOnlyOnce = "The enumerable may be enumerated only once.";
    }
}
