// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnityLlmTransport.Sse
{
    /// <summary>
    /// netstandard2.1 polyfill: the netstandard2.1 API surface does not expose
    /// <see cref="Array.MaxLength"/> (it was added to the BCL in .NET Core 3.0).
    /// The constant matches the .NET runtime value (0x7FFFFFC7 = 2147483591).
    /// </summary>
    internal static class ArrayMaxLengthShim
    {
        public const int MaxLength = 0x7FFFFFC7;
    }
}
