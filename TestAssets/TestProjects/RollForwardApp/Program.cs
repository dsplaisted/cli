// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

static class Program
{
    public static void Main()
    {
        Console.WriteLine(typeof(object).Assembly.Location);
        Console.WriteLine(typeof(System.Collections.Immutable.ImmutableList).Assembly.Location);
    }
}