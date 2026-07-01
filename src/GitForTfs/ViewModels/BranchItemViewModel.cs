using GitForTfs.Mvvm;
using GitForTfs.Services;

namespace GitForTfs.ViewModels
{
    /// <summary>Row in the "Branches" list.</summary>
    public sealed class BranchItemViewModel : ViewModelBase
    {
        public BranchItemViewModel(GitBranch branch)
        {
            Branch = branch;
        }

        public GitBranch Branch { get; }

        public string Name => Branch.Name;

        public bool IsCurrent => Branch.IsCurrent;

        public string Upstream => Branch.Upstream;

        public bool HasUpstream => !string.IsNullOrEmpty(Branch.Upstream);

        public string TrackingText
        {
            get
            {
                if (!HasUpstream)
                    return "(no upstream)";

                if (Branch.Ahead == 0 && Branch.Behind == 0)
                    return Branch.Upstream;

                var parts = new System.Collections.Generic.List<string>();
                if (Branch.Ahead > 0)
                    parts.Add($"↑{Branch.Ahead}");
                if (Branch.Behind > 0)
                    parts.Add($"↓{Branch.Behind}");

                return $"{Branch.Upstream}  {string.Join(" ", parts)}";
            }
        }
    }
}
