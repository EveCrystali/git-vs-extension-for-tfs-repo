using System;

namespace GitForTfs
{
    /// <summary>
    /// GUIDs shared between the C# package and the <c>GitForTfsPackage.vsct</c> command table. The string values MUST
    /// stay in sync with the &lt;Symbols&gt; section of the .vsct file.
    /// </summary>
    internal static class PackageGuids
    {
        public const string PackageString = "b7f6d2a4-3c81-4e57-9a0b-1f2e3d4c5b60";
        public const string CommandSetString = "b7f6d2a4-3c81-4e57-9a0b-1f2e3d4c5b61";
        public const string ToolWindowString = "b7f6d2a4-3c81-4e57-9a0b-1f2e3d4c5b62";

        public static readonly Guid CommandSet = new Guid(CommandSetString);
    }

    /// <summary>Command IDs, matching the &lt;IDSymbol&gt; entries in the .vsct file.</summary>
    internal static class PackageIds
    {
        public const int GitMenuGroup = 0x1020;
        public const int SolutionExplorerGroup = 0x1030;
        public const int OpenToolWindowCommand = 0x0100;
        public const int FileHistoryCommand = 0x0200;
    }
}