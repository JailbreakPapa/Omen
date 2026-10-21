using System.Collections.Generic;

namespace Omen.Core.Rules;

/// <summary>
/// The result of <see cref="M:Omen.Core.Rules.RuleCompiler.DiscoverRuleFilesCore(System.String,System.Threading.CancellationToken)" />: every rule file a project's
/// build would compile, split by kind, plus the project definition file(s) found at the root
/// (0 or 1 - see <see cref="T:Omen.Core.Rules.RuleCompiler" />'s ProjectFilePattern) and the parsed
/// <see cref="T:Omen.Core.Rules.OmenProjectRules" /> itself, if any.
/// </summary>
public sealed record RuleFileDiscovery(IReadOnlyList<string> ModuleFiles, IReadOnlyList<string> TargetFiles, IReadOnlyList<string> PluginFiles, IReadOnlyList<string> ProjectFiles, OmenProjectRules? Project);
